using Microsoft.UI.Xaml.Controls;

namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow
{
    private readonly Button launchButton = new() { Content = "开启字幕", MinHeight = 44, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
    private readonly CaptionController captions = new(new CaptionService());
    private DateTime nextCaptionRefresh = DateTime.MinValue;
    private bool captureChanging;
    private long captureEpoch;

    private void UpdateCaptionControls()
    {
        if (shuttingDown) return;
        launchButton.Content = captions.Label;
        launchButton.IsEnabled = !captions.IsBusy && !captureChanging && !shuttingDown;
        captureButton.IsEnabled = vrButton.IsEnabled = !captions.IsBusy && !captureChanging && !shuttingDown;
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
        if (captions.IsBusy || DateTime.UtcNow < nextCaptionRefresh) return;
        nextCaptionRefresh = DateTime.UtcNow.AddSeconds(1);
        await captions.RefreshAsync(lifetime.Token);
        if (!shuttingDown) UpdateCaptionControls();
    }
}
