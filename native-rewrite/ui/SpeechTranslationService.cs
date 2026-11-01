using System.Text.RegularExpressions;
using System.Threading.Channels;
using NAudio.Wave;
using SherpaOnnx;

namespace DoubaoVR.Desktop;

internal sealed class SpeechTranslationService : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WaveInEvent? waveIn;
    private VoiceActivityDetector? vad;
    private OfflineRecognizer? recognizer;
    private Channel<float[]>? audioQueue;
    private Task? processingTask;
    private CancellationTokenSource? cts;

    private bool isListening;
    private string targetLanguage = "en";
    private bool sendBilingual = true;
    private bool sendSound = true;
    private Action<string, string>? onSpeechProcessed;
    private Action<string>? onStatusChanged;
    private string? activeMicName;
    private bool speechActiveInCurrentUtterance;

    public bool IsListening => isListening;

    public void Configure(string language, bool bilingual, bool sound, Action<string, string>? onProcessed = null, Action<string>? onStatus = null)
    {
        targetLanguage = language;
        sendBilingual = bilingual;
        sendSound = sound;
        if (onProcessed != null) onSpeechProcessed = onProcessed;
        if (onStatus != null) onStatusChanged = onStatus;
    }

    public async Task<bool> StartAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (isListening) return true;

            // 1. Ensure SenseVoice and Silero VAD models are downloaded and verified
            try
            {
                await SpeechModelStore.EnsureModelsAsync(msg => onStatusChanged?.Invoke(msg));
            }
            catch (Exception exModel)
            {
                Log.Write($"[SpeechTranslation] Model download/verify error: {exModel.Message}");
                onStatusChanged?.Invoke($"语音模型准备失败: {exModel.Message}");
                return false;
            }

            // 2. Discover microphone device safely off UI thread
            activeMicName = "默认麦克风";
            int selectedDeviceNumber = -1; // -1 = WAVE_MAPPER (system default recording device)
            try
            {
                var mics = await Task.Run(() =>
                {
                    try { return AudioService.Devices(AudioFlow.Microphone); }
                    catch { return new List<AudioDevice>(); }
                });

                var defMic = mics.FirstOrDefault(d => (d.DefaultRoles & 1) != 0) ?? mics.FirstOrDefault();
                if (defMic != null)
                {
                    activeMicName = defMic.Name;
                    int count = WaveInEvent.DeviceCount;
                    for (int i = 0; i < count; i++)
                    {
                        var caps = WaveInEvent.GetCapabilities(i);
                        if (defMic.Name.Contains(caps.ProductName, StringComparison.OrdinalIgnoreCase) ||
                            caps.ProductName.Contains(defMic.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedDeviceNumber = i;
                            break;
                        }
                    }
                }
            }
            catch (Exception exEnum)
            {
                Log.Write($"[SpeechTranslation] Microphone query warning: {exEnum.Message}");
            }

            Log.Write($"[SpeechTranslation] Selected recording device: '{activeMicName}' (DeviceNumber={selectedDeviceNumber})");

            // 3. Initialize Silero VAD
            var vadConfig = new VadModelConfig();
            vadConfig.SileroVad.Model = SpeechModelStore.VadPath;
            vadConfig.SileroVad.Threshold = 0.5f;
            vadConfig.SileroVad.MinSilenceDuration = 0.5f;
            vadConfig.SileroVad.MinSpeechDuration = 0.25f;
            vadConfig.SileroVad.WindowSize = 512;
            vadConfig.SampleRate = 16000;
            vad = new VoiceActivityDetector(vadConfig, 30.0f);

            // 4. Initialize SenseVoice OfflineRecognizer
            var recConfig = new OfflineRecognizerConfig();
            recConfig.ModelConfig.SenseVoice.Model = SpeechModelStore.ModelPath;
            recConfig.ModelConfig.SenseVoice.Language = "auto";
            recConfig.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
            recConfig.ModelConfig.Tokens = SpeechModelStore.TokensPath;
            recConfig.ModelConfig.NumThreads = 2;
            recConfig.ModelConfig.Provider = "cpu";
            recognizer = new OfflineRecognizer(recConfig);

            // 5. Initialize WaveInEvent (Win32 winmm - zero COM, zero CsWinRT E_NOINTERFACE issues)
            waveIn = new WaveInEvent
            {
                DeviceNumber = selectedDeviceNumber,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 20
            };

            // 6. Start speech processing worker channel
            cts = new CancellationTokenSource();
            audioQueue = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            processingTask = Task.Run(() => ProcessSpeechQueueAsync(audioQueue.Reader, cts.Token));

            // 7. Subscribe to audio events and start recording
            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += OnRecordingStopped;
            waveIn.StartRecording();

            isListening = true;
            speechActiveInCurrentUtterance = false;

            string listenPrompt = string.IsNullOrWhiteSpace(activeMicName)
                ? "正在监听麦克风（SenseVoice 离线）…"
                : $"正在监听麦克风（{activeMicName}，SenseVoice 离线）…";
            onStatusChanged?.Invoke(listenPrompt);
            Log.Write("[SpeechTranslation] SenseVoice speech recognition started successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"[SpeechTranslation] Start failed: {ex.Message}");
            onStatusChanged?.Invoke($"麦克风识别启动失败: {ex.Message}");
            await StopInternalAsync(notifyStopped: false);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            await StopInternalAsync(notifyStopped: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StopInternalAsync(bool notifyStopped = true)
    {
        isListening = false;
        speechActiveInCurrentUtterance = false;

        if (waveIn != null)
        {
            try
            {
                waveIn.DataAvailable -= OnDataAvailable;
                waveIn.RecordingStopped -= OnRecordingStopped;
                waveIn.StopRecording();
            }
            catch { }
            waveIn.Dispose();
            waveIn = null;
        }

        if (cts != null)
        {
            cts.Cancel();
        }

        if (audioQueue != null)
        {
            audioQueue.Writer.TryComplete();
        }

        if (processingTask != null)
        {
            try
            {
                await processingTask;
            }
            catch { }
            processingTask = null;
        }

        cts?.Dispose();
        cts = null;
        audioQueue = null;

        if (vad != null)
        {
            try { vad.Dispose(); } catch { }
            vad = null;
        }

        if (recognizer != null)
        {
            try { recognizer.Dispose(); } catch { }
            recognizer = null;
        }

        _ = VrchatOsc.SendTypingAsync(false);

        if (notifyStopped)
        {
            onStatusChanged?.Invoke("已停止监听");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!isListening || vad == null || e.BytesRecorded <= 0) return;

        try
        {
            int sampleCount = e.BytesRecorded / 2;
            if (sampleCount <= 0) return;

            float[] samples16k = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                samples16k[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768.0f;
            }

            // Feed samples in chunks of 512 to Silero VAD
            const int vadChunkSize = 512;
            for (int offset = 0; offset < samples16k.Length; offset += vadChunkSize)
            {
                int count = Math.Min(vadChunkSize, samples16k.Length - offset);
                float[] chunk = new float[count];
                Array.Copy(samples16k, offset, chunk, 0, count);

                vad.AcceptWaveform(chunk);

                if (vad.IsSpeechDetected())
                {
                    if (!speechActiveInCurrentUtterance)
                    {
                        speechActiveInCurrentUtterance = true;
                        _ = VrchatOsc.SendTypingAsync(true);
                        string micLabel = string.IsNullOrWhiteSpace(activeMicName) ? "麦克风" : activeMicName;
                        onStatusChanged?.Invoke($"检测到语音输入（{micLabel}），正在识别…");
                    }
                }

                while (!vad.IsEmpty())
                {
                    var segment = vad.Front();
                    float[] speechSamples = segment.Samples;
                    vad.Pop();
                    speechActiveInCurrentUtterance = false;

                    if (speechSamples.Length > 0 && audioQueue != null)
                    {
                        audioQueue.Writer.TryWrite(speechSamples);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[SpeechTranslation] OnDataAvailable error: {ex.Message}");
        }
    }

    private async Task ProcessSpeechQueueAsync(ChannelReader<float[]> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var samples))
                {
                    ct.ThrowIfCancellationRequested();
                    if (recognizer == null || samples.Length == 0) continue;

                    string cleanText = "";
                    try
                    {
                        using var stream = recognizer.CreateStream();
                        stream.AcceptWaveform(16000, samples);
                        recognizer.Decode(stream);
                        string raw = stream.Result.Text;
                        cleanText = Regex.Replace(raw, @"<\|[^>]*\|>", "").Trim();
                    }
                    catch (Exception exDec)
                    {
                        Log.Write($"[SpeechTranslation] SenseVoice decode error: {exDec.Message}");
                    }

                    if (string.IsNullOrWhiteSpace(cleanText))
                    {
                        _ = VrchatOsc.SendTypingAsync(false);
                        continue;
                    }

                    Log.Write($"[SpeechTranslation] SenseVoice recognized: '{cleanText}'");

                    try
                    {
                        string translated = await TranslationClient.TranslateAsync(cleanText, targetLanguage);
                        if (string.IsNullOrWhiteSpace(translated))
                        {
                            translated = cleanText;
                        }

                        string finalChat = sendBilingual && !string.Equals(translated, cleanText, StringComparison.OrdinalIgnoreCase)
                            ? $"{translated} ({cleanText})"
                            : translated;

                        await VrchatOsc.SendChatboxAsync(finalChat, direct: true, notify: sendSound);
                        onSpeechProcessed?.Invoke(cleanText, translated);
                        onStatusChanged?.Invoke($"已发送: {finalChat}");
                    }
                    catch (Exception exTrans)
                    {
                        Log.Write($"[SpeechTranslation] Translation/OSC send error: {exTrans.Message}");
                    }
                    finally
                    {
                        _ = VrchatOsc.SendTypingAsync(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write($"[SpeechTranslation] ProcessSpeechQueueAsync fatal error: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Log.Write($"[SpeechTranslation] Recording stopped with error: {e.Exception.Message}");
            onStatusChanged?.Invoke($"麦克风录音中断: {e.Exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        gate.Dispose();
    }
}
