using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow : Window
{
    private readonly Settings settings = Settings.Load();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherQueueTimer timer;
    private readonly Grid root = new();
    private readonly ComboBox sources = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 260 };
    private readonly TextBlock status = new() { Text = "未连接", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7 };
    private readonly Image preview = new() { Stretch = Stretch.Uniform, MaxHeight = 180 };
    private readonly TextBlock placeholder = new() { Text = "暂无字幕", Opacity = .5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button vrButton = new() { Content = "开启VR", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button captureButton = new() { Content = "连接预览", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch borderToggle = new() { Header = "显示捕获黄框" };
    private readonly ToggleSwitch compatibilityToggle = new() { Header = "兼容模式" };
    private readonly TextBlock compatibilityNotice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75, Visibility = Visibility.Collapsed };
    private readonly ToggleSwitch autoRestartToggle = new() { Header = "定时自动重启字幕窗口" };
    private readonly byte[] previewBuffer = new byte[640 * 240 * 4];
    private readonly PreviewBuffer nativePreview = new();
    private BridgeHandle? bridge;
    private CaptionWindow? connected;
    private bool running, vrEnabled, changingSources, shuttingDown, closeReady, previewVisible = true, tickBusy, reconnecting, reconnectVr, syncingCompatibility;
    private int selectedPage;
    private ulong previewSequence;
    private WriteableBitmap? bitmap;
    private DateTime nextDiscovery = DateTime.MinValue;
    private readonly bool smokeTest;
    private bool curvatureWarningReported;
    private readonly TitleBarAppearance titleBar;
    private readonly DateTime opened = DateTime.UtcNow;

    public MainWindow(bool smokeTest)
    {
        this.smokeTest = smokeTest;
        bool versionChanged = !string.Equals(settings.CheckedAppVersion, Settings.CurrentAppVersion, StringComparison.Ordinal);
        if (versionChanged)
        {
            if (Settings.IsBelowFullSupport) settings.CompatibilityMode = true;
            settings.CheckedAppVersion = Settings.CurrentAppVersion;
        }
        preferences = new(DispatcherQueue, ConfigureAsync, PersistSettingsAsync, PreferenceFailure);
        Title = "豆包VR字幕";
        AppWindow.Resize(new SizeInt32(590, 740));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 520;
            presenter.PreferredMinimumHeight = 600;
        }
        var icon = Path.Combine(AppContext.BaseDirectory, "bridge.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        SystemBackdrop = new MicaBackdrop();
        accent = new AccentTheme(root);
        accent.Apply(settings.AccentColor);
        background.ImageFailed += BackgroundFailed;
        Build();
        surface.Children.Add(background);
        surface.Children.Add(root);
        Content = surface;
        titleBar = new TitleBarAppearance(this, root);
        ApplyTheme();
        AppWindow.Changed += (_, arguments) =>
        {
            if (!arguments.DidPresenterChange) return;
            previewVisible = selectedPage == 0 && (AppWindow.Presenter is not OverlappedPresenter current || current.State != OverlappedPresenterState.Minimized);
            RequestLiveConfiguration();
        };
        AppWindow.Closing += OnClosing;
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += Tick;
        timer.Start();
        try
        {
            NativeBridge.EnsureVersion();
            bridge = new BridgeHandle();
            if (bridge.IsInvalid) throw new InvalidOperationException("无法创建原生核心。");
            NativeBridge.SetCompatibility(bridge, settings.EffectiveCompatibilityMode ? 1u : 0);
        }
        catch (Exception exception) { Report(new InvalidOperationException("原生核心尚未就绪，请运行 build.ps1 完成构建。" + exception.Message)); }
        if (versionChanged) ApplyConfiguration();
        if (Settings.LoadWarning != null) status.Text = Settings.LoadWarning;
        RefreshSources();
        ApplyBackground();
        _ = RefreshAudioAsync();
        _ = InitializeMicrophoneAsync();
        InitializeAssistant();
    }

    private static TextBlock Heading(string text, double size = 18) => new() { Text = text, FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private static StackPanel Column(double spacing = 12) => new() { Spacing = spacing };
    private static StackPanel Row(double spacing = 10) => new() { Spacing = spacing, Orientation = Orientation.Horizontal };

    private Border Card(UIElement content) => new()
    {
        Child = content, Style = (Style)Application.Current.Resources["CardStyle"]
    };

    private void Build()
    {
        root.Padding = new Thickness(20, 12, 20, 16);
        root.RowSpacing = 14;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var home = Column(12);
        var controls = Column();
        var actions = EqualColumns(2);
        launchButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        launchButton.Click += async (_, _) => await ToggleCaptionAsync();
        captureButton.Click += async (_, _) =>
        {
            try { if (running) await StopAsync(); else await StartAsync(false); }
            catch (Exception exception) { Report(exception); }
        };
        vrButton.Click += async (_, _) =>
        {
            try
            {
                if (!vrEnabled) RefreshSources();
                if (!vrEnabled && (sources.SelectedItem is not CaptionWindow source || !WindowSource.Same(source) || !source.IsCaption))
                {
                    await ToggleCaptionAsync(true);
                    return;
                }
                await StartAsync(!vrEnabled);
            }
            catch (Exception exception) { Report(exception); }
        };
        AddCell(actions, launchButton, 0);
        AddCell(actions, vrButton, 1);
        controls.Children.Add(actions);
        var imagePanel = new Grid { MinHeight = 110, MaxHeight = 150 };
        imagePanel.Children.Add(placeholder);
        imagePanel.Children.Add(preview);
        controls.Children.Add(new Border { Child = imagePanel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128)) });
        controls.Children.Add(status);
        home.Children.Add(Card(controls));
        var placement = Column(16);
        placement.Children.Add(Slider("字幕大小", .25, 2.5, .05, settings.Width, value => settings.Width = (float)value, value => $"{value * 100:F0}%",
            ("小", .55), ("标准", 1), ("大", 1.5)));
        placement.Children.Add(Slider("距离", .4, 3, .1, settings.Distance, value => settings.Distance = (float)value, value => $"{value:F1} 米",
            ("近", .8), ("适中", 1.2), ("远", 1.8)));
        home.Children.Add(Card(placement));
        var advanced = Column(12);
        var position = Column(16);
        position.Children.Add(Slider("左右", -1.2, 1.2, .05, settings.Horizontal, value => settings.Horizontal = (float)value, value => $"{value:F2} 米"));
        position.Children.Add(Slider("上下", -1.2, .8, .05, settings.Vertical, value => settings.Vertical = (float)value, value => $"{value:F2} 米"));
        position.Children.Add(Slider("上下倾斜", -45, 45, 1, settings.Pitch, value => settings.Pitch = (float)value, value => $"{value:F0}°", ("归零", 0)));
        position.Children.Add(Slider("左右倾斜", -45, 45, 1, settings.Yaw, value => settings.Yaw = (float)value, value => $"{value:F0}°", ("归零", 0)));
        position.Children.Add(Slider("不透明度", .1, 1, .05, settings.Opacity, value => settings.Opacity = (float)value, value => $"{value * 100:F0}%"));
        advanced.Children.Add(Card(position));
        var curvature = Column();
        curvature.Children.Add(Slider("曲面程度", 0, 90, 5, settings.CurvatureDegrees, value => settings.CurvatureDegrees = (float)value,
            value => $"{value:F0}°", ("平面", 0), ("轻微", 30), ("标准", 60)));
        advanced.Children.Add(new SettingsExpander
        {
            Style = (Style)Application.Current.Resources["SettingsExpanderStyle"],
            Header = "曲面", Content = curvature, IsExpanded = false
        });
        var preferences = Column(12);
        var sourceCard = Column();
        sources.Header = "捕获窗口";
        sourceCard.Children.Add(sources);
        var sourceActions = EqualColumns(2);
        var refresh = new Button { Content = "刷新", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        refresh.Click += (_, _) => RefreshSources();
        AddCell(sourceActions, refresh, 0);
        AddCell(sourceActions, captureButton, 1);
        sourceCard.Children.Add(sourceActions);
        sources.SelectionChanged += async (_, _) =>
        {
            if (changingSources || !running || shuttingDown || captions.IsBusy) return;
            try { await StartAsync(vrEnabled); } catch (Exception exception) { Report(exception); }
        };
        audioSection = SettingsSection("音频设备", AudioSettings());
        preferences.Children.Add(audioSection);
        var behavior = Column();
        var minimizeOnClose = new ToggleSwitch { Header = "关闭时自动最小化", IsOn = settings.MinimizeOnClose };
        minimizeOnClose.Toggled += (_, _) => { settings.MinimizeOnClose = minimizeOnClose.IsOn; ApplyConfiguration(); };
        behavior.Children.Add(minimizeOnClose);
        var closeDoubao = new ToggleSwitch { Header = "退出时同时关闭豆包", IsOn = settings.ExitCloseDoubao };
        closeDoubao.Toggled += (_, _) => { settings.ExitCloseDoubao = closeDoubao.IsOn; ApplyConfiguration(); };
        behavior.Children.Add(closeDoubao);
        behavior.Children.Add(SteamVrStartupSettings());
        var imageSettings = Column();
        imageSettings.Children.Add(Slider("顶部裁剪", 0, 120, 1, settings.CropTop, value => settings.CropTop = (float)value, value => $"{value:F0} 像素",
            ("完整", 0), ("隐藏工具栏", 44)));
        var trim = new ToggleSwitch { Header = "裁去透明边缘", IsOn = settings.Trim };
        trim.Toggled += (_, _) => { settings.Trim = trim.IsOn; ApplyConfiguration(); };
        imageSettings.Children.Add(trim);
        sourceCard.Children.Add(imageSettings);
        var appearance = Column();
        var theme = new ComboBox { Header = "主题", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = settings.Appearance == "light" ? 1 : settings.Appearance == "dark" ? 2 : 0 };
        theme.SelectionChanged += (_, _) => { settings.Appearance = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system"; ApplyTheme(); ApplyConfiguration(); };
        appearance.Children.Add(theme);
        appearance.Children.Add(ColorSettings());
        borderToggle.IsOn = !settings.EffectiveCompatibilityMode && settings.Border;
        borderToggle.Toggled += async (_, _) => await UpdateCaptureBorderAsync(borderToggle.IsOn);
        sourceCard.Children.Add(borderToggle);
        compatibilityToggle.IsOn = settings.EffectiveCompatibilityMode;
        compatibilityToggle.Toggled += async (_, _) => await UpdateCompatibilityModeAsync(compatibilityToggle.IsOn);
        sourceCard.Children.Add(compatibilityToggle);
        sourceCard.Children.Add(compatibilityNotice);
        autoRestartToggle.IsOn = settings.AutoRestartCaptions;
        autoRestartToggle.Toggled += (_, _) => { settings.AutoRestartCaptions = autoRestartToggle.IsOn; ResetAutoRestartSchedule(); ApplyConfiguration(); };
        sourceCard.Children.Add(autoRestartToggle);
        sourceCard.Children.Add(Slider("重启间隔时间（分钟）", 0, 120, 5, settings.AutoRestartIntervalMinutes,
            value => { settings.AutoRestartIntervalMinutes = (float)Math.Round(value); ResetAutoRestartSchedule(); },
            value => value <= 0 ? "已停用（0 分钟）" : $"{value:F0} 分钟", ("15分", 15), ("30分", 30), ("60分", 60)));
        appearance.Children.Add(BackgroundSettings());
        preferences.Children.Add(SettingsSection("主题与背景", appearance));
        preferences.Children.Add(SettingsSection("字幕捕获", sourceCard));
        preferences.Children.Add(SettingsSection("程序设置", behavior));
        preferences.Children.Add(SettingsSection("关于", AboutSettings()));
        var pageHost = new Grid();
        var navigation = EqualColumns(4);
        var pageButtons = new List<ToggleButton>();
        var pages = new List<ScrollViewer>();
        var entries = new[] { ("字幕", home), ("更多调节", advanced), ("助手", AssistantPage()), ("设置", preferences) };
        showPage = currentIndex =>
        {
            selectedPage = currentIndex;
            for (int index = 0; index < pages.Count; index++)
            {
                pageButtons[index].IsChecked = index == currentIndex;
                pages[index].Visibility = index == currentIndex ? Visibility.Visible : Visibility.Collapsed;
            }
            previewVisible = selectedPage == 0 && (AppWindow.Presenter is not OverlappedPresenter current || current.State != OverlappedPresenterState.Minimized);
            RequestLiveConfiguration();
        };
        for (int pageIndex = 0; pageIndex < entries.Length; pageIndex++)
        {
            int currentIndex = pageIndex;
            var button = new ToggleButton { Content = entries[pageIndex].Item1, IsChecked = pageIndex == 0, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
            var page = new ScrollViewer { Content = entries[pageIndex].Item2, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = pageIndex == 0 ? Visibility.Visible : Visibility.Collapsed };
            pageButtons.Add(button);
            pages.Add(page);
            AddCell(navigation, button, pageIndex);
            pageHost.Children.Add(page);
            button.Click += (_, _) => showPage(currentIndex);
        }
        root.Children.Add(navigation);
        Grid.SetRow(pageHost, 1);
        root.Children.Add(pageHost);
        var exit = new Button { Content = "退出", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        exit.Click += (_, _) => RequestExit();
        var footer = EqualColumns(2);
        restoreAudio.Click += async (_, _) => await RestoreAudioAsync();
        AddCell(footer, restoreAudio, 0);
        AddCell(footer, exit, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        UpdateCompatibilityUi();
    }

    private void UpdateCompatibilityUi()
    {
        bool compat = settings.EffectiveCompatibilityMode;
        bool forced = Settings.IsBelowFullSupport;
        string noticeText = Settings.CompatibilityPromptText;
        syncingCompatibility = true;
        syncingBorder = true;
        try
        {
            compatibilityToggle.IsOn = compat;
            compatibilityToggle.IsEnabled = !forced;
            borderToggle.IsEnabled = !compat;
            borderToggle.IsOn = !compat && settings.Border;
            assistantBorder.IsEnabled = !compat;
            assistantBorder.IsOn = !compat && settings.Border;
            compatibilityNotice.Text = noticeText;
            compatibilityNotice.Visibility = compat ? Visibility.Visible : Visibility.Collapsed;
            assistantCompatibilityNotice.Text = noticeText;
            assistantCompatibilityNotice.Visibility = compat ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            syncingBorder = false;
            syncingCompatibility = false;
        }
    }

    private async Task UpdateCompatibilityModeAsync(bool enabled)
    {
        if (syncingCompatibility || shuttingDown) return;
        if (Settings.IsBelowFullSupport)
        {
            UpdateCompatibilityUi();
            return;
        }
        if (settings.CompatibilityMode == enabled) return;
        settings.CompatibilityMode = enabled;
        UpdateCompatibilityUi();
        if (bridge != null) NativeBridge.SetCompatibility(bridge, settings.EffectiveCompatibilityMode ? 1u : 0);
        ApplyConfiguration();
        try { if (running) await StartAsync(vrEnabled); }
        catch (Exception exception) { Report(exception); }
        await ReconnectAssistantAsync();
    }

    private static Grid EqualColumns(int count)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        for (int index = 0; index < count; index++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static void AddCell(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private UIElement Slider(string title, double minimum, double maximum, double step, double value, Action<double> set, Func<double, string> format,
        params (string Label, double Value)[] presets)
    {
        var panel = Column(6);
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = title });
        var label = new TextBlock { Text = format(value), HorizontalAlignment = HorizontalAlignment.Right, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        header.Children.Add(label);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, StepFrequency = step, Value = value, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(slider, title);
        var buttons = new List<(Button Button, double Value)>();
        void Update()
        {
            label.Text = format(slider.Value);
            foreach (var entry in buttons) entry.Button.Style = Math.Abs(entry.Value - slider.Value) < step / 2
                ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];
        }
        slider.ValueChanged += (_, arguments) => { set(arguments.NewValue); Update(); ApplyConfiguration(); };
        var adjustment = new Grid { ColumnSpacing = 12 };
        adjustment.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        adjustment.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        adjustment.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        var minus = new RepeatButton { Content = "−", FontSize = 24, MinHeight = 44, Delay = 400, Interval = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
        var plus = new RepeatButton { Content = "+", FontSize = 24, MinHeight = 44, Delay = 400, Interval = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(minus, $"减小{title}");
        AutomationProperties.SetName(plus, $"增大{title}");
        minus.Click += (_, _) => slider.Value = Math.Round(Math.Clamp(slider.Value - step, minimum, maximum), 3);
        plus.Click += (_, _) => slider.Value = Math.Round(Math.Clamp(slider.Value + step, minimum, maximum), 3);
        AddCell(adjustment, minus, 0);
        AddCell(adjustment, slider, 1);
        AddCell(adjustment, plus, 2);
        panel.Children.Add(header);
        panel.Children.Add(adjustment);
        if (presets.Length > 0)
        {
            var presetRow = EqualColumns(presets.Length);
            for (int index = 0; index < presets.Length; index++)
            {
                var preset = presets[index];
                var button = new Button { Content = preset.Label, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
                button.Click += (_, _) => slider.Value = preset.Value;
                buttons.Add((button, preset.Value));
                AddCell(presetRow, button, index);
            }
            panel.Children.Add(presetRow);
        }
        Update();
        return panel;
    }

    private void ApplyTheme()
    {
        var theme = settings.Appearance == "light" ? ElementTheme.Light : settings.Appearance == "dark" ? ElementTheme.Dark : ElementTheme.Default;
        surface.RequestedTheme = root.RequestedTheme = theme;
        titleBar?.Apply();
    }
    private async Task ConfigureAsync()
    {
        if (!running || bridge == null || shuttingDown) return;
        await operations.WaitAsync();
        try
        {
            if (shuttingDown || !running) return;
            var options = settings.Options(vrEnabled, previewVisible);
            options.Border = captureBorder ? 1u : 0;
            await Task.Run(() => Marshal.ThrowExceptionForHR(NativeBridge.Configure(bridge, options)));
        }
        finally { operations.Release(); }
    }

    private void RefreshSources(nint preferred = default)
    {
        if (shuttingDown) return;
        changingSources = true;
        try
        {
            var previous = preferred != 0 ? preferred : (sources.SelectedItem as CaptionWindow)?.Handle;
            var windows = WindowSource.Discover();
            sources.ItemsSource = windows;
            sources.SelectedItem = windows.FirstOrDefault(window => window.Handle == previous)
                ?? windows.FirstOrDefault(window => window.Score >= 150) ?? windows.FirstOrDefault();
        }
        finally { changingSources = false; }
    }

    private async Task StartAsync(bool enableVr)
    {
        if (shuttingDown || captions.IsClosing) return;
        long requestedEpoch = captureEpoch;
        if (bridge == null) throw new InvalidOperationException("请先构建原生核心 DLL。");
        if (sources.SelectedItem is not CaptionWindow source) throw new InvalidOperationException("请先打开豆包字幕并选择捕获来源。");
        if (!WindowSource.Same(source)) throw new InvalidOperationException("字幕来源窗口已变化，请刷新来源后重新选择。");
        var options = settings.Options(enableVr, previewVisible);
        await operations.WaitAsync();
        try
        {
            if (shuttingDown || captions.IsClosing || requestedEpoch != captureEpoch) return;
            captureChanging = true;
            UpdateCaptionControls();
            captureButton.IsEnabled = vrButton.IsEnabled = false;
            status.Text = "正在连接捕获…";
            Log.Write($"Capture request: class={source.ClassName}, size={source.Width}x{source.Height}, vr={enableVr}, border={settings.EffectiveBorder}, compat={settings.EffectiveCompatibilityMode}.");
            NativeBridge.SetCompatibility(bridge, settings.EffectiveCompatibilityMode ? 1u : 0);
            await Task.Run(() => NativeBridge.StartCapture(bridge, (nuint)source.Handle, options));
            running = true;
            reconnecting = false;
            vrEnabled = enableVr;
            captureBorder = options.Border != 0;
            connected = source;
            previewSequence = 0;
            ResetAutoRestartSchedule();
            captureButton.Content = "停止连接";
            vrButton.Content = enableVr ? "关闭VR" : "开启VR";
            RequestLiveConfiguration();
        }
        finally { captureChanging = false; UpdateCaptionControls(); operations.Release(); }
    }

    private async Task StopAsync()
    {
        ++captureEpoch;
        await operations.WaitAsync();
        try
        {
            if (bridge != null) await Task.Run(() => NativeBridge.Stop(bridge));
            running = vrEnabled = false;
            reconnecting = false;
            connected = null;
            ResetAutoRestartSchedule();
            captureButton.Content = "连接预览";
            vrButton.Content = "开启VR";
            preview.Source = null;
            placeholder.Visibility = Visibility.Visible;
            status.Text = "已停止";
        }
        finally { operations.Release(); }
    }

    private async void Tick(DispatcherQueueTimer sender, object arguments)
    {
        if (shuttingDown || tickBusy) return;
        if (Program.RestoreEvent?.WaitOne(0) == true)
        {
            (AppWindow.Presenter as OverlappedPresenter)?.Restore();
            Activate();
        }
        if (smokeTest && DateTime.UtcNow - opened > TimeSpan.FromSeconds(3)) { RequestExit(); return; }
        tickBusy = true;
        try
        {
            await RefreshCaptionAsync();
            if (shuttingDown || captions.IsBusy || autoRestarting) return;
            if (!running && reconnecting && DateTime.UtcNow >= nextDiscovery)
            {
                nextDiscovery = DateTime.UtcNow.AddSeconds(3);
                RefreshSources();
                if (sources.SelectedItem is CaptionWindow candidate && candidate.Score >= 150) await StartAsync(reconnectVr);
            }
            if (!running || bridge == null) return;
            if (settings.AutoRestartCaptions && settings.AutoRestartIntervalMinutes > 0 && DateTime.UtcNow >= nextAutoRestart)
            {
                await AutoRestartCaptionWindowAsync();
                if (!running || bridge == null || shuttingDown) return;
            }
            var stats = NativeBridge.Stats(bridge);
            if (stats.State == 3)
            {
                var message = NativeBridge.Error(bridge);
                bool reconnect = connected != null && !WindowSource.Same(connected);
                reconnectVr = vrEnabled;
                await StopAsync();
                status.Text = message;
                Log.Write(message);
                if (reconnect && !shuttingDown && !captions.IsBusy) { reconnecting = true; nextDiscovery = DateTime.UtcNow.AddSeconds(3); }
                return;
            }
            if (vrEnabled && stats.CurvatureFallback != 0)
            {
                status.Text = "曲面不可用，已恢复平面字幕。";
                if (!curvatureWarningReported) { curvatureWarningReported = true; Log.Write(NativeBridge.Error(bridge)); }
            }
            else
            {
                curvatureWarningReported = false;
                status.Text = stats.State == 1 ? "等待字幕…" : vrEnabled ? "VR已开启" : "已连接";
            }
            if (!previewVisible) return;
            uint count = NativeBridge.Preview(bridge, nativePreview, previewBuffer, previewSequence, out uint width, out uint height, out ulong sequence);
            if (count == 0 || count > previewBuffer.Length || sequence == previewSequence) return;
            if (bitmap == null || bitmap.PixelWidth != width || bitmap.PixelHeight != height) bitmap = new WriteableBitmap((int)width, (int)height);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(previewBuffer, 0, (int)count);
            bitmap.Invalidate();
            preview.Source = bitmap;
            placeholder.Visibility = Visibility.Collapsed;
            previewSequence = sequence;
        }
        catch (OperationCanceledException) when (shuttingDown) { }
        catch (Exception exception) { if (!shuttingDown) Report(exception); }
        finally { tickBusy = false; }
    }

    private void RequestExit() => _ = ShutdownAsync(ClosePolicy.Decide(settings, true, smokeTest));

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs arguments)
    {
        if (closeReady) return;
        arguments.Cancel = true;
        if (shuttingDown) return;
        var policy = ClosePolicy.Decide(settings, false, smokeTest);
        if (policy.Minimize)
        {
            (AppWindow.Presenter as OverlappedPresenter)?.Minimize();
            return;
        }
        _ = ShutdownAsync(policy);
    }

    private async Task ShutdownAsync(ClosePolicy policy)
    {
        if (shuttingDown) return;
        shuttingDown = true;
        activeDialog?.Hide();
        accent.Dispose();
        titleBar.Dispose();
        background.ImageFailed -= BackgroundFailed;
        background.Source = null;
        Log.Write("Shutdown requested; releasing native core.");
        lifetime.Cancel();
        timer.Stop();
        assistantTimer?.Stop();
        root.IsHitTestVisible = false;
        status.Text = "正在移除 VR 悬浮并退出…";
        try
        {
            await preferences.StopAsync();
            await StopAssistantAsync(true);
            await operations.WaitAsync();
            try { if (bridge != null) await Task.Run(() => bridge.Dispose()); }
            finally { operations.Release(); }
            Log.Write("Native core release completed.");
            try { await backgroundImport; } catch (Exception exception) { Log.Write(exception.ToString()); }
            try { await PersistSettingsAsync(); } catch (Exception exception) { Log.Write(exception.ToString()); }
            if (policy.CloseDoubao)
            {
                try { await Task.Run(DoubaoExitService.CloseAsync); }
                catch (Exception exception)
                {
                    Log.Write(exception.ToString());
                    await new ContentDialog { Title = "豆包未完全关闭", Content = "请从豆包托盘手动退出。", CloseButtonText = "确定", XamlRoot = root.XamlRoot }.ShowAsync();
                }
            }
        }
        catch (Exception exception) { Log.Write(exception.ToString()); }
        finally { settingsStore.Dispose(); nativePreview.Dispose(); closeReady = true; Log.Write("Window closed; native core disposed."); Close(); }
    }

    private void Report(Exception exception) { status.Text = exception.Message; Log.Write(exception.ToString()); }
}
