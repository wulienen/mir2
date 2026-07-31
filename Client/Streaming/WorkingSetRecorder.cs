using System.Collections.Concurrent;
using Shared.StreamingAssets;

namespace Client.Streaming;

/// <summary>
/// Records which streaming images a run actually touches, so AssetBuilder can publish exactly those as the
/// first-run working set. Prefetching whole startup libraries was measured at 88.96 MB and rejected; the
/// recorded set is a small fraction of that.
///
/// The recording is taken from the draw path, so it is enabled by <c>[Streaming] RecordWorkingSet=True</c>
/// and off by default: a player gains nothing from it. Only the marking is on the render thread, and that is
/// one array store into a per-library flag array.
/// </summary>
public static class WorkingSetRecorder
{
    /// <summary>
    /// Bytes of image records to record before the recorder stops itself. A working set is what a cold client
    /// needs between the login screen and standing in the first map; without a cap, leaving the client running
    /// would keep appending the whole session's gameplay images to the recording.
    /// </summary>
    private const long MaxRecordedBytes = 48L * 1024 * 1024;

    private const string FileName = "workset-usage.txt";

    private static readonly ConcurrentDictionary<string, RecordedLibrary> Libraries = new(StringComparer.Ordinal);
    private static long _recordedBytes;
    private static int _stopped;
    private static bool _enabled;

    public static bool IsRecording => _enabled && Volatile.Read(ref _stopped) == 0;

    public static void Initialize()
    {
        _enabled = Settings.RecordWorkingSet && AssetManager.Enabled;
        if (_enabled) CMain.SaveError("Working set recording is enabled; usage will be written on exit.");
    }

    /// <summary>
    /// Marks one image of one library as touched. Called from the draw path for hits as well as misses, so a
    /// recording can be taken against a warm cache and still describe the whole working set.
    /// </summary>
    public static void Record(V3LibraryRecord library, V3LibraryImageRecord image)
    {
        if (!IsRecording || library == null || image == null || !image.Exists) return;
        if (image.Index < 0 || image.Index >= library.ImageCount) return;

        RecordedLibrary recorded = Libraries.GetOrAdd(library.Id, _ => new RecordedLibrary(library.ImageCount));
        if (!recorded.TryMark(image.Index)) return;
        if (Interlocked.Add(ref _recordedBytes, image.Length) < MaxRecordedBytes) return;
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            CMain.SaveError($"Working set recording stopped at {MaxRecordedBytes / 1048576} MB.");
    }

    /// <summary>
    /// Merges the recording into <c>&lt;cache&gt;/workset-usage.txt</c> and returns the number of images in the
    /// merged file. Merging rather than overwriting means several runs (login, character creation, a second
    /// map) can be recorded one after another.
    /// </summary>
    public static int Save()
    {
        if (!_enabled || Libraries.IsEmpty) return 0;
        string path = Path.Combine(Path.GetFullPath(Settings.AssetCachePath), FileName);
        try
        {
            Dictionary<string, SortedSet<int>> usage = File.Exists(path)
                ? StreamingWorkingSetPack.ReadUsage(File.ReadLines(path))
                : new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, RecordedLibrary> entry in Libraries)
            {
                if (!usage.TryGetValue(entry.Key, out SortedSet<int> indexes))
                    usage[entry.Key] = indexes = new SortedSet<int>();
                foreach (int index in entry.Value.Marked()) indexes.Add(index);
            }

            List<string> lines = new()
            {
                "# Recorded streaming working set. One 'libraryId<TAB>imageIndex' per line.",
                "# Feed it to AssetBuilder: build-v3 ... --usage <this file>"
            };
            int images = 0;
            foreach (KeyValuePair<string, SortedSet<int>> entry in usage.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                foreach (int index in entry.Value)
                {
                    lines.Add(StreamingWorkingSetPack.FormatUsageLine(entry.Key, index));
                    images++;
                }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllLines(temp, lines);
            File.Move(temp, path, true);
            CMain.SaveError($"Working set recording written: {images} image(s) in {path}");
            return images;
        }
        catch (Exception ex)
        {
            CMain.SaveError($"Working set recording could not be written: {ex.Message}");
            return 0;
        }
    }

    private sealed class RecordedLibrary
    {
        private readonly bool[] _marked;

        public RecordedLibrary(int imageCount) => _marked = new bool[Math.Max(1, imageCount)];

        /// <summary>
        /// A plain array store: two threads marking the same index simply both see "already touched" later,
        /// and the recorder is a tool, not a correctness-critical path.
        /// </summary>
        public bool TryMark(int index)
        {
            if (index >= _marked.Length || _marked[index]) return false;
            _marked[index] = true;
            return true;
        }

        public IEnumerable<int> Marked()
        {
            for (int i = 0; i < _marked.Length; i++)
                if (_marked[i]) yield return i;
        }
    }
}
