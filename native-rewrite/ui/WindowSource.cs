using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DoubaoVR.Desktop;

internal sealed record CaptionWindow(nint Handle, uint ProcessId, string Title, string ClassName,
    int Width, int Height, long Style)
{
    public int Score
    {
        get
        {
            var ratio = Width / (double)Math.Max(1, Height);
            bool expanded = Title.Length == 0 && ClassName == "Chrome_WidgetWin_1" && (Style & 0x80) != 0
                && Width >= 600 && ratio >= .7 && ratio <= 2.5;
            return (Title.Length == 0 ? 80 : 0) + ((Style & 0x80) != 0 ? 40 : 0)
                + (ratio > 2.5 || expanded ? 40 : 0) + (Width >= 350 ? 20 : -100);
        }
    }
    public override string ToString() => $"{(Title.Length == 0 ? "无标题字幕候选" : Title)} · {Width}×{Height}";
}

internal static class WindowSource
{
    private delegate bool WindowCallback(nint hwnd, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);

    public static string? Executable(uint processId)
    {
        using var handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid) return null;
        return Executable(handle);
    }

    public static string? Executable(SafeProcessHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint length = (uint)buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
    }

    public static List<CaptionWindow> Discover()
    {
        var windows = new List<CaptionWindow>();
        var paths = new Dictionary<uint, string?>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var processId);
            if (!paths.TryGetValue(processId, out var path)) paths[processId] = path = Executable(processId);
            if (!string.Equals(Path.GetFileName(path), "doubao.exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (!GetWindowRect(hwnd, out var rect)) return true;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width < 80 || height < 40) return true;
            var title = new StringBuilder(1024);
            var className = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            GetClassName(hwnd, className, className.Capacity);
            windows.Add(new(hwnd, processId, title.ToString(), className.ToString(), width, height, GetWindowLongPtr(hwnd, -20).ToInt64()));
            return true;
        }, 0);
        return windows.OrderByDescending(window => window.Score).ToList();
    }

    public static bool Same(CaptionWindow window)
    {
        GetWindowThreadProcessId(window.Handle, out var owner);
        return owner == window.ProcessId && IsWindowVisible(window.Handle);
    }
}
