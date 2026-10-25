using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DoubaoVR.Desktop;

internal sealed record AssistantHostRequest(string Action, ulong Window = 0, uint ProcessId = 0, ulong ProcessStarted = 0,
    bool Vr = false, bool Activate = false, bool Preview = false, ulong AfterPreview = 0, ulong AfterImage = 0, bool Border = false, float Width = .7f, uint Eye = 0, bool ResetOnDashboard = false);

internal sealed record AssistantHostReply(uint CaptureState = 0, uint InteractionState = 0, string Message = "", string Error = "",
    ulong PreviewSequence = 0, uint PreviewWidth = 0, uint PreviewHeight = 0, int PreviewLength = 0,
    ulong ImageSequence = 0, uint ImageWidth = 0, uint ImageHeight = 0, int ImageLength = 0);

internal sealed record AssistantHostSnapshot(AssistantHostReply Reply, byte[] Preview, byte[] Image);

internal sealed class AssistantHost
{
    private const string Command = "--assistant-host";
    private readonly Process process;
    private readonly SemaphoreSlim calls = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task errors;
    private int closed;

    internal AssistantHost(Process process)
    {
        this.process = process;
        errors = DrainAsync(process.StandardError.BaseStream);
    }

    public static async Task<AssistantHost> StartAsync(AssistantTarget target, bool vr, bool activate, CancellationToken cancellation, Action<string>? progress = null, bool border = false, float width = .7f, uint eye = 0, bool resetOnDashboard = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (vr) SteamVrAccess.EnsureCompatible();
        using var parent = Process.GetCurrentProcess();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定助手服务路径。"))
            {
                WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(Command);
        process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        try { if (!process.Start()) throw new InvalidOperationException("无法启动助手服务。"); }
        catch { process.Dispose(); throw; }
        var host = new AssistantHost(process);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await host.CallAsync(new("start", target.Window, target.ProcessId, target.ProcessStarted, vr, activate, Border: border, Width: width, Eye: eye, ResetOnDashboard: resetOnDashboard), deadline.Token);
            string lastStage = "正在启动助手";
            try
            {
                while (true)
                {
                    var snapshot = await host.PollAsync(false, 0, 0, deadline.Token, width, eye, resetOnDashboard);
                    if (snapshot.Reply.Message != lastStage) { lastStage = snapshot.Reply.Message; progress?.Invoke(lastStage); }
                    if (snapshot.Reply.CaptureState == 2 && (!vr || snapshot.Reply.InteractionState == 0 && lastStage == "助手已连接")) return host;
                    await Task.Delay(100, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new TimeoutException($"助手连接超时：{lastStage}。已隔离服务，不影响字幕和主界面。"); }
        }
        catch { await host.StopAsync(); throw; }
    }

    public Task<AssistantHostSnapshot> PollAsync(bool preview, ulong afterPreview, ulong afterImage, CancellationToken cancellation, float width = .7f, uint eye = 0, bool resetOnDashboard = false) =>
        CallAsync(new("poll", Preview: preview, AfterPreview: afterPreview, AfterImage: afterImage, Width: width, Eye: eye, ResetOnDashboard: resetOnDashboard), cancellation);

    public async Task ResetPositionAsync(CancellationToken cancellation)
    {
        await CallAsync(new("reset_position"), cancellation);
    }

    private async Task<AssistantHostSnapshot> CallAsync(AssistantHostRequest request, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await calls.WaitAsync(deadline.Token);
        try
        {
            if (Volatile.Read(ref closed) != 0) throw new OperationCanceledException("助手服务已关闭。");
            await WritePacketAsync(process.StandardInput.BaseStream, request, deadline.Token);
            var reply = await ReadPacketAsync<AssistantHostReply>(process.StandardOutput.BaseStream, deadline.Token)
                ?? throw new EndOfStreamException("助手服务已经退出。");
            ValidateReply(reply);
            if (reply.Error.Length != 0) throw new InvalidOperationException(reply.Error);
            var preview = new byte[reply.PreviewLength];
            var image = new byte[reply.ImageLength];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(preview, deadline.Token);
            await process.StandardOutput.BaseStream.ReadExactlyAsync(image, deadline.Token);
            return new(reply, preview, image);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("助手服务响应超时，已停止等待，可以断开后重试。"); }
        finally { calls.Release(); }
    }

    internal static void ValidateReply(AssistantHostReply reply)
    {
        if (reply.CaptureState > 3 || reply.InteractionState > 6 || reply.Message == null || reply.Error == null || reply.Message.Length > 4096 || reply.Error.Length > 4096
            || !ValidImage(reply.PreviewWidth, reply.PreviewHeight, reply.PreviewLength, 640, 240)
            || !ValidImage(reply.ImageWidth, reply.ImageHeight, reply.ImageLength, 2048, 1536))
            throw new InvalidDataException("助手服务返回了无效图像或状态。");
    }
    private static bool ValidImage(uint width, uint height, int length, uint maximumWidth, uint maximumHeight) =>
        length == 0 ? width == 0 && height == 0 : width > 0 && height > 0 && width <= maximumWidth && height <= maximumHeight
            && (ulong)width * height * 4 == (ulong)length;

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        lifetime.Cancel();
        try
        {
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException) { }
        finally
        {
            if (!process.HasExited) process.Kill();
            try { await errors.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            process.Dispose();
        }
    }

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[1024];
        try { while (await stream.ReadAsync(buffer) != 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    internal static async Task WritePacketAsync<T>(Stream stream, T value, CancellationToken cancellation)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length is 0 or > 32768) throw new InvalidDataException("助手消息长度无效。");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, data.Length);
        await stream.WriteAsync(prefix, cancellation); await stream.WriteAsync(data, cancellation); await stream.FlushAsync(cancellation);
    }

    internal static async Task<T?> ReadPacketAsync<T>(Stream stream, CancellationToken cancellation) where T : class
    {
        var prefix = new byte[4];
        int first = await stream.ReadAsync(prefix.AsMemory(0, 1), cancellation);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(prefix.AsMemory(1), cancellation);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > 32768) throw new InvalidDataException("助手消息长度无效。");
        var data = new byte[length]; await stream.ReadExactlyAsync(data, cancellation);
        return JsonSerializer.Deserialize<T>(data) ?? throw new InvalidDataException("助手消息为空。");
    }

    public static bool TryHandleCommand(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Length == 0 || arguments[0] != Command) return false;
        try { exitCode = RunAsync(arguments).GetAwaiter().GetResult(); }
        catch { exitCode = 1; }
        return true;
    }

    private static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length != 3 || !int.TryParse(arguments[1], out int parentId) || parentId <= 0
            || !long.TryParse(arguments[2], out long parentStarted)) return 1;
        using var parent = Process.GetProcessById(parentId);
        if (parent.StartTime.ToUniversalTime().Ticks != parentStarted
            || !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 1;
        _ = Task.Run(async () => { try { await parent.WaitForExitAsync(); Environment.Exit(0); } catch (Exception) { } });
        using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
        using var bridge = new BridgeHandle();
        NativeBridge.EnsureVersion();
        if (bridge.IsInvalid) return 1;
        using var previewBuffer = new PreviewBuffer();
        var previewPixels = new byte[PreviewBuffer.Capacity];
        bool started = false, vr = false;
        BridgeOptions currentOptions = default;
        while (await ReadPacketAsync<AssistantHostRequest>(input, CancellationToken.None) is { } request)
        {
            try
            {
                if (request.Action == "start" && !started)
                {
                    var target = new AssistantTarget { Size = 24, Window = request.Window, ProcessId = request.ProcessId, ProcessStarted = request.ProcessStarted };
                    var error = new BridgeError { Size = (uint)Marshal.SizeOf<BridgeError>(), Text = "" };
                    var original = target;
                    int identified = NativeBridge.AssistantTarget(ref target, ref error);
                    if (identified < 0 || target.ProcessStarted != original.ProcessStarted) throw new InvalidOperationException(error.Text.Length == 0 ? "豆包窗口已变化。" : error.Text);
                    vr = request.Vr;
                    float initialWidth = float.IsFinite(request.Width) ? Math.Clamp(request.Width, .3f, 1.5f) : .7f;
                    uint initialEye = request.Eye != 0 ? 1u : 0;
                    uint initialReset = request.ResetOnDashboard ? 1u : 0;
                    currentOptions = new BridgeOptions
                    { Size = 56, Width = initialWidth, Distance = 1, Opacity = 1, CropTop = initialReset, Trim = initialEye, Border = request.Border ? 1u : 0, Vr = vr ? 1u : 0, Preview = 1 };
                    if (vr)
                    {
                        var options = new AssistantRequest
                        {
                            Size = (uint)Marshal.SizeOf<AssistantRequest>(), Window = target.Window, ProcessId = target.ProcessId,
                            ProcessStarted = target.ProcessStarted, Width = initialWidth, Distance = 1, ActivateOnClick = request.Activate ? 1u : 0,
                            Border = request.Border ? 1u : 0,
                            Manifest = Path.Combine(AppContext.BaseDirectory, "steamvr", "assistant_actions.json")
                        };
                        int result = NativeBridge.AssistantStart(bridge, options);
                        if (result < 0) throw new InvalidOperationException(NativeBridge.Error(bridge));
                        if (initialEye != 0 || initialReset != 0) Marshal.ThrowExceptionForHR(NativeBridge.Configure(bridge, currentOptions));
                    }
                    else NativeBridge.StartCapture(bridge, (nuint)target.Window, currentOptions);
                    started = true;
                    await WritePacketAsync(output, new AssistantHostReply(Message: "正在启动助手…"), CancellationToken.None);
                    continue;
                }
                if (request.Action == "reset_position")
                {
                    if (started && vr)
                    {
                        Marshal.ThrowExceptionForHR(NativeBridge.AssistantResetPosition(bridge));
                    }
                    await WritePacketAsync(output, new AssistantHostReply(Message: "已重置浮窗位置"), CancellationToken.None);
                    continue;
                }
                if (request.Action != "poll" || !started) throw new InvalidDataException("助手服务请求无效。");
                float requestedWidth = float.IsFinite(request.Width) ? Math.Clamp(request.Width, .3f, 1.5f) : currentOptions.Width;
                uint requestedEye = request.Eye != 0 ? 1u : 0;
                uint requestedReset = request.ResetOnDashboard ? 1u : 0;
                if (Math.Abs(requestedWidth - currentOptions.Width) > 1e-4f || requestedEye != currentOptions.Trim || requestedReset != currentOptions.CropTop)
                {
                    currentOptions.Width = requestedWidth;
                    currentOptions.Trim = requestedEye;
                    currentOptions.CropTop = requestedReset;
                    Marshal.ThrowExceptionForHR(NativeBridge.Configure(bridge, currentOptions));
                }
                var stats = NativeBridge.Stats(bridge);
                var state = new AssistantStatus { Size = 1040, Message = "" };
                if (vr) Marshal.ThrowExceptionForHR(NativeBridge.AssistantStatus(bridge, ref state));
                string failure = stats.State == 3 ? NativeBridge.Error(bridge) : "";
                uint previewWidth = 0, previewHeight = 0, imageWidth = 0, imageHeight = 0;
                ulong previewSequence = request.AfterPreview, imageSequence = request.AfterImage;
                uint previewLength = 0;
                byte[] image = Array.Empty<byte>();
                if (failure.Length == 0 && request.Preview)
                    previewLength = NativeBridge.Preview(bridge, previewBuffer, previewPixels, request.AfterPreview, out previewWidth, out previewHeight, out previewSequence);
                if (failure.Length == 0 && state.State == 4 && state.Sequence != request.AfterImage)
                {
                    using var imageBuffer = new AssistantImageBuffer();
                    int result = NativeBridge.AssistantImage(bridge, state.Sequence, imageBuffer);
                    Marshal.ThrowExceptionForHR(result);
                    if (result == 0) image = imageBuffer.CopyTo(out imageWidth, out imageHeight, out imageSequence);
                }
                var reply = new AssistantHostReply(stats.State, state.State, vr ? state.Message : stats.State == 2 ? "浮窗已连接" : "等待浮窗画面…", failure,
                    previewSequence, previewWidth, previewHeight, (int)previewLength, imageSequence, imageWidth, imageHeight, image.Length);
                await WritePacketAsync(output, reply, CancellationToken.None);
                if (previewLength > 0) await output.WriteAsync(previewPixels.AsMemory(0, (int)previewLength));
                if (image.Length > 0) await output.WriteAsync(image);
                await output.FlushAsync();
            }
            catch (Exception exception)
            {
                await WritePacketAsync(output, new AssistantHostReply(Error: exception.Message), CancellationToken.None);
                return 1;
            }
        }
        return 0;
    }
}
