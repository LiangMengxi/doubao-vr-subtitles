namespace DoubaoVR.Desktop;

internal sealed class LatestWork<T>(Func<T, Task> action, Action<Exception> failure)
{
    private T? pending;
    private bool hasPending, active, stopped;
    private Task completion = Task.CompletedTask;
    public Task Completion => completion;

    public void Post(T value)
    {
        if (stopped) return;
        pending = value;
        hasPending = true;
        if (active) return;
        active = true;
        completion = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (hasPending && !stopped)
            {
                T current = pending!;
                pending = default;
                hasPending = false;
                try { await action(current); }
                catch (Exception exception) { failure(exception); }
            }
        }
        finally { active = false; }
    }

    public Task StopAsync()
    {
        stopped = true;
        pending = default;
        hasPending = false;
        return completion;
    }
}
