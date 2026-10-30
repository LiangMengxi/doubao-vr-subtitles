using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow
{
    private readonly Button launchButton = new() { Content = "开启字幕", MinHeight = 44, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
    private readonly CaptionController captions = new(new CaptionService());
    private DateTime nextCaptionRefresh = DateTime.MinValue;
    private DateTime nextAutoRestart = DateTime.MaxValue;
    private bool captureChanging;
    private bool autoRestarting;
    private long captureEpoch;

    private void UpdateCaptionControls()
    {
        if (shuttingDown) return;
        launchButton.Content = captions.Label;
        launchButton.IsEnabled = !captions.IsBusy && !captureChanging && !autoRestarting && !shuttingDown;
        captureButton.IsEnabled = vrButton.IsEnabled = !captions.IsBusy && !captureChanging && !autoRestarting && !shuttingDown;
    }

    private void ResetAutoRestartSchedule()
    {
        if (running && settings.AutoRestartCaptions && settings.AutoRestartIntervalMinutes > 0)
            nextAutoRestart = DateTime.UtcNow.AddMinutes(settings.AutoRestartIntervalMinutes);
        else
            nextAutoRestart = DateTime.MaxValue;
    }

    private async Task AutoRestartCaptionWindowAsync()
    {
        if (autoRestarting || !running || bridge == null || connected == null || shuttingDown || captions.IsBusy || captureChanging) return;
        autoRestarting = true;
        UpdateCaptionControls();
        await operations.WaitAsync();
        try
        {
            if (shuttingDown || !running || bridge == null || connected == null) return;
            var oldWindow = connected;
            bool hasBounds = WindowSource.TryGetBounds(oldWindow.Handle, out var bounds);
            NativeBridge.SetRestarting(bridge, 1);
            status.Text = "正在无感重启字幕窗口…";
            await ShortcutService.CloseForRestartAsync(oldWindow, lifetime.Token);
            var newWindow = await ShortcutService.OpenAsync(lifetime.Token);
            if (hasBounds) WindowSource.RestoreBounds(newWindow.Handle, bounds);
            RefreshSources(newWindow.Handle);
            captions.SyncTarget(newWindow);
            connected = newWindow;
            Marshal.ThrowExceptionForHR(NativeBridge.RebindWindow(bridge, (nuint)newWindow.Handle));
            var settleDeadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < settleDeadline && !shuttingDown)
            {
                await Task.Delay(50, lifetime.Token);
                if (NativeBridge.Stats(bridge).State == 2) break;
            }
            NativeBridge.SetRestarting(bridge, 0);
            Log.Write($"Auto-restarted caption window seamlessly: old={oldWindow.Handle}, new={newWindow.Handle}.");
        }
        catch (OperationCanceledException) when (shuttingDown)
        {
            if (bridge != null) NativeBridge.SetRestarting(bridge, 0);
        }
        catch (Exception exception)
        {
            if (bridge != null) NativeBridge.SetRestarting(bridge, 0);
            if (!shuttingDown) Report(exception);
        }
        finally
        {
            autoRestarting = false;
            ResetAutoRestartSchedule();
            nextCaptionRefresh = DateTime.MinValue;
            UpdateCaptionControls();
            operations.Release();
        }
    }

    private async Task ToggleCaptionAsync(bool? requestVr = null)
    {
        if (captions.IsBusy || shuttingDown) return;
        bool targetVr = requestVr ?? vrEnabled;
        status.Text = captions.Target == null ? (targetVr ? "正在一键开启字幕与VR…" : "正在开启字幕…") : "正在关闭字幕…";
        var operation = captions.ToggleAsync(async window =>
        {
            if (shuttingDown) return;
            UpdateCaptionControls();
            RefreshSources(window.Handle);
            await StartAsync(targetVr);
        }, async () =>
        {
            reconnecting = false;
            await StopAsync();
        }, lifetime.Token);
        UpdateCaptionControls();
        try { await operation; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!shuttingDown) Report(exception); }
        finally
        {
            nextCaptionRefresh = DateTime.MinValue;
            UpdateCaptionControls();
        }
    }

    private async Task RefreshCaptionAsync()
    {
        if (captions.IsBusy || autoRestarting || DateTime.UtcNow < nextCaptionRefresh) return;
        nextCaptionRefresh = DateTime.UtcNow.AddSeconds(1);
        await captions.RefreshAsync(lifetime.Token);
        if (!shuttingDown) UpdateCaptionControls();
    }
}
