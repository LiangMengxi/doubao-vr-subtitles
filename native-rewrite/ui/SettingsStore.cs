namespace DoubaoVR.Desktop;

internal sealed class SettingsStore(string directory) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string root = Path.GetFullPath(directory);
    private SettingsSnapshot? saved;

    public async Task<Session> AcquireAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        return new Session(this);
    }

    public void Dispose() => gate.Dispose();

    internal sealed class Session : IAsyncDisposable
    {
        private readonly SettingsStore owner;
        private bool disposed, cleanup;
        private string protectedBackground = "";

        internal Session(SettingsStore store) { owner = store; }

        public async Task SaveAsync(SettingsSnapshot snapshot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            bool changed = await Task.Run(() =>
            {
                Directory.CreateDirectory(owner.root);
                string path = Path.Combine(owner.root, "settings.json");
                if (File.Exists(path) && File.ReadAllText(path) == snapshot.Json) return false;
                string temporary = Path.Combine(owner.root, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllText(temporary, snapshot.Json);
                    if (File.Exists(path)) File.Copy(path, path + ".bak", true);
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return true;
            }).ConfigureAwait(false);
            owner.saved = snapshot;
            cleanup |= changed;
        }

        public async Task<string> ImportAsync(string source, string previousBackground = "")
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cleanup = true;
            protectedBackground = previousBackground;
            return await Task.Run(() => BackgroundImageService.ImportAsync(source, owner.root)).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (cleanup) await Task.Run(() => BackgroundFiles.Cleanup(owner.root, owner.saved?.BackgroundImage ?? "", protectedBackground)).ConfigureAwait(false);
            }
            finally { owner.gate.Release(); }
        }
    }
}
