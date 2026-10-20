using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoubaoVR.Desktop;

internal static class Program
{
    private static Mutex? instance;
    internal static EventWaitHandle? RestoreEvent;

    [STAThread]
    private static void Main(string[] arguments)
    {
        Log.Write("Starting WinUI desktop.");
        bool motionTest = arguments.Contains("--settings-motion-test");
        string suffix = motionTest ? ".MotionChecks" : "";
        instance = new Mutex(true, @"Local\DoubaoVR.Native.Desktop" + suffix, out bool created);
        RestoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DoubaoVR.Native.Restore" + suffix);
        if (!created && motionTest) { RestoreEvent.Dispose(); instance.Dispose(); Environment.ExitCode = 1; return; }
        if (!created) { RestoreEvent.Set(); RestoreEvent.Dispose(); instance.Dispose(); return; }
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Log.Write("COM wrappers initialized.");
            Application.Start(initialization =>
            {
                Log.Write("Application dispatcher initialized.");
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new BridgeApplication(arguments.Contains("--smoke-test"), motionTest);
            });
        }
        catch (Exception exception) { Log.Write(exception.ToString()); throw; }
        finally { RestoreEvent.Dispose(); instance.ReleaseMutex(); instance.Dispose(); }
    }
}

public sealed partial class BridgeApplication : Application
{
    private Window? window;
    private readonly bool smokeTest;
    private readonly bool motionTest;
    public BridgeApplication(bool smokeTest, bool motionTest = false)
    {
        Log.Write("Creating application resources.");
        this.smokeTest = smokeTest;
        this.motionTest = motionTest;
        InitializeComponent();
        Log.Write("Application resources ready.");
        UnhandledException += (_, arguments) => Log.Write(arguments.Exception.ToString());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs arguments)
    {
        try
        {
            if (motionTest)
            {
                var checks = new SettingsMotionChecks();
                window = checks;
                checks.Activate();
                _ = checks.RunAsync();
                return;
            }
            Log.Write("Creating main window.");
            window = new MainWindow(smokeTest);
            window.Activate();
            Log.Write("WinUI window activated.");
        }
        catch (Exception exception) { Log.Write(exception.ToString()); throw; }
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var folder = Path.Combine(Settings.DataDirectory, "logs");
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "native.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".1", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
