using System.Security.Cryptography;

namespace DoubaoVR.Desktop;

internal static class SpeechModelStore
{
    public sealed record ModelFileInfo(string FileName, long ExpectedSize, string ExpectedSha256, string[] Urls);

    public static readonly string ModelsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DoubaoVR.Native",
        "models",
        "sense-voice");

    public static readonly ModelFileInfo ModelFile = new(
        "model.int8.onnx",
        239233841L,
        "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51",
        [
            "https://hf-mirror.com/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/model.int8.onnx",
            "https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/model.int8.onnx"
        ]);

    public static readonly ModelFileInfo TokensFile = new(
        "tokens.txt",
        315894L,
        "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc",
        [
            "https://hf-mirror.com/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/tokens.txt",
            "https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/tokens.txt"
        ]);

    public static readonly ModelFileInfo VadFile = new(
        "silero_vad.onnx",
        1807522L,
        "a35ebf52fd3ce5f1469b2a36158dba761bc47b973ea3382b3186ca15b1f5af28",
        [
            "https://hf-mirror.com/csukuangfj/vad/resolve/main/silero_vad.onnx",
            "https://huggingface.co/csukuangfj/vad/resolve/main/silero_vad.onnx",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx"
        ]);

    public static string ModelPath => Path.Combine(ModelsDirectory, ModelFile.FileName);
    public static string TokensPath => Path.Combine(ModelsDirectory, TokensFile.FileName);
    public static string VadPath => Path.Combine(ModelsDirectory, VadFile.FileName);

    public static bool AreModelsInstalled()
    {
        return IsFileValid(ModelFile) && IsFileValid(TokensFile) && IsFileValid(VadFile);
    }

    private static bool IsFileValid(ModelFileInfo fileInfo)
    {
        string filePath = Path.Combine(ModelsDirectory, fileInfo.FileName);
        if (!File.Exists(filePath)) return false;
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Length != fileInfo.ExpectedSize) return false;
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(stream);
            string hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return string.Equals(hex, fileInfo.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Write($"[SpeechModelStore] Verification error for {fileInfo.FileName}: {ex.Message}");
            return false;
        }
    }

    public static async Task EnsureModelsAsync(Action<string>? statusCallback = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsDirectory);

        var allFiles = new[] { VadFile, TokensFile, ModelFile };
        var filesToDownload = new List<ModelFileInfo>();

        foreach (var file in allFiles)
        {
            if (!IsFileValid(file))
            {
                filesToDownload.Add(file);
            }
        }

        if (filesToDownload.Count == 0)
        {
            Log.Write("[SpeechModelStore] All speech models are verified and ready.");
            return;
        }

        long totalBytesToDownload = filesToDownload.Sum(f => f.ExpectedSize);
        long totalBytesDownloadedBeforeCurrent = 0;

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        foreach (var file in filesToDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetPath = Path.Combine(ModelsDirectory, file.FileName);
            string partPath = targetPath + ".part";

            bool success = false;
            Exception? lastEx = null;

            foreach (var url in file.Urls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log.Write($"[SpeechModelStore] Downloading {file.FileName} from {url}...");

                try
                {
                    if (File.Exists(partPath))
                    {
                        File.Delete(partPath);
                    }

                    using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();

                        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await using var partStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                        byte[] buffer = new byte[65536];
                        long fileDownloaded = 0;
                        int bytesRead;

                        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                        {
                            await partStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                            incrementalHash.AppendData(buffer, 0, bytesRead);
                            fileDownloaded += bytesRead;

                            long overallDownloaded = totalBytesDownloadedBeforeCurrent + fileDownloaded;
                            int percent = totalBytesToDownload > 0
                                ? (int)Math.Clamp(overallDownloaded * 100 / totalBytesToDownload, 0, 100)
                                : 0;
                            double mbDownloaded = overallDownloaded / (1024.0 * 1024.0);
                            double mbTotal = totalBytesToDownload / (1024.0 * 1024.0);

                            statusCallback?.Invoke($"正在下载语音模型… {percent}%（{mbDownloaded:F1}/{mbTotal:F1} MB）");
                        }

                        byte[] hashBytes = incrementalHash.GetHashAndReset();
                        string computedHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

                        if (fileDownloaded != file.ExpectedSize)
                        {
                            throw new InvalidOperationException($"Size mismatch for {file.FileName}: got {fileDownloaded}, expected {file.ExpectedSize}");
                        }

                        if (!string.Equals(computedHash, file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"Hash mismatch for {file.FileName}: got {computedHash}, expected {file.ExpectedSha256}");
                        }
                    }

                    File.Move(partPath, targetPath, overwrite: true);
                    totalBytesDownloadedBeforeCurrent += file.ExpectedSize;
                    success = true;
                    Log.Write($"[SpeechModelStore] Downloaded and verified {file.FileName} successfully.");
                    break;
                }
                catch (OperationCanceledException)
                {
                    TryDeleteFile(partPath);
                    throw;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Log.Write($"[SpeechModelStore] Failed downloading {file.FileName} from {url}: {ex.Message}");
                    TryDeleteFile(partPath);
                }
            }

            if (!success)
            {
                throw new InvalidOperationException($"无法下载模型文件 {file.FileName}: {lastEx?.Message}", lastEx);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
