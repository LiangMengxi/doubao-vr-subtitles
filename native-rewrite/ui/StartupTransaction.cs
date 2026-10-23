using System.Text;
using System.Text.Json;

namespace DoubaoVR.Desktop;

internal sealed record StartupState(bool Installed, bool AutoLaunch, string Executable = "", string Arguments = "", bool Dashboard = false)
{
    public bool Matches(StartupState other) => Installed == other.Installed && AutoLaunch == other.AutoLaunch
        && (!Installed || (string.Equals(Executable, other.Executable, StringComparison.OrdinalIgnoreCase)
            && Arguments == other.Arguments && Dashboard == other.Dashboard));
}

internal interface IStartupBackend
{
    Task<StartupState> ReadAsync();
    Task ApplyAsync(bool installed, bool autoLaunch);
}

internal sealed class StartupTransaction(string directory, string executable, IStartupBackend backend)
{
    private readonly string root = Path.GetFullPath(directory);
    private string Manifest => Path.Combine(root, "DoubaoVR.vrmanifest");

    private static async Task<byte[]?> ReadFileAsync(string path)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > 32768) throw new InvalidDataException("SteamVR 注册文件过大。");
        byte[] content = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(content);
        return content;
    }

    private static async Task ReplaceAsync(string path, byte[]? content)
    {
        if (content == null) { if (File.Exists(path)) File.Delete(path); return; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, content); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Equal(byte[]? first, byte[]? second) => first == null ? second == null : second != null && first.SequenceEqual(second);

    private async Task<StartupState> ConfirmAsync(StartupState expected)
    {
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            var actual = await backend.ReadAsync();
            if (actual.Matches(expected)) return actual;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("SteamVR 未确认目标启动项状态。");
    }

    public async Task ChangeAsync(bool enabled, Func<bool, Task> persist, Action<bool> display, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Manifest.Length >= 4096) throw new InvalidOperationException("SteamVR 注册文件路径过长。");
        Directory.CreateDirectory(root);
        await using var lease = new FileStream(Path.Combine(root, "transaction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Directory.EnumerateFiles(root, "transaction-*.json").Any())
            throw new InvalidOperationException("存在尚未确认的 SteamVR 注册事务，请先检查启动项和本地恢复记录，避免覆盖原状态。");
        var before = await backend.ReadAsync();
        cancellation.ThrowIfCancellationRequested();
        byte[]? original = await ReadFileAsync(Manifest);
        byte[]? backup = await ReadFileAsync(Manifest + ".bak");
        if (before.Installed)
        {
            if (original == null) throw new InvalidDataException("原 SteamVR 注册文件缺失，无法安全修改，请先检查原安装目录。");
            SteamVrStartupService.ValidateManifest(Encoding.UTF8.GetString(original), before.Executable);
            if (before.Arguments != "--steamvr-startup" || !before.Dashboard)
                throw new InvalidDataException("实际 SteamVR 启动项与本程序不匹配，未作修改。");
        }
        var desired = enabled ? new StartupState(true, true, Path.GetFullPath(executable), "--steamvr-startup", true) : new StartupState(false, false);
        byte[]? target = enabled ? Encoding.UTF8.GetBytes(SteamVrStartupService.BuildManifest(executable)) : original;
        string journal = Path.Combine(root, "transaction-" + Guid.NewGuid().ToString("N") + ".json");
        await ReplaceAsync(journal, JsonSerializer.SerializeToUtf8Bytes(new { Before = before, Desired = desired, Manifest = original, Backup = backup, Target = target }));
        bool fileChanged = false, mutationAttempted = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Equal(await ReadFileAsync(Manifest), original) || !Equal(await ReadFileAsync(Manifest + ".bak"), backup))
                throw new IOException("SteamVR 注册文件已被其他程序修改，请重试。");
            if (enabled && !Equal(original, target))
            {
                fileChanged = true;
                if (original != null) await ReplaceAsync(Manifest + ".bak", original);
                await ReplaceAsync(Manifest, target);
            }
            if (!before.Matches(desired))
            {
                mutationAttempted = true;
                await backend.ApplyAsync(desired.Installed, desired.AutoLaunch);
            }
            var actual = await ConfirmAsync(desired);
            display(actual.AutoLaunch);
            await persist(actual.AutoLaunch);
            File.Delete(journal);
        }
        catch (Exception originalError)
        {
            try
            {
                if (fileChanged)
                {
                    var currentBackup = await ReadFileAsync(Manifest + ".bak");
                    if (!Equal(currentBackup, original) && !Equal(currentBackup, backup)) throw new IOException("注册备份已发生外部变化，未覆盖。");
                }
                if (mutationAttempted)
                {
                    var observed = await backend.ReadAsync();
                    bool owned = !observed.Installed || (observed.Arguments == "--steamvr-startup" && observed.Dashboard
                        && (string.Equals(observed.Executable, before.Executable, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(observed.Executable, desired.Executable, StringComparison.OrdinalIgnoreCase)));
                    if (!owned) throw new InvalidOperationException("启动项已被外部程序改变，未盲目覆盖。");
                    if (!Equal(await ReadFileAsync(Manifest), target)) throw new IOException("注册文件已发生外部变化，未覆盖。");
                    if (before.Installed)
                    {
                        await ReplaceAsync(Manifest, original);
                        if (!observed.Matches(before)) await backend.ApplyAsync(true, before.AutoLaunch);
                    }
                    else if (observed.Installed) await backend.ApplyAsync(false, false);
                    _ = await ConfirmAsync(before);
                }
                if (fileChanged)
                {
                    var currentFile = await ReadFileAsync(Manifest);
                    if (!Equal(currentFile, original) && !Equal(currentFile, target)) throw new IOException("注册文件已发生外部变化，未覆盖。");
                    await ReplaceAsync(Manifest, original);
                    await ReplaceAsync(Manifest + ".bak", backup);
                }
                display(before.AutoLaunch);
                await persist(before.AutoLaunch);
                File.Delete(journal);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("SteamVR 原状态尚未完整确认；恢复记录保留在 " + journal, originalError, rollbackError);
            }
            throw;
        }
    }
}
