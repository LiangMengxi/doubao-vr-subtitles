using System.ComponentModel;
using System.Diagnostics;

namespace DoubaoVR.Desktop;

internal static class DoubaoExitService
{
    public static async Task CloseAsync()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { allowed.Add(Path.GetFullPath(ShortcutService.InstalledExecutable())); }
        catch (InvalidOperationException) { }
        var running = ShortcutService.RunningExecutable();
        if (running != null) allowed.Add(Path.GetFullPath(running));
        var processes = Process.GetProcessesByName("doubao");
        try { await CloseVerifiedAsync(processes, allowed); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static async Task CloseVerifiedAsync(IEnumerable<Process> processes, IReadOnlySet<string> allowed)
    {
        var targets = new List<Process>();
        var failures = new List<Exception>();
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited) continue;
                var executable = WindowSource.Executable(process.SafeHandle);
                if (executable == null) throw new Win32Exception("无法确认豆包进程路径。");
                if (allowed.Contains(Path.GetFullPath(executable))) targets.Add(process);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                if (!Exited(process)) failures.Add(exception);
            }
        }
        bool requested = false;
        foreach (var process in targets)
        {
            try { if (!process.HasExited) requested |= process.CloseMainWindow(); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                if (!Exited(process)) failures.Add(exception);
            }
        }
        if (requested)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await Task.WhenAll(targets.Select(process => process.WaitForExitAsync(deadline.Token))); }
            catch (OperationCanceledException) { }
        }
        foreach (var process in targets)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or TimeoutException)
            {
                if (!Exited(process)) failures.Add(exception);
            }
        }
        if (failures.Count != 0) throw new AggregateException("部分豆包进程未能关闭，请从豆包托盘手动退出。", failures);
    }

    private static bool Exited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }
}
