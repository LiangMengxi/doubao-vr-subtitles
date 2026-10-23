using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow
{
    private readonly ToggleSwitch picoKeeper = new() { Header = "改善PICO麦克风延迟" };
    private readonly ToggleSwitch steamVrStartup = new() { Header = "随SteamVR启动", OnContent = "开", OffContent = "关" };
    private bool changingPico;
    private bool changingStartup;

    private UIElement PicoMicrophoneSettings()
    {
        picoKeeper.IsOn = settings.PicoMicrophoneKeeper;
        picoKeeper.IsEnabled = false;
        picoKeeper.Toggled += async (_, _) =>
        {
            if (!changingPico && !shuttingDown) await ChangePicoMicrophoneAsync(picoKeeper.IsOn);
        };
        return picoKeeper;
    }

    private UIElement SteamVrStartupSettings()
    {
        steamVrStartup.IsOn = settings.SteamVrAutoStart;
        steamVrStartup.Toggled += async (_, _) =>
        {
            if (!changingStartup && !shuttingDown) await ChangeSteamVrStartupAsync(steamVrStartup.IsOn);
        };
        return steamVrStartup;
    }

    private async Task InitializeMicrophoneAsync()
    {
        picoKeeper.IsEnabled = bridge is { IsInvalid: false };
        if (!smokeTest && settings.PicoMicrophoneKeeper && picoKeeper.IsEnabled) await ChangePicoMicrophoneAsync(true, false);
    }

    private async Task ChangePicoMicrophoneAsync(bool enabled, bool report = true)
    {
        if (changingPico || shuttingDown) return;
        changingPico = true;
        picoKeeper.IsEnabled = false;
        bool previous = settings.PicoMicrophoneKeeper;
        Exception? failure = null;
        await operations.WaitAsync();
        try
        {
            if (shuttingDown) return;
            await using var storage = await settingsStore.AcquireAsync();
            if (shuttingDown) return;
            var native = bridge ?? throw new InvalidOperationException("原生核心尚未就绪。");
            await Task.Run(() => PicoMicrophoneService.Configure(native, enabled));
            if (previous != enabled)
            {
                settings.PicoMicrophoneKeeper = enabled;
                try { await storage.SaveAsync(settings.Snapshot()); }
                catch
                {
                    await Task.Run(() => PicoMicrophoneService.Configure(native, previous));
                    settings.PicoMicrophoneKeeper = previous;
                    throw;
                }
            }
        }
        catch (Exception exception) { failure = exception; Log.Write(exception.ToString()); }
        finally
        {
            operations.Release();
            picoKeeper.IsOn = settings.PicoMicrophoneKeeper;
            picoKeeper.IsEnabled = !shuttingDown && bridge is { IsInvalid: false };
            changingPico = false;
        }
        if (failure != null && report && !shuttingDown) await ShowErrorAsync("无法调整PICO麦克风", failure.Message);
    }

    private async Task ChangeSteamVrStartupAsync(bool enabled)
    {
        if (changingStartup || shuttingDown) return;
        changingStartup = true;
        steamVrStartup.IsEnabled = false;
        steamVrStartup.OnContent = "正在注册…";
        steamVrStartup.OffContent = "正在取消…";
        Exception? failure = null;
        await operations.WaitAsync();
        try
        {
            if (shuttingDown) return;
            await using var storage = await settingsStore.AcquireAsync();
            if (shuttingDown) return;
            await SteamVrStartupService.ChangeAsync(enabled,
                value => { settings.SteamVrAutoStart = value; return storage.SaveAsync(settings.Snapshot()); },
                value => settings.SteamVrAutoStart = value, lifetime.Token);
        }
        catch (OperationCanceledException) when (shuttingDown) { }
        catch (Exception exception) { failure = exception; Log.Write(exception.ToString()); }
        finally
        {
            operations.Release();
            steamVrStartup.IsOn = settings.SteamVrAutoStart;
            steamVrStartup.OnContent = "开";
            steamVrStartup.OffContent = "关";
            steamVrStartup.IsEnabled = !shuttingDown;
            changingStartup = false;
        }
        if (failure != null && !shuttingDown) await ShowErrorAsync("无法调整SteamVR启动项", failure.Message);
    }
}
