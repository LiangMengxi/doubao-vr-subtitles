using System.Diagnostics;

namespace DoubaoVR.Desktop;

internal interface ICaptionBackend
{
    IReadOnlyList<CaptionWindow> Discover();
    Task<CaptionWindow> OpenAsync(CancellationToken cancellation);
    Task CloseAsync(CaptionWindow target, CancellationToken cancellation);
}

internal sealed class CaptionController(ICaptionBackend backend)
{
    public CaptionWindow? Target { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsClosing { get; private set; }
    public string Label => Target == null ? "开启字幕" : "关闭字幕";
    private long revision;

    public void SyncTarget(CaptionWindow? target)
    {
        ++revision;
        Target = target;
    }

    internal static CaptionWindow? Find(IReadOnlyList<CaptionWindow> windows)
    {
        var candidates = windows.Where(window => window.IsCaption).Take(2).ToArray();
        if (candidates.Length > 1) throw new InvalidOperationException("发现多个豆包字幕候选窗口，请先在豆包中确认字幕条后重试。");
        return candidates.FirstOrDefault();
    }

    public async Task RefreshAsync(CancellationToken cancellation)
    {
        if (IsBusy) return;
        long observed = revision;
        var windows = await Task.Run(backend.Discover, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (!IsBusy && observed == revision)
        {
            var candidates = windows.Where(window => window.IsCaption).ToArray();
            Target = candidates.FirstOrDefault(window => window.Handle == Target?.Handle && window.ProcessId == Target?.ProcessId)
                ?? (candidates.Length == 1 ? candidates[0] : null);
        }
    }

    public async Task ToggleAsync(Func<CaptionWindow, Task> opened, Func<Task> closing, CancellationToken cancellation)
    {
        if (IsBusy) return;
        cancellation.ThrowIfCancellationRequested();
        IsBusy = true;
        IsClosing = Target != null;
        ++revision;
        var elapsed = Stopwatch.StartNew();
        try
        {
            if (Target is CaptionWindow target)
            {
                await closing();
                cancellation.ThrowIfCancellationRequested();
                await backend.CloseAsync(target, cancellation);
                Target = null;
            }
            else
            {
                var openedWindow = await backend.OpenAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!openedWindow.IsCaption) throw new InvalidOperationException("未能确认豆包字幕窗口。");
                Target = openedWindow;
                await opened(openedWindow);
            }
        }
        finally
        {
            var remaining = TimeSpan.FromMilliseconds(350) - elapsed.Elapsed;
            if (remaining > TimeSpan.Zero && !cancellation.IsCancellationRequested) await Task.Delay(remaining);
            IsClosing = false;
            IsBusy = false;
        }
    }
}
