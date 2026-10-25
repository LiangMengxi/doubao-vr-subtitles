using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow
{
    private sealed record AssistantWindow(CaptionWindow Window)
    {
        public override string ToString() => $"{(Window.Title.Length == 0 ? "豆包浮窗" : Window.Title)} · {Window.Width}×{Window.Height}";
    }
    private readonly ComboBox assistantSources = new() { Header = "豆包浮窗", PlaceholderText = "请选择对话浮窗", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock assistantStatus = new() { Text = "未连接", TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
    private readonly Button assistantConnect = new() { Content = "连接浮窗", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button assistantVr = new() { Content = "开启VR", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button assistantAttach = new() { Content = "添加截图", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
    private readonly Button assistantResetPosition = new() { Content = "重置浮窗位置到视野右侧", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
    private readonly ToggleSwitch assistantBorder = new() { Header = "显示捕获黄框" };
    private readonly Image assistantPreview = new() { Stretch = Stretch.Uniform, MaxHeight = 260 };
    private readonly SemaphoreSlim assistantOperations = new(1, 1);
    private DispatcherQueueTimer? assistantTimer;
    private AssistantHost? assistantHost;
    private AssistantTarget assistantTarget;
    private bool assistantRunning, assistantVrEnabled, assistantBusy, assistantTickBusy, syncingBorder;
    private long assistantEpoch;
    private ulong assistantPreviewSequence, assistantImageSequence;
    private uint lastAssistantState;
    private string lastAssistantMessage = "";
    private byte[]? assistantScreenshot;
    private uint screenshotWidth, screenshotHeight;
    private InMemoryRandomAccessStream? assistantClipboardImage;
    private WriteableBitmap? assistantBitmap;

    private StackPanel AssistantPage()
    {
        var page = Column();
        var controls = Column();
        var actions = EqualColumns(2);
        assistantVr.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        assistantConnect.Click += async (_, _) => await ChangeAssistantAsync(false);
        assistantVr.Click += async (_, _) => await ChangeAssistantAsync(true);
        AddCell(actions, assistantConnect, 0); AddCell(actions, assistantVr, 1);
        controls.Children.Add(actions);
        controls.Children.Add(new Border { Child = assistantPreview, MinHeight = 110, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)) });
        controls.Children.Add(assistantStatus);
        assistantAttach.Click += async (_, _) => await AttachAssistantScreenshotAsync();
        controls.Children.Add(assistantAttach);
        page.Children.Add(Card(controls));
        var sizing = Column(16);
        sizing.Children.Add(Slider("浮窗大小", .3, 1.5, .05, settings.AssistantWidth, value => settings.AssistantWidth = (float)value, value => $"{value * 100:F0}%",
            ("小", .5), ("标准", .7), ("大", 1)));
        assistantResetPosition.Click += async (_, _) => await ResetAssistantPositionAsync();
        sizing.Children.Add(assistantResetPosition);
        var menuReset = new ToggleSwitch { Header = "按菜单键自动重置位置", IsOn = settings.AssistantResetOnDashboard };
        menuReset.Toggled += (_, _) => { settings.AssistantResetOnDashboard = menuReset.IsOn; ApplyConfiguration(); };
        sizing.Children.Add(menuReset);
        page.Children.Add(Card(sizing));
        var options = Column();
        options.Children.Add(assistantSources);
        var refresh = new Button { Content = "刷新窗口", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        refresh.Click += (_, _) => RefreshAssistantSources();
        options.Children.Add(refresh);
        var eyeSelector = new ComboBox
        {
            Header = "空间截图视角",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "左眼（默认）", "右眼" },
            SelectedIndex = settings.AssistantCaptureEye == 1 ? 1 : 0
        };
        eyeSelector.SelectionChanged += (_, _) =>
        {
            settings.AssistantCaptureEye = (uint)(eyeSelector.SelectedIndex == 1 ? 1 : 0);
            ApplyConfiguration();
        };
        options.Children.Add(eyeSelector);
        var activate = new ToggleSwitch { Header = "交互时激活豆包浮窗", IsOn = settings.AssistantActivateOnClick };
        activate.Toggled += (_, _) => { settings.AssistantActivateOnClick = activate.IsOn; ApplyConfiguration(); if (assistantRunning) assistantStatus.Text = "重新连接后生效"; };
        options.Children.Add(activate);
        var autoAttach = new ToggleSwitch { Header = "截图后添加到豆包", IsOn = settings.AssistantAutoAttach };
        autoAttach.Toggled += (_, _) => { settings.AssistantAutoAttach = autoAttach.IsOn; ApplyConfiguration(); };
        options.Children.Add(autoAttach);
        assistantBorder.IsOn = settings.Border;
        assistantBorder.Toggled += async (_, _) => await UpdateCaptureBorderAsync(assistantBorder.IsOn);
        options.Children.Add(assistantBorder);
        page.Children.Add(new SettingsExpander { Style = (Style)Application.Current.Resources["SettingsExpanderStyle"], Header = "窗口与截图", Content = options, IsExpanded = false });
        return page;
    }

    private void InitializeAssistant()
    {
        RefreshAssistantSources();
        assistantTimer = DispatcherQueue.CreateTimer();
        assistantTimer.Interval = TimeSpan.FromMilliseconds(100);
        assistantTimer.Tick += async (_, _) => await TickAssistantAsync();
        assistantTimer.Start();
    }

    private void RefreshAssistantSources(nint preferred = default)
    {
        if (shuttingDown || assistantRunning) return;
        var previous = preferred != 0 ? preferred : (assistantSources.SelectedItem as AssistantWindow)?.Window.Handle;
        var candidates = WindowSource.Discover(true)
            .Where(window => window.Handle != connected?.Handle && !window.IsCaption)
            .OrderByDescending(window => window.AssistantScore)
            .Select(window => new AssistantWindow(window))
            .ToList();
        assistantSources.ItemsSource = candidates;
        assistantSources.SelectedItem = candidates.FirstOrDefault(candidate => candidate.Window.Handle == previous)
            ?? candidates.FirstOrDefault(candidate => candidate.Window.IsAssistantCandidate)
            ?? candidates.FirstOrDefault();
    }

    private void UpdateAssistantControls()
    {
        assistantConnect.Content = assistantRunning ? "断开浮窗" : "连接浮窗";
        assistantVr.Content = assistantVrEnabled ? "关闭VR" : "开启VR";
        assistantConnect.IsEnabled = assistantVr.IsEnabled = !assistantBusy && !shuttingDown;
        assistantSources.IsEnabled = !assistantRunning && !assistantBusy;
        assistantAttach.IsEnabled = assistantRunning && assistantScreenshot != null && !assistantBusy && !shuttingDown;
        assistantResetPosition.IsEnabled = assistantRunning && assistantVrEnabled && !assistantBusy && !shuttingDown;
    }

    private async Task ChangeAssistantAsync(bool toggleVr)
    {
        if (assistantBusy || shuttingDown) return;
        assistantBusy = true; UpdateAssistantControls();
        await assistantOperations.WaitAsync();
        try
        {
            if (shuttingDown) return;
            if ((!toggleVr && assistantRunning) || (toggleVr && assistantRunning && assistantVrEnabled))
            {
                await StopAssistantCoreAsync(false);
                return;
            }
            if (!assistantRunning) RefreshAssistantSources();
            var selected = assistantSources.SelectedItem as AssistantWindow;
            if (selected == null || !WindowSource.Same(selected.Window) || selected.Window.Handle == connected?.Handle || selected.Window.IsCaption)
            {
                assistantStatus.Text = "正在自动唤起豆包浮窗…";
                var openedWindow = await ShortcutService.OpenAssistantAsync(connected?.Handle ?? 0, lifetime.Token);
                RefreshAssistantSources(openedWindow.Handle);
                selected = assistantSources.SelectedItem as AssistantWindow ?? new AssistantWindow(openedWindow);
            }
            if (connected?.Handle == selected.Window.Handle) throw new InvalidOperationException("不能将当前字幕窗口作为对话浮窗。");
            var target = new AssistantTarget { Size = (uint)Marshal.SizeOf<AssistantTarget>(), Window = (ulong)selected.Window.Handle, ProcessId = selected.Window.ProcessId };
            var error = new BridgeError { Size = (uint)Marshal.SizeOf<BridgeError>(), Text = "" };
            int result = NativeBridge.AssistantTarget(ref target, ref error);
            if (result < 0) throw new COMException(error.Text, result);
            WindowSource.SetAssistant((nint)target.Window, target.ProcessId);
            bool enableVr = toggleVr && !assistantVrEnabled;
            ++assistantEpoch;
            var previous = assistantHost; assistantHost = null;
            assistantRunning = assistantVrEnabled = false;
            if (previous != null) await previous.StopAsync();
            assistantStatus.Text = enableVr ? "正在连接 SteamVR…" : "正在连接浮窗…";
            assistantHost = await AssistantHost.StartAsync(target, enableVr, settings.AssistantActivateOnClick, lifetime.Token,
                message => { if (!shuttingDown) assistantStatus.Text = message; }, settings.Border, settings.AssistantWidth, settings.AssistantCaptureEye, settings.AssistantResetOnDashboard);
            assistantTarget = target; assistantRunning = true; assistantVrEnabled = enableVr;
            assistantPreviewSequence = 0;
            assistantImageSequence = 0;
            lastAssistantState = uint.MaxValue; lastAssistantMessage = "";
            RefreshSources();
            assistantStatus.Text = enableVr ? "助手已连接" : "浮窗已连接";
            Log.Write($"Assistant capture started: class={selected.Window.ClassName}, vr={enableVr}.");
        }
        catch (Exception exception) { if (!shuttingDown) { assistantStatus.Text = exception.Message; Log.Write(exception.ToString()); } }
        finally { assistantBusy = false; assistantOperations.Release(); UpdateAssistantControls(); }
    }

    private async Task ReconnectAssistantAsync()
    {
        if (assistantBusy || shuttingDown || !assistantRunning) return;
        bool resumeVr = assistantVrEnabled;
        await StopAssistantAsync(false);
        await ChangeAssistantAsync(resumeVr);
    }

    private async Task UpdateCaptureBorderAsync(bool enabled)
    {
        if (syncingBorder || shuttingDown) return;
        syncingBorder = true;
        try
        {
            if (borderToggle.IsOn != enabled) borderToggle.IsOn = enabled;
            if (assistantBorder.IsOn != enabled) assistantBorder.IsOn = enabled;
        }
        finally { syncingBorder = false; }
        if (settings.Border == enabled) return;
        settings.Border = enabled;
        ApplyConfiguration();
        try { if (running) await StartAsync(vrEnabled); }
        catch (Exception exception) { Report(exception); }
        await ReconnectAssistantAsync();
    }

    private async Task StopAssistantCoreAsync(bool dispose)
    {
        ++assistantEpoch;
        var previous = assistantHost; assistantHost = null;
        assistantRunning = assistantVrEnabled = false;
        if (previous != null) await previous.StopAsync();
        assistantTarget = default;
        assistantScreenshot = null;
        assistantPreview.Source = null; assistantBitmap = null;
        assistantClipboardImage?.Dispose(); assistantClipboardImage = null;
        assistantStatus.Text = "已断开";
        UpdateAssistantControls();
    }

    private async Task StopAssistantAsync(bool dispose)
    {
        await assistantOperations.WaitAsync();
        try { await StopAssistantCoreAsync(dispose); }
        finally { assistantOperations.Release(); }
    }

    private async Task TickAssistantAsync()
    {
        if (shuttingDown || assistantBusy || assistantTickBusy || !assistantRunning || assistantHost == null) return;
        assistantTickBusy = true;
        var host = assistantHost;
        long epoch = assistantEpoch;
        try
        {
            var snapshot = await host.PollAsync(selectedPage == 2, assistantPreviewSequence, assistantImageSequence, lifetime.Token, settings.AssistantWidth, settings.AssistantCaptureEye, settings.AssistantResetOnDashboard);
            if (shuttingDown || epoch != assistantEpoch || !ReferenceEquals(host, assistantHost)) return;
            var state = snapshot.Reply;
            if (state.InteractionState != lastAssistantState || state.Message != lastAssistantMessage)
            {
                assistantStatus.Text = state.Message;
                lastAssistantState = state.InteractionState; lastAssistantMessage = state.Message;
            }
            if (snapshot.Image.Length != 0 && state.ImageSequence != assistantImageSequence)
            {
                assistantScreenshot = snapshot.Image; screenshotWidth = state.ImageWidth; screenshotHeight = state.ImageHeight;
                assistantImageSequence = state.ImageSequence;
                UpdateAssistantControls(); assistantStatus.Text = "截图已就绪";
                if (settings.AssistantAutoAttach) await AttachAssistantScreenshotAsync();
            }
            if (!assistantRunning || assistantBusy || shuttingDown || selectedPage != 2) return;
            if (snapshot.Preview.Length == 0) return;
            uint width = state.PreviewWidth, height = state.PreviewHeight;
            if (assistantBitmap == null || assistantBitmap.PixelWidth != width || assistantBitmap.PixelHeight != height)
                assistantBitmap = new WriteableBitmap((int)width, (int)height);
            using (var stream = assistantBitmap.PixelBuffer.AsStream()) stream.Write(snapshot.Preview, 0, snapshot.Preview.Length);
            assistantBitmap.Invalidate(); assistantPreview.Source = assistantBitmap; assistantPreviewSequence = state.PreviewSequence;
        }
        catch (Exception exception)
        {
            if (!shuttingDown && epoch == assistantEpoch && ReferenceEquals(host, assistantHost))
            {
                await StopAssistantAsync(false);
                assistantStatus.Text = exception is OperationCanceledException ? "助手服务响应超时，已断开，可重试。" : exception.Message;
                Log.Write(exception.ToString());
            }
        }
        finally { assistantTickBusy = false; }
    }

    private async Task AttachAssistantScreenshotAsync()
    {
        if (assistantBusy || shuttingDown || !assistantRunning || assistantScreenshot == null) return;
        assistantBusy = true; UpdateAssistantControls();
        await assistantOperations.WaitAsync();
        long epoch = assistantEpoch;
        var target = assistantTarget;
        try
        {
            using var pixels = SoftwareBitmap.CreateCopyFromBuffer(assistantScreenshot.AsBuffer(), BitmapPixelFormat.Bgra8,
                (int)screenshotWidth, (int)screenshotHeight, BitmapAlphaMode.Ignore);
            var encoded = new InMemoryRandomAccessStream();
            try
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
                encoder.SetSoftwareBitmap(pixels); await encoder.FlushAsync(); encoded.Seek(0);
                if (shuttingDown || epoch != assistantEpoch) return;
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(encoded));
                bool clipboardSet = false;
                for (int attempt = 0; attempt < 5 && !clipboardSet && !shuttingDown; ++attempt)
                {
                    try
                    {
                        Clipboard.SetContent(package);
                        Clipboard.Flush();
                        clipboardSet = true;
                    }
                    catch (Exception clipboardException)
                    {
                        if (attempt == 4) Log.Write(clipboardException.ToString());
                        else await Task.Delay(25);
                    }
                }
                if (!clipboardSet) throw new InvalidOperationException("无法打开系统剪贴板放置截图。");
                await Task.Delay(50);
                assistantClipboardImage?.Dispose(); assistantClipboardImage = encoded; encoded = null!;
            }
            finally { encoded?.Dispose(); }
            if (shuttingDown || epoch != assistantEpoch) return;
            var error = new BridgeError { Size = (uint)Marshal.SizeOf<BridgeError>(), Text = "" };
            int result = NativeBridge.AssistantPaste(target, ref error);
            if (result < 0) throw new COMException(error.Text, result);
            assistantStatus.Text = "截图已自动放入豆包发送框";
        }
        catch (Exception exception) { if (!shuttingDown) { assistantStatus.Text = exception.Message; Log.Write(exception.ToString()); } }
        finally { assistantBusy = false; assistantOperations.Release(); UpdateAssistantControls(); }
    }

    private async Task ResetAssistantPositionAsync()
    {
        if (assistantBusy || shuttingDown || !assistantRunning || !assistantVrEnabled || assistantHost == null) return;
        try
        {
            await assistantHost.ResetPositionAsync(lifetime.Token);
            assistantStatus.Text = "已重置浮窗位置到视野右侧";
        }
        catch (Exception exception)
        {
            if (!shuttingDown)
            {
                assistantStatus.Text = exception.Message;
                Log.Write(exception.ToString());
            }
        }
    }
}
