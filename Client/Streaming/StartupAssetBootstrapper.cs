using Shared.StreamingAssets;

namespace Client.Streaming;

/// <summary>
/// Explicit startup phase for streaming assets. Everything the first frames of UI layout depend on - the
/// root manifest, the catalog indexes of the startup libraries and the sound list - is prepared in one
/// asynchronous phase. The main thread performs a single bounded wait instead of a chain of synchronous
/// HTTP requests hidden inside property getters and static constructors.
/// </summary>
public static class StartupAssetBootstrapper
{
    private static readonly object Sync = new();
    private static Task<bool> _task;
    private static string _status = string.Empty;

    /// <summary>Human readable progress, safe to show in a loading or retry state.</summary>
    public static string Status
    {
        get => Volatile.Read(ref _status);
        private set => Volatile.Write(ref _status, value);
    }

    public static bool IsRunning
    {
        get { lock (Sync) return _task is { IsCompleted: false }; }
    }

    /// <summary>Starts the phase. Safe to call more than once; only the first call does any work.</summary>
    public static void Begin()
    {
        if (!AssetManager.Enabled)
        {
            AssetManager.StartupMetadataReady = true;
            return;
        }
        lock (Sync) _task ??= Task.Run(RunAsync);
    }

    /// <summary>
    /// The one place the main thread is allowed to wait on streaming assets. Returns false when the
    /// startup metadata is still incomplete; the client starts anyway and keeps retrying in the
    /// background, drawing transparent placeholders until the real records arrive.
    /// </summary>
    public static bool Wait(TimeSpan timeout)
    {
        Task<bool> task;
        lock (Sync) task = _task;
        if (task == null) return AssetManager.StartupMetadataReady;

        bool ready;
        try { ready = task.Wait(timeout) && task.Result; }
        catch (AggregateException ex)
        {
            CMain.SaveError($"Streaming startup phase failed: {ex.GetBaseException().Message}");
            ready = false;
        }
        AssetManager.StartupMetadataReady = ready;
        return ready;
    }

    private static async Task<bool> RunAsync()
    {
        try
        {
            Status = "正在获取资源清单";
            StreamingAssetV3Manifest manifest = await AssetManager.GetManifestAsync().ConfigureAwait(false);
            if (manifest == null)
            {
                Status = "资源清单不可用";
                return false;
            }

            // The sound list is best effort: a missing one costs sound names, not layout.
            Task<bool> soundList = AssetManager.PrefetchSoundAsync("soundlist.lst");

            // The working set is pixels, not layout metadata, so it must never gate the login screen. It is
            // started here so its single request overlaps the index fetches instead of following them.
            _ = ApplyWorkingSetAsync();

            List<string> libraries = AssetManager.GetRequiredStartupLibraryIds();
            Status = $"正在准备启动界面资源索引 0/{libraries.Count}";
            int completed = 0;
            bool[] results = await Task.WhenAll(libraries.Select(async id =>
            {
                bool ok = await AssetManager.GetLibraryIndexAsync(id, true).ConfigureAwait(false) != null;
                Status = $"正在准备启动界面资源索引 {Interlocked.Increment(ref completed)}/{libraries.Count}";
                return ok;
            })).ConfigureAwait(false);

            await soundList.ConfigureAwait(false);
            AssetManager.NotifyAssetsUpdated();

            bool ready = results.All(value => value);
            Status = ready ? "启动资源索引就绪" : "部分启动资源索引不可用";
            if (!ready) CMain.SaveError("Streaming startup metadata is incomplete.");
            return ready;
        }
        catch (Exception ex)
        {
            Status = "启动资源索引失败";
            CMain.SaveError($"Streaming startup metadata failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unpacks the first-run working set without letting it affect startup readiness: a slow or missing pack
    /// costs a few ranged requests later, and must not delay the login screen.
    /// </summary>
    private static async Task ApplyWorkingSetAsync()
    {
        try
        {
            int images = await AssetManager.ApplyWorkingSetAsync().ConfigureAwait(false);
            if (images > 0)
            {
                CMain.SaveError($"Streaming working set applied: {images} image(s).");
                AssetManager.NotifyAssetsUpdated();
            }
        }
        catch (Exception ex) { CMain.SaveError($"Streaming working set failed: {ex.Message}"); }
    }
}
