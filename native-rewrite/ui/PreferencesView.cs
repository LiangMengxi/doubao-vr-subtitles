using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow
{
    private readonly AccentTheme accent;
    private readonly Grid surface = new();
    private readonly Image background = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly ComboBox audioTargets = new() { Header = "播放设备", PlaceholderText = "不更改", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox microphoneTargets = new() { Header = "麦克风", PlaceholderText = "不更改", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<SettingsExpander> settingsSections = new();
    private SettingsExpander? audioSection;
    private readonly Button refreshAudio = new() { Content = "刷新设备", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button restoreAudio = new() { Content = "恢复音频", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button removeBackground = new() { Content = "移除图片", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    private Action<int>? showPage;
    private ContentDialog? activeDialog;
    private bool refreshingAudio;
    private bool restoringAudio;
    private long backgroundRevision;
    private Task backgroundImport = Task.CompletedTask;

    private SettingsExpander SettingsSection(string title, UIElement content)
    {
        var section = new SettingsExpander { Style = (Style)Application.Current.Resources["SettingsExpanderStyle"], Header = title, Content = content };
        section.Expanding += (expanded, _) =>
        {
            foreach (var other in settingsSections)
                if (other != expanded) other.IsExpanded = false;
        };
        settingsSections.Add(section);
        return section;
    }

    private UIElement AudioSettings()
    {
        var panel = Column();
        panel.Children.Add(audioTargets);
        audioTargets.SelectionChanged += (_, _) =>
        {
            if (refreshingAudio || shuttingDown || audioTargets.SelectedItem is not AudioDevice device) return;
            settings.AudioTargetId = device.Id;
            settings.AudioTargetName = device.Name;
            ApplyConfiguration();
        };
        panel.Children.Add(microphoneTargets);
        microphoneTargets.SelectionChanged += (_, _) =>
        {
            if (refreshingAudio || shuttingDown || microphoneTargets.SelectedItem is not AudioDevice device) return;
            settings.MicrophoneTargetId = device.Id;
            settings.MicrophoneTargetName = device.Name;
            ApplyConfiguration();
        };
        refreshAudio.Click += async (_, _) => await RefreshAudioAsync(true);
        panel.Children.Add(refreshAudio);
        panel.Children.Add(PicoMicrophoneSettings());
        return panel;
    }

    private async Task RefreshAudioAsync(bool report = false)
    {
        if (refreshingAudio || shuttingDown) return;
        refreshingAudio = true;
        refreshAudio.IsEnabled = audioTargets.IsEnabled = microphoneTargets.IsEnabled = false;
        try
        {
            var devices = await Task.Run(() => (Playback: AudioService.Devices(AudioFlow.Playback), Microphone: AudioService.Devices(AudioFlow.Microphone)));
            if (shuttingDown) return;
            UpdateAudioChoices(audioTargets, devices.Playback, settings.AudioTargetId, settings.AudioTargetName);
            UpdateAudioChoices(microphoneTargets, devices.Microphone, settings.MicrophoneTargetId, settings.MicrophoneTargetName);
        }
        catch (Exception exception)
        {
            Log.Write(exception.ToString());
            audioTargets.PlaceholderText = microphoneTargets.PlaceholderText = "设备读取失败，请刷新";
            if (report && !shuttingDown) await ShowErrorAsync("无法读取音频设备", exception.Message);
        }
        finally { refreshingAudio = false; refreshAudio.IsEnabled = audioTargets.IsEnabled = microphoneTargets.IsEnabled = !shuttingDown; }
    }

    private static void UpdateAudioChoices(ComboBox choices, List<AudioDevice> devices, string identifier, string name)
    {
        devices.Insert(0, new AudioDevice("", "不更改", 0));
        var selected = devices.FirstOrDefault(device => device.Id == identifier);
        if (selected == null)
        {
            selected = new AudioDevice(identifier, string.IsNullOrEmpty(name) ? "已保存的设备" : name, 0, false);
            devices.Add(selected);
        }
        choices.ItemsSource = devices;
        choices.SelectedItem = selected;
    }

    private async Task RestoreAudioAsync()
    {
        if (restoringAudio || shuttingDown) return;
        if (string.IsNullOrEmpty(settings.AudioTargetId) && string.IsNullOrEmpty(settings.MicrophoneTargetId))
        {
            showPage?.Invoke(2);
            if (audioSection != null) audioSection.IsExpanded = true;
            DispatcherQueue.TryEnqueue(() => { audioTargets.StartBringIntoView(); audioTargets.Focus(FocusState.Programmatic); });
            return;
        }
        restoringAudio = true;
        restoreAudio.IsEnabled = false;
        restoreAudio.Content = "正在恢复…";
        Exception? failure = null;
        string target = settings.AudioTargetId;
        string microphone = settings.MicrophoneTargetId;
        await operations.WaitAsync();
        try { if (!shuttingDown) await Task.Run(() => AudioService.Restore(target, microphone)); }
        catch (Exception exception) { failure = exception; Log.Write(exception.ToString()); }
        finally { operations.Release(); }
        try
        {
            if (shuttingDown) return;
            if (failure != null) await ShowErrorAsync("无法恢复音频", "请确认目标播放设备和麦克风已连接，或在设置中重新选择。\n" + failure.Message);
            else
            {
                restoreAudio.Content = "已恢复";
                await Task.Delay(1500, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            restoringAudio = false;
            if (!shuttingDown) { restoreAudio.Content = "恢复音频"; restoreAudio.IsEnabled = true; }
        }
    }

    private UIElement ColorSettings()
    {
        var panel = Column(8);
        panel.Children.Add(new TextBlock { Text = "主题色" });
        var choices = EqualColumns(5);
        var colors = new[] { ("系统", ""), ("蓝", "#0067C0"), ("紫", "#744DA9"), ("绿", "#0F7B0F") };
        for (int index = 0; index < colors.Length; index++)
        {
            var choice = colors[index];
            var button = new Button { Content = choice.Item1, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => { settings.AccentColor = choice.Item2; accent.Apply(settings.AccentColor); ApplyConfiguration(); };
            AddCell(choices, button, index);
        }
        var custom = new Button { Content = "自定义", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        custom.Click += async (_, _) =>
        {
            if (activeDialog != null || shuttingDown) return;
            var picker = new ColorPicker { Color = accent.Current, IsAlphaEnabled = false, IsHexInputVisible = true, MaxWidth = 340 };
            var dialog = new ContentDialog { Title = "主题色", Content = new ScrollViewer { Content = picker, MaxHeight = 400 }, PrimaryButtonText = "确定", CloseButtonText = "取消", XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme };
            activeDialog = dialog;
            try
            {
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || shuttingDown) return;
                settings.AccentColor = new Rgb(picker.Color.R, picker.Color.G, picker.Color.B).Hex;
                accent.Apply(settings.AccentColor);
                ApplyConfiguration();
            }
            catch (Exception exception) { Report(exception); }
            finally { activeDialog = null; }
        };
        AddCell(choices, custom, 4);
        panel.Children.Add(choices);
        return panel;
    }

    private UIElement BackgroundSettings()
    {
        var panel = Column();
        var actions = EqualColumns(2);
        var choose = new Button { Content = "选择背景图片", MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        choose.Click += async (_, _) =>
        {
            choose.IsEnabled = false;
            try { await ChooseBackgroundAsync(); }
            catch (Exception exception) { Log.Write(exception.ToString()); if (!shuttingDown) await ShowErrorAsync("无法设置背景", exception.Message); }
            finally { if (!shuttingDown) choose.IsEnabled = true; }
        };
        removeBackground.Click += (_, _) =>
        {
            ++backgroundRevision;
            settings.BackgroundImage = "";
            ApplyBackground();
            ApplyConfiguration();
        };
        AddCell(actions, choose, 0);
        AddCell(actions, removeBackground, 1);
        panel.Children.Add(actions);
        panel.Children.Add(Slider("背景不透明度", 0, 1, .05, settings.BackgroundOpacity, value =>
        {
            settings.BackgroundOpacity = (float)value;
            background.Opacity = value;
        }, value => $"{value * 100:F0}%"));
        return panel;
    }

    private async Task ChooseBackgroundAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file == null || shuttingDown) return;
        backgroundImport = ImportBackgroundAsync(file.Path, ++backgroundRevision);
        await backgroundImport;
    }

    private async Task ImportBackgroundAsync(string path, long revision)
    {
        await using var storage = await settingsStore.AcquireAsync(lifetime.Token);
        if (shuttingDown || revision != backgroundRevision) return;
        string image = await storage.ImportAsync(path, settings.BackgroundImage);
        if (shuttingDown || revision != backgroundRevision) return;
        string previous = settings.BackgroundImage;
        settings.BackgroundImage = image;
        try
        {
            await storage.SaveAsync(settings.Snapshot());
        }
        catch
        {
            if (revision == backgroundRevision) settings.BackgroundImage = previous;
            throw;
        }
        if (!shuttingDown && revision == backgroundRevision) ApplyBackground();
    }

    private void ApplyBackground()
    {
        background.Opacity = settings.BackgroundOpacity;
        removeBackground.IsEnabled = !string.IsNullOrEmpty(settings.BackgroundImage);
        if (string.IsNullOrEmpty(settings.BackgroundImage)) { background.Source = null; return; }
        try
        {
            string path = BackgroundFiles.Resolve(Settings.DataDirectory, settings.BackgroundImage);
            if (!File.Exists(path)) throw new FileNotFoundException("背景图片不可用，请重新选择。");
            var image = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache, UriSource = new Uri(path) };
            background.Source = image;
        }
        catch (Exception exception) { background.Source = null; Report(exception); }
    }

    private void BackgroundFailed(object sender, ExceptionRoutedEventArgs arguments)
    {
        if (shuttingDown) return;
        background.Source = null;
        status.Text = "背景图片无法显示，请重新选择。";
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (shuttingDown || activeDialog != null) return;
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "确定", XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme };
        activeDialog = dialog;
        try { await dialog.ShowAsync(); }
        catch (Exception exception) { Log.Write(exception.ToString()); }
        finally { activeDialog = null; }
    }
}
