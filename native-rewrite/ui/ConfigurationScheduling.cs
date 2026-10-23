namespace DoubaoVR.Desktop;

internal sealed partial class MainWindow
{
    private readonly SettingsStore settingsStore = new(Settings.DataDirectory);
    private readonly PreferenceScheduler preferences;
    private bool captureBorder;

    private void RequestLiveConfiguration()
    {
        if (!shuttingDown) preferences.Change(false);
    }

    private void ApplyConfiguration()
    {
        if (shuttingDown) return;
        preferences.Change(true);
    }

    private async Task PersistSettingsAsync()
    {
        await using var storage = await settingsStore.AcquireAsync();
        await storage.SaveAsync(settings.Snapshot());
    }

    private void PreferenceFailure(Exception exception)
    {
        if (shuttingDown) Log.Write(exception.ToString());
        else Report(exception);
    }
}
