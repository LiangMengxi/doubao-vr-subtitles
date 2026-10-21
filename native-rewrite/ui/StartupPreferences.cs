using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow
{
    private readonly ToggleSwitch picoKeeper = new() { Header = "改善PICO麦克风延迟" };
    private readonly ToggleSwitch steamVrStartup = new() { Header = "随SteamVR启动", OnContent = "已注册", OffContent = "未注册" };
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
            var native = bridge ?? throw new InvalidOperationException("原生核心尚未就绪。");
            await Task.Run(() => PicoMicrophoneService.Configure(native, enabled));
            if (previous != enabled)
            {
                settings.PicoMicrophoneKeeper = enabled;
                try { settings.Save(); }
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
        bool previous = settings.SteamVrAutoStart;
        Exception? failure = null;
        await operations.WaitAsync();
        try
        {
            if (shuttingDown) return;
            await SteamVrStartupService.ChangeAsync(enabled, lifetime.Token);
            settings.SteamVrAutoStart = enabled;
            try { settings.Save(); }
            catch (Exception saveError)
            {
                try { await SteamVrStartupService.ChangeAsync(previous, CancellationToken.None); }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("无法保存设置，且未能恢复原启动项，请在 SteamVR 设置中检查。", saveError, rollbackError);
                }
                settings.SteamVrAutoStart = previous;
                throw;
            }
        }
        catch (OperationCanceledException) when (shuttingDown) { }
        catch (Exception exception) { failure = exception; Log.Write(exception.ToString()); }
        finally
        {
            operations.Release();
            steamVrStartup.IsOn = settings.SteamVrAutoStart;
            steamVrStartup.OnContent = "已注册";
            steamVrStartup.OffContent = "未注册";
            steamVrStartup.IsEnabled = !shuttingDown;
            changingStartup = false;
        }
        if (failure != null && !shuttingDown) await ShowErrorAsync("无法调整SteamVR启动项", failure.Message);
    }
}
