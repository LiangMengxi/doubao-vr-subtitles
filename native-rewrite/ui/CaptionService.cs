using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DoubaoVR.Desktop;

[StructLayout(LayoutKind.Sequential)]
internal struct CaptionButton
{
    public uint Size, ProcessId;
    public ulong Window, Observed, ProcessStarted;
    public uint ClientWidth, ClientHeight, SourceWidth, SourceHeight, Horizontal, Vertical, Dpi, Reserved;
}

internal sealed class CaptionService : ICaptionBackend
{
    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_caption_button", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadButton(nuint window, uint processId, ref CaptionButton button, ref BridgeError error);

    [DllImport("DoubaoVR.Core.dll", EntryPoint = "bridge_caption_close", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ClickClose(in CaptionButton button, ref BridgeError error);

    public IReadOnlyList<CaptionWindow> Discover() => WindowSource.Discover();
    public Task<CaptionWindow> OpenAsync(CancellationToken cancellation) => ShortcutService.OpenAsync(cancellation);

    public async Task CloseAsync(CaptionWindow target, CancellationToken cancellation)
    {
        if (!target.IsCaption) throw new InvalidOperationException("目标不是豆包字幕窗口，已取消关闭。");
        cancellation.ThrowIfCancellationRequested();
        if (!WindowSource.Same(target)) return;
        var button = await Task.Run(() =>
        {
            var snapshot = new CaptionButton { Size = 64 };
            var error = new BridgeError { Size = (uint)Marshal.SizeOf<BridgeError>(), Text = "" };
            int result = ReadButton((nuint)target.Handle, target.ProcessId, ref snapshot, ref error);
            Check(result, error);
            return result == 1 ? (CaptionButton?)null : snapshot;
        }, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (button == null) return;
        var detail = new BridgeError { Size = (uint)Marshal.SizeOf<BridgeError>(), Text = "" };
        Check(ClickClose(button.Value, ref detail), detail);
        var elapsed = Stopwatch.StartNew();
        while (WindowSource.Same(target))
        {
            cancellation.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("豆包未关闭字幕，请重试或使用字幕条的关闭按钮。");
            await Task.Delay(100, cancellation);
        }
    }

    private static void Check(int result, BridgeError error)
    {
        if (result < 0) throw new COMException(string.IsNullOrWhiteSpace(error.Text) ? $"关闭豆包字幕失败（0x{unchecked((uint)result):X8}）。" : error.Text, result);
    }
}
