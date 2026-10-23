using Microsoft.UI.Dispatching;

namespace DoubaoVR.Desktop;

internal sealed class PreferenceScheduler
{
    private readonly DispatcherQueueTimer liveTimer, saveTimer;
    private readonly LatestWork<bool> live, save;
    private bool stopped;

    public PreferenceScheduler(DispatcherQueue dispatcher, Func<Task> configure, Func<Task> persist, Action<Exception> failure)
    {
        live = new(_ => configure(), failure);
        save = new(_ => persist(), failure);
        liveTimer = dispatcher.CreateTimer();
        liveTimer.Interval = TimeSpan.FromMilliseconds(50);
        liveTimer.IsRepeating = false;
        liveTimer.Tick += ApplyLive;
        saveTimer = dispatcher.CreateTimer();
        saveTimer.Interval = TimeSpan.FromMilliseconds(200);
        saveTimer.IsRepeating = false;
        saveTimer.Tick += SaveAfterQuiet;
    }

    public void Change(bool persistent)
    {
        if (stopped) return;
        if (!liveTimer.IsRunning) liveTimer.Start();
        if (persistent) { saveTimer.Stop(); saveTimer.Start(); }
    }

    private void ApplyLive(DispatcherQueueTimer sender, object arguments) => live.Post(true);
    private void SaveAfterQuiet(DispatcherQueueTimer sender, object arguments) => save.Post(true);

    public Task StopAsync()
    {
        if (!stopped)
        {
            stopped = true;
            liveTimer.Stop();
            saveTimer.Stop();
            liveTimer.Tick -= ApplyLive;
            saveTimer.Tick -= SaveAfterQuiet;
        }
        return Task.WhenAll(live.StopAsync(), save.StopAsync());
    }
}
