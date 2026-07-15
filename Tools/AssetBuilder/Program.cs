using Shared.StreamingAssets;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBuilder;

internal static class Program
{
    private const string BuildStateFileName = ".assetbuilder-state.json";

    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "fix-manifest", StringComparison.OrdinalIgnoreCase))
        {
            string existingOutput = Path.GetFullPath(args.Length > 1 ? args[1] : "StreamingAssets");
            string newVersion = args.Length > 2 ? args[2] : DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            return FixManifest(existingOutput, newVersion);
        }

        BuildOptions options = BuildOptions.Parse(args);
        string source = options.Source;
        string output = options.Output;

        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine($"Source client directory not found: {source}");
            return 1;
        }

        Directory.CreateDirectory(output);

        AssetManifest previousManifest = TryReadJson<AssetManifest>(Path.Combine(output, StreamingAssetConstants.ManifestFileName));
        BuildState previousState = TryReadJson<BuildState>(Path.Combine(output, BuildStateFileName)) ?? new BuildState();
        BuildState nextState = new();
        BuildProgress progress = new();

        AssetManifest manifest = new()
        {
            CreatedUtc = DateTime.UtcNow,
            MapChunkSize = StreamingAssetConstants.DefaultMapChunkSize
        };

        BuildLibraries(source, output, manifest, previousManifest, previousState, nextState, options, progress);
        BuildMaps(source, output, manifest, previousManifest, previousState, nextState, options, progress);
        BuildSounds(source, output, manifest, previousManifest, previousState, nextState, options, progress);

        bool assetSetChanged = !HasSameAssetSet(previousManifest, manifest);
        bool manifestChanged = previousManifest == null || progress.RebuiltCount > 0 || assetSetChanged ||
            (!string.IsNullOrWhiteSpace(options.Version) && !string.Equals(previousManifest.Version, options.Version, StringComparison.Ordinal));

        manifest.Version = !string.IsNullOrWhiteSpace(options.Version)
            ? options.Version
            : manifestChanged || string.IsNullOrWhiteSpace(previousManifest?.Version)
                ? DateTime.UtcNow.ToString("yyyyMMddHHmmss")
                : previousManifest.Version;

        if (!manifestChanged)
        {
            manifest.CreatedUtc = previousManifest.CreatedUtc;
        }

        if (manifestChanged)
        {
            StreamingAssetIO.WriteJson(Path.Combine(output, StreamingAssetConstants.ManifestFileName), manifest);
        }

        StreamingAssetIO.WriteJson(Path.Combine(output, BuildStateFileName), nextState);

        Console.WriteLine($"Streaming assets built at {output}");
        Console.WriteLine($"Libraries: {manifest.Libraries.Count}, Maps: {manifest.Maps.Count}, Sounds: {manifest.Sounds.Count}");
        Console.WriteLine($"Rebuilt: {progress.RebuiltCount}, Reused: {progress.ReusedCount}, Failed: {progress.FailedCount}");
        Console.WriteLine($"Manifest version: {manifest.Version}");
        return 0;
    }

    private static int FixManifest(string output, string version)
    {
        string manifestPath = Path.Combine(output, StreamingAssetConstants.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Manifest not found: {manifestPath}");
            return 1;
        }

        AssetManifest manifest = StreamingAssetIO.ReadJson<AssetManifest>(manifestPath);
        manifest.Version = version;
        manifest.CreatedUtc = DateTime.UtcNow;

        foreach (AssetLibraryRecord record in manifest.Libraries)
        {
            RefreshRecordHash(output, record.ManifestPath, hash => record.Hash = hash, length => record.Length = length);
        }

        foreach (AssetMapRecord record in manifest.Maps)
        {
            RefreshRecordHash(output, record.ManifestPath, hash => record.Hash = hash, length => record.Length = length);
        }

        StreamingAssetIO.WriteJson(manifestPath, manifest);

        Console.WriteLine($"Manifest fixed at {manifestPath}");
        Console.WriteLine($"Version: {manifest.Version}");
        Console.WriteLine($"Libraries: {manifest.Libraries.Count}, Maps: {manifest.Maps.Count}, Sounds: {manifest.Sounds.Count}");
        return 0;
    }

    private static void RefreshRecordHash(string output, string relativePath, Action<string> setHash, Action<long> setLength)
    {
        string path = Path.Combine(output, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Referenced manifest not found: {path}");
            return;
        }

        using FileStream stream = File.OpenRead(path);
        setLength(stream.Length);
        setHash(StreamingAssetIO.ComputeSha256(stream));
    }

    private static void BuildLibraries(string source, string output, AssetManifest manifest, AssetManifest previousManifest,
        BuildState previousState, BuildState nextState, BuildOptions options, BuildProgress progress)
    {
        string dataPath = ResolveLibraryDataPath(source);
        if (dataPath == null)
        {
            Console.Error.WriteLine($"No Data or Data_Full library directory found under: {source}");
            return;
        }

        List<string> files = Directory.EnumerateFiles(dataPath, "*.Lib", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress.StartStage("Libraries", files);
        Dictionary<string, AssetLibraryRecord> previous = previousManifest?.Libraries
            .ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < files.Count; index++)
        {
            string file = files[index];
            string id = ToAssetId(Path.GetRelativePath(dataPath, Path.ChangeExtension(file, null)));
            string libraryRoot = Path.Combine(output, StreamingAssetConstants.LibrariesDirectory, id);
            string imagesRoot = Path.Combine(libraryRoot, StreamingAssetConstants.LibraryImagesDirectory);
            SourceStateEntry sourceState = CreateSourceState("library", id, dataPath, file, previousState, options.Verify);
            nextState.Sources.Add(sourceState);

            string manifestPath = Path.Combine(libraryRoot, StreamingAssetConstants.ManifestFileName);
            if (!options.ForceRebuild && (!sourceState.Changed || options.AdoptExisting) &&
                TryReuseLibrary(output, previous, id, out AssetLibraryRecord existing))
            {
                manifest.Libraries.Add(existing);
                progress.CompleteItem(file, false);
                continue;
            }

            try
            {
                Directory.CreateDirectory(imagesRoot);
                LibraryManifest libraryManifest = StreamingLibraryReader.ReadLibrary(file, id, imagesRoot,
                    (current, total) => progress.ReportItemProgress(file, index, current, total));
                StreamingAssetIO.WriteJson(manifestPath, libraryManifest);

                using FileStream stream = File.OpenRead(manifestPath);
                manifest.Libraries.Add(new AssetLibraryRecord
                {
                    Id = id,
                    ManifestPath = ToWebPath(Path.GetRelativePath(output, manifestPath)),
                    ImageCount = libraryManifest.ImageCount,
                    Length = stream.Length,
                    Hash = StreamingAssetIO.ComputeSha256(stream)
                });
                progress.CompleteItem(file, true);
            }
            catch (Exception ex)
            {
                progress.FailItem(file, ex);
            }
        }

        progress.FinishStage();
    }

    private static string ResolveLibraryDataPath(string source)
    {
        string fullDataPath = Path.Combine(source, "Data_Full");
        if (Directory.Exists(fullDataPath) && Directory.EnumerateFiles(fullDataPath, "*.Lib", SearchOption.AllDirectories).Any())
        {
            return fullDataPath;
        }

        string dataPath = Path.Combine(source, "Data");
        if (Directory.Exists(dataPath) && Directory.EnumerateFiles(dataPath, "*.Lib", SearchOption.AllDirectories).Any())
        {
            return dataPath;
        }

        return null;
    }

    private static void BuildMaps(string source, string output, AssetManifest manifest, AssetManifest previousManifest,
        BuildState previousState, BuildState nextState, BuildOptions options, BuildProgress progress)
    {
        string mapPath = Path.Combine(source, "Map");
        if (!Directory.Exists(mapPath))
        {
            return;
        }

        List<string> files = Directory.EnumerateFiles(mapPath, "*.map", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress.StartStage("Maps", files);
        Dictionary<string, AssetMapRecord> previous = previousManifest?.Maps
            .ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < files.Count; index++)
        {
            string file = files[index];
            string id = ToAssetId(Path.GetFileNameWithoutExtension(file));
            string mapRoot = Path.Combine(output, StreamingAssetConstants.MapsDirectory, id);
            string chunkRoot = Path.Combine(mapRoot, StreamingAssetConstants.MapChunksDirectory);
            SourceStateEntry sourceState = CreateSourceState("map", id, mapPath, file, previousState, options.Verify);
            nextState.Sources.Add(sourceState);

            try
            {
                if (!options.ForceRebuild && (!sourceState.Changed || options.AdoptExisting) &&
                    TryReuseMap(output, previous, id, out AssetMapRecord existing))
                {
                    manifest.Maps.Add(existing);
                    progress.CompleteItem(file, false);
                    continue;
                }

                Directory.CreateDirectory(chunkRoot);
                MapManifest mapManifest = StreamingMapReader.ReadMap(file, id, chunkRoot, StreamingAssetConstants.DefaultMapChunkSize);
                string manifestPath = Path.Combine(mapRoot, StreamingAssetConstants.ManifestFileName);
                StreamingAssetIO.WriteJson(manifestPath, mapManifest);

                using FileStream stream = File.OpenRead(manifestPath);
                manifest.Maps.Add(new AssetMapRecord
                {
                    Id = id,
                    ManifestPath = ToWebPath(Path.GetRelativePath(output, manifestPath)),
                    Width = mapManifest.Width,
                    Height = mapManifest.Height,
                    Length = stream.Length,
                    Hash = StreamingAssetIO.ComputeSha256(stream)
                });
                progress.CompleteItem(file, true);
            }
            catch (Exception ex)
            {
                progress.FailItem(file, ex);
            }
        }

        progress.FinishStage();
    }

    private static void BuildSounds(string source, string output, AssetManifest manifest, AssetManifest previousManifest,
        BuildState previousState, BuildState nextState, BuildOptions options, BuildProgress progress)
    {
        string soundPath = Path.Combine(source, "Sound");
        if (!Directory.Exists(soundPath))
        {
            return;
        }

        List<string> files = Directory.EnumerateFiles(soundPath, "*.*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Path.GetFileName(path), "SoundList.lst", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress.StartStage("Sounds", files);
        Dictionary<string, AssetSoundRecord> previous = previousManifest?.Sounds
            .ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < files.Count; index++)
        {
            string file = files[index];
            string relative = Path.GetRelativePath(soundPath, file);
            string id = ToAssetId(relative);
            string destination = Path.Combine(output, StreamingAssetConstants.SoundsDirectory, relative);
            SourceStateEntry sourceState = CreateSourceState("sound", id, soundPath, file, previousState, options.Verify);
            nextState.Sources.Add(sourceState);

            if (!options.ForceRebuild && (!sourceState.Changed || options.AdoptExisting) &&
                TryReuseSound(output, previous, id, out AssetSoundRecord existing))
            {
                manifest.Sounds.Add(existing);
                progress.CompleteItem(file, false);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
            File.Copy(file, destination, true);

            using FileStream stream = File.OpenRead(file);
            manifest.Sounds.Add(new AssetSoundRecord
            {
                Id = id,
                Path = ToWebPath(Path.GetRelativePath(output, destination)),
                Length = stream.Length,
                Hash = StreamingAssetIO.ComputeSha256(stream)
            });
            progress.CompleteItem(file, true);
        }

        progress.FinishStage();
    }

    private static T TryReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? StreamingAssetIO.ReadJson<T>(path) : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ignoring unreadable build metadata {path}: {ex.Message}");
            return null;
        }
    }

    private static SourceStateEntry CreateSourceState(string kind, string id, string sourceRoot, string file,
        BuildState previousState, bool verify)
    {
        string relativePath = ToWebPath(Path.GetRelativePath(sourceRoot, file));
        SourceStateEntry previous = previousState.Find(kind, id);
        FileInfo info = new(file);
        bool metadataMatches = previous != null &&
            previous.Length == info.Length &&
            previous.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
            string.Equals(previous.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase);

        SourceStateEntry state = new()
        {
            Kind = kind,
            Id = id,
            RelativePath = relativePath,
            Length = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            Hash = previous?.Hash ?? string.Empty,
            Changed = !metadataMatches
        };

        if (verify || (!metadataMatches && previous != null && !string.IsNullOrEmpty(previous.Hash)))
        {
            using FileStream stream = File.OpenRead(file);
            state.Hash = StreamingAssetIO.ComputeSha256(stream);
            state.Changed = !string.Equals(state.Hash, previous?.Hash, StringComparison.OrdinalIgnoreCase);
        }

        return state;
    }

    private static bool TryReuseLibrary(string output, Dictionary<string, AssetLibraryRecord> previous, string id,
        out AssetLibraryRecord record)
    {
        if (!previous.TryGetValue(id, out record)) return false;
        return File.Exists(Path.Combine(output, record.ManifestPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool TryReuseMap(string output, Dictionary<string, AssetMapRecord> previous, string id,
        out AssetMapRecord record)
    {
        if (!previous.TryGetValue(id, out record)) return false;
        return File.Exists(Path.Combine(output, record.ManifestPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool TryReuseSound(string output, Dictionary<string, AssetSoundRecord> previous, string id,
        out AssetSoundRecord record)
    {
        if (!previous.TryGetValue(id, out record)) return false;
        return File.Exists(Path.Combine(output, record.Path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool HasSameAssetSet(AssetManifest previous, AssetManifest current)
    {
        if (previous == null || previous.Libraries.Count != current.Libraries.Count ||
            previous.Maps.Count != current.Maps.Count || previous.Sounds.Count != current.Sounds.Count)
        {
            return false;
        }

        return previous.Libraries.Select(record => record.Id).OrderBy(id => id).SequenceEqual(current.Libraries.Select(record => record.Id).OrderBy(id => id), StringComparer.OrdinalIgnoreCase) &&
               previous.Maps.Select(record => record.Id).OrderBy(id => id).SequenceEqual(current.Maps.Select(record => record.Id).OrderBy(id => id), StringComparer.OrdinalIgnoreCase) &&
               previous.Sounds.Select(record => record.Id).OrderBy(id => id).SequenceEqual(current.Sounds.Select(record => record.Id).OrderBy(id => id), StringComparer.OrdinalIgnoreCase);
    }

    internal static string ToAssetId(string value)
    {
        string normalized = value.Replace('\\', '/').TrimStart('.', '/');
        if (normalized.EndsWith(".Lib", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..normalized.LastIndexOf('.')];
        }

        return normalized.ToLowerInvariant();
    }

    internal static string ToWebPath(string value)
    {
        return value.Replace('\\', '/');
    }

    private sealed class BuildOptions
    {
        public string Source { get; private set; }
        public string Output { get; private set; }
        public string Version { get; private set; }
        public bool ForceRebuild { get; private set; }
        public bool Verify { get; private set; }
        public bool AdoptExisting { get; private set; }

        public static BuildOptions Parse(string[] args)
        {
            List<string> values = new();
            BuildOptions options = new();
            foreach (string arg in args)
            {
                switch (arg.ToLowerInvariant())
                {
                    case "--full": options.ForceRebuild = true; break;
                    case "--verify": options.Verify = true; break;
                    case "--adopt-existing": options.AdoptExisting = true; break;
                    default: values.Add(arg); break;
                }
            }

            options.Source = Path.GetFullPath(values.Count > 0 ? values[0] : Path.Combine("Build", "Client", "Debug"));
            options.Output = Path.GetFullPath(values.Count > 1 ? values[1] : "StreamingAssets");
            options.Version = values.Count > 2 ? values[2] : null;
            return options;
        }
    }

    private sealed class BuildState
    {
        public int FormatVersion { get; set; } = 1;
        public List<SourceStateEntry> Sources { get; set; } = new();

        public SourceStateEntry Find(string kind, string id)
        {
            return Sources.FirstOrDefault(entry => string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                                                   string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class SourceStateEntry
    {
        public string Kind { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Hash { get; set; } = string.Empty;

        [JsonIgnore]
        public bool Changed { get; set; }
    }

    private sealed class BuildProgress
    {
        private string _stage = string.Empty;
        private int _totalItems;
        private int _completedItems;
        private long _totalBytes;
        private long _completedBytes;
        private readonly Stopwatch _timer = Stopwatch.StartNew();

        public int RebuiltCount { get; private set; }
        public int ReusedCount { get; private set; }
        public int FailedCount { get; private set; }

        public void StartStage(string stage, IReadOnlyCollection<string> files)
        {
            FinishStage();
            _stage = stage;
            _totalItems = files.Count;
            _completedItems = 0;
            _totalBytes = files.Sum(path => new FileInfo(path).Length);
            _completedBytes = 0;
            Console.WriteLine($"{stage}: {_totalItems} files, {FormatBytes(_totalBytes)}");
        }

        public void ReportItemProgress(string file, int itemIndex, int current, int total)
        {
            double itemFraction = total <= 0 ? 0 : (double)current / total;
            Write(file, itemIndex, itemFraction);
        }

        public void CompleteItem(string file, bool rebuilt)
        {
            _completedItems++;
            _completedBytes += new FileInfo(file).Length;
            if (rebuilt) RebuiltCount++; else ReusedCount++;
            Write(file, _completedItems - 1, 0);
        }

        public void FailItem(string file, Exception ex)
        {
            _completedItems++;
            _completedBytes += new FileInfo(file).Length;
            FailedCount++;
            Write(file, _completedItems - 1, 0);
            Console.WriteLine();
            Console.Error.WriteLine($"Failed: {file}");
            Console.Error.WriteLine(ex.Message);
        }

        public void FinishStage()
        {
            if (!string.IsNullOrEmpty(_stage)) Console.WriteLine();
            _stage = string.Empty;
        }

        private void Write(string file, int itemIndex, double itemFraction)
        {
            long currentFileBytes = new FileInfo(file).Length;
            long currentBytes = _completedBytes + (long)(currentFileBytes * itemFraction);
            double percent = _totalBytes == 0 ? 1 : (double)currentBytes / _totalBytes;
            string name = Path.GetFileName(file);
            string line = $"[{_stage}] {percent,7:P1} ({Math.Min(itemIndex + 1, _totalItems)}/{_totalItems}) " +
                          $"rebuilt {RebuiltCount}, reused {ReusedCount}, elapsed {_timer.Elapsed:hh\\:mm\\:ss} | {name}";
            Console.Write($"\r{line.PadRight(200)}");
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.##} {units[unit]}";
        }
    }
}

internal static class StreamingLibraryReader
{
    public static LibraryManifest ReadLibrary(string file, string id, string imagesRoot, Action<int, int> progress = null)
    {
        using FileStream stream = File.OpenRead(file);
        using BinaryReader reader = new(stream);

        int version = reader.ReadInt32();
        if (version < 2)
        {
            throw new InvalidDataException($"Unsupported library version {version}: {file}");
        }

        int count = reader.ReadInt32();
        int frameSeek = version >= 3 ? reader.ReadInt32() : 0;
        int[] indexList = new int[count];
        for (int i = 0; i < count; i++)
        {
            indexList[i] = reader.ReadInt32();
        }

        LibraryManifest manifest = new()
        {
            Id = id,
            ImageCount = count
        };

        for (int i = 0; i < count; i++)
        {
            if (indexList[i] <= 0 || indexList[i] >= stream.Length)
            {
                manifest.Images.Add(new LibraryImageRecord { Index = i, Path = string.Empty });
                progress?.Invoke(i + 1, count);
                continue;
            }

            stream.Position = indexList[i];
            short width = reader.ReadInt16();
            short height = reader.ReadInt16();
            short x = reader.ReadInt16();
            short y = reader.ReadInt16();
            short shadowX = reader.ReadInt16();
            short shadowY = reader.ReadInt16();
            byte shadow = reader.ReadByte();
            int length = reader.ReadInt32();
            byte[] imageData = reader.ReadBytes(length);

            bool hasMask = ((shadow >> 7) == 1);
            short maskWidth = 0;
            short maskHeight = 0;
            short maskX = 0;
            short maskY = 0;
            byte[] maskData = Array.Empty<byte>();

            if (hasMask)
            {
                maskWidth = reader.ReadInt16();
                maskHeight = reader.ReadInt16();
                maskX = reader.ReadInt16();
                maskY = reader.ReadInt16();
                int maskLength = reader.ReadInt32();
                maskData = reader.ReadBytes(maskLength);
            }

            byte[] chunk = StreamingAssetIO.WriteLibraryImageChunk(width, height, x, y, shadowX, shadowY, shadow, imageData, maskWidth, maskHeight, maskX, maskY, maskData);
            string chunkPath = Path.Combine(imagesRoot, $"{i}.bin");
            File.WriteAllBytes(chunkPath, chunk);

            manifest.Images.Add(new LibraryImageRecord
            {
                Index = i,
                Path = $"{StreamingAssetConstants.LibraryImagesDirectory}/{i}.bin",
                Width = width,
                Height = height,
                X = x,
                Y = y,
                ShadowX = shadowX,
                ShadowY = shadowY,
                Shadow = shadow,
                Length = length,
                HasMask = hasMask,
                MaskWidth = maskWidth,
                MaskHeight = maskHeight,
                MaskX = maskX,
                MaskY = maskY,
                MaskLength = maskData.Length,
                FileLength = chunk.LongLength,
                Hash = StreamingAssetIO.ComputeSha256(chunk)
            });

            progress?.Invoke(i + 1, count);
        }

        if (version >= 3 && frameSeek > 0 && frameSeek < stream.Length)
        {
            stream.Position = frameSeek;
            int frameCount = reader.ReadInt32();
            for (int i = 0; i < frameCount; i++)
            {
                manifest.Frames.Add(new LibraryFrameRecord
                {
                    Action = reader.ReadByte(),
                    Start = reader.ReadInt32(),
                    Count = reader.ReadInt32(),
                    Skip = reader.ReadInt32(),
                    Interval = reader.ReadInt32(),
                    EffectStart = reader.ReadInt32(),
                    EffectCount = reader.ReadInt32(),
                    EffectSkip = reader.ReadInt32(),
                    EffectInterval = reader.ReadInt32(),
                    Reverse = reader.ReadBoolean(),
                    Blend = reader.ReadBoolean()
                });
            }
        }

        return manifest;
    }
}

internal static class StreamingMapReader
{
    public static MapManifest ReadMap(string file, string id, string chunkRoot, int chunkSize)
    {
        MapData map = MapData.Read(file);
        MapManifest manifest = new()
        {
            Id = id,
            Width = map.Width,
            Height = map.Height,
            ChunkSize = chunkSize
        };

        for (int chunkX = 0; chunkX < map.Width; chunkX += chunkSize)
        {
            for (int chunkY = 0; chunkY < map.Height; chunkY += chunkSize)
            {
                int width = Math.Min(chunkSize, map.Width - chunkX);
                int height = Math.Min(chunkSize, map.Height - chunkY);
                StreamingMapCell[] cells = new StreamingMapCell[width * height];

                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        cells[x * height + y] = map.Cells[chunkX + x, chunkY + y];
                    }
                }

                StreamingMapChunk chunk = new()
                {
                    X = chunkX,
                    Y = chunkY,
                    Width = width,
                    Height = height,
                    Cells = cells
                };

                byte[] data = StreamingAssetIO.WriteMapChunk(chunk);
                string fileName = $"{chunkX}_{chunkY}.bin";
                string path = Path.Combine(chunkRoot, fileName);
                File.WriteAllBytes(path, data);

                manifest.Chunks.Add(new MapChunkRecord
                {
                    X = chunkX,
                    Y = chunkY,
                    Width = width,
                    Height = height,
                    Path = $"{StreamingAssetConstants.MapChunksDirectory}/{fileName}",
                    Length = data.LongLength,
                    Hash = StreamingAssetIO.ComputeSha256(data)
                });
            }
        }

        return manifest;
    }
}

internal sealed class MapData
{
    public int Width;
    public int Height;
    public StreamingMapCell[,] Cells;
    private byte[] _bytes;

    public static MapData Read(string file)
    {
        MapData map = new()
        {
            _bytes = File.ReadAllBytes(file)
        };

        map.Load();
        return map;
    }

    private void Load()
    {
        switch (FindType(_bytes))
        {
            case 1:
                LoadType1();
                break;
            case 2:
                LoadType2();
                break;
            case 3:
                LoadType3();
                break;
            case 4:
                LoadType4();
                break;
            case 5:
                LoadType5();
                break;
            case 6:
                LoadType6();
                break;
            case 7:
                LoadType7();
                break;
            case 100:
                LoadType100();
                break;
            default:
                LoadType0();
                break;
        }
    }

    private static byte FindType(byte[] input)
    {
        if ((input[2] == 0x43) && (input[3] == 0x23)) return 100;
        if (input[0] == 0) return 5;
        if ((input[0] == 0x0F) && (input[5] == 0x53) && (input[14] == 0x33)) return 6;
        if ((input[0] == 0x15) && (input[4] == 0x32) && (input[6] == 0x41) && (input[19] == 0x31)) return 4;
        if ((input[0] == 0x10) && (input[2] == 0x61) && (input[7] == 0x31) && (input[14] == 0x31)) return 1;

        if ((input[4] == 0x0F) || (input[4] == 0x03) && (input[18] == 0x0D) && (input[19] == 0x0A))
        {
            int width = input[0] + (input[1] << 8);
            int height = input[2] + (input[3] << 8);
            return input.Length > 52 + width * height * 14 ? (byte)3 : (byte)2;
        }

        if ((input[0] == 0x0D) && (input[1] == 0x4C) && (input[7] == 0x20) && (input[11] == 0x6D)) return 7;
        return 0;
    }

    private void InitCells(int width, int height)
    {
        Width = width;
        Height = height;
        Cells = new StreamingMapCell[Width, Height];
    }

    private void SetFishing(int x, int y)
    {
        if (Cells[x, y].Light >= 100 && Cells[x, y].Light <= 119)
        {
            Cells[x, y].FishingCell = true;
        }
    }

    private void LoadType0()
    {
        int offset = 0;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 52;

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = 0;
            Cells[x, y].MiddleIndex = 1;
            Cells[x, y].BackImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 2);
            Cells[x, y].Light = _bytes[offset++];

            if ((Cells[x, y].BackImage & 0x8000) != 0)
            {
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            }

            SetFishing(x, y);
        }
    }

    private void LoadType1()
    {
        int offset = 21;
        int width = BitConverter.ToInt16(_bytes, offset);
        offset += 2;
        int xor = BitConverter.ToInt16(_bytes, offset);
        offset += 2;
        int height = BitConverter.ToInt16(_bytes, offset);
        InitCells(width ^ xor, height ^ xor);
        offset = 54;

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = 0;
            Cells[x, y].BackImage = BitConverter.ToInt32(_bytes, offset) ^ unchecked((int)0xAA38AA38);
            offset += 4;
            Cells[x, y].MiddleIndex = 1;
            Cells[x, y].MiddleImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor);
            offset += 2;
            Cells[x, y].FrontImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor);
            offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 2);
            Cells[x, y].Light = _bytes[offset++];
            Cells[x, y].Unknown = _bytes[offset++];

            if (Cells[x, y].FrontIndex == 102) Cells[x, y].FrontIndex = 90;
            if (Cells[x, y].FrontIndex >= 255) Cells[x, y].FrontIndex = -1;
            SetFishing(x, y);
        }
    }

    private void LoadType2()
    {
        int offset = 0;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 52;

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 120);
            Cells[x, y].Light = _bytes[offset++];
            Cells[x, y].BackIndex = (short)(_bytes[offset++] + 100);
            Cells[x, y].MiddleIndex = (short)(_bytes[offset++] + 110);

            if ((Cells[x, y].BackImage & 0x8000) != 0)
            {
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            }

            SetFishing(x, y);
        }
    }

    private void LoadType3()
    {
        int offset = 0;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 52;

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 120);
            Cells[x, y].Light = _bytes[offset++];
            Cells[x, y].BackIndex = (short)(_bytes[offset++] + 100);
            Cells[x, y].MiddleIndex = (short)(_bytes[offset++] + 110);
            Cells[x, y].TileAnimationImage = BitConverter.ToInt16(_bytes, offset);
            offset += 7;
            Cells[x, y].TileAnimationFrames = _bytes[offset++];
            Cells[x, y].TileAnimationOffset = BitConverter.ToInt16(_bytes, offset);
            offset += 14;

            if ((Cells[x, y].BackImage & 0x8000) != 0)
            {
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            }

            SetFishing(x, y);
        }
    }

    private void LoadType4()
    {
        int offset = 31;
        int width = BitConverter.ToInt16(_bytes, offset);
        offset += 2;
        int xor = BitConverter.ToInt16(_bytes, offset);
        offset += 2;
        int height = BitConverter.ToInt16(_bytes, offset);
        InitCells(width ^ xor, height ^ xor);
        offset = 64;

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = 0;
            Cells[x, y].MiddleIndex = 1;
            Cells[x, y].BackImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor);
            offset += 2;
            Cells[x, y].MiddleImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor);
            offset += 2;
            Cells[x, y].FrontImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor);
            offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 2);
            Cells[x, y].Light = _bytes[offset++];

            if ((Cells[x, y].BackImage & 0x8000) != 0)
            {
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            }

            SetFishing(x, y);
        }
    }

    private void LoadType5()
    {
        int offset = 22;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 28;

        for (int x = 0; x < Width / 2; x++)
        for (int y = 0; y < Height / 2; y++)
        {
            for (int i = 0; i < 4; i++)
            {
                int cellX = x * 2 + i % 2;
                int cellY = y * 2 + i / 2;
                Cells[cellX, cellY].BackIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 200 : -1);
                Cells[cellX, cellY].BackImage = BitConverter.ToUInt16(_bytes, offset + 1) + 1;
            }

            offset += 3;
        }

        offset = 28 + 3 * ((Width / 2) + (Width % 2)) * (Height / 2);
        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            byte flag = _bytes[offset++];
            Cells[x, y].MiddleAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset] == 255 ? (byte)0 : _bytes[offset];
            Cells[x, y].FrontAnimationFrame &= 0x8F;
            offset++;
            Cells[x, y].MiddleAnimationTick = 0;
            Cells[x, y].FrontAnimationTick = 0;
            Cells[x, y].FrontIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 200 : -1);
            offset++;
            Cells[x, y].MiddleIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 200 : -1);
            offset++;
            Cells[x, y].MiddleImage = BitConverter.ToUInt16(_bytes, offset) + 1;
            offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToUInt16(_bytes, offset) + 1;
            if (Cells[x, y].FrontImage == 1 && Cells[x, y].FrontIndex == 200) Cells[x, y].FrontIndex = -1;
            offset += 5;
            Cells[x, y].Light = (byte)(_bytes[offset] & 0x0F);
            offset += 2;

            if ((flag & 0x01) != 1) Cells[x, y].BackImage |= 0x20000000;
            if ((flag & 0x02) != 2) Cells[x, y].FrontImage = (ushort)(Cells[x, y].FrontImage | 0x8000);

            if (Cells[x, y].Light >= 100 && Cells[x, y].Light <= 119)
            {
                Cells[x, y].FishingCell = true;
            }
            else
            {
                Cells[x, y].Light *= 2;
            }
        }
    }

    private void LoadType6()
    {
        int offset = 16;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 40;

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            byte flag = _bytes[offset++];
            Cells[x, y].BackIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 300 : -1);
            offset++;
            Cells[x, y].MiddleIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 300 : -1);
            offset++;
            Cells[x, y].FrontIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 300 : -1);
            offset++;
            Cells[x, y].BackImage = BitConverter.ToInt16(_bytes, offset) + 1;
            offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset) + 1;
            offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset) + 1;
            offset += 2;
            if (Cells[x, y].FrontImage == 1 && Cells[x, y].FrontIndex == 200) Cells[x, y].FrontIndex = -1;
            Cells[x, y].MiddleAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset] == 255 ? (byte)0 : _bytes[offset];
            if (Cells[x, y].FrontAnimationFrame > 0x0F)
            {
                Cells[x, y].FrontAnimationFrame = (byte)(Cells[x, y].FrontAnimationFrame & 0x0F);
            }
            offset++;
            Cells[x, y].MiddleAnimationTick = 1;
            Cells[x, y].FrontAnimationTick = 1;
            Cells[x, y].Light = (byte)(_bytes[offset] & 0x0F);
            Cells[x, y].Light *= 4;
            offset += 8;
            if ((flag & 0x01) != 1) Cells[x, y].BackImage |= 0x20000000;
            if ((flag & 0x02) != 2) Cells[x, y].FrontImage = (ushort)(Cells[x, y].FrontImage | 0x8000);
        }
    }

    private void LoadType7()
    {
        int offset = 21;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 4));
        offset = 54;

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = 0;
            Cells[x, y].BackImage = BitConverter.ToInt32(_bytes, offset);
            offset += 4;
            Cells[x, y].MiddleIndex = 1;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 2);
            Cells[x, y].Light = _bytes[offset++];
            Cells[x, y].Unknown = _bytes[offset++];

            if ((Cells[x, y].BackImage & 0x8000) != 0)
            {
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            }

            SetFishing(x, y);
        }
    }

    private void LoadType100()
    {
        int offset = 4;
        if (_bytes[0] != 1 || _bytes[1] != 0) return;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 8;

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].BackImage = BitConverter.ToInt32(_bytes, offset);
            offset += 4;
            Cells[x, y].MiddleIndex = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].FrontIndex = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].MiddleAnimationFrame = _bytes[offset++];
            Cells[x, y].MiddleAnimationTick = _bytes[offset++];
            Cells[x, y].TileAnimationImage = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].TileAnimationOffset = BitConverter.ToInt16(_bytes, offset);
            offset += 2;
            Cells[x, y].TileAnimationFrames = _bytes[offset++];
            Cells[x, y].Light = _bytes[offset++];
            SetFishing(x, y);
        }
    }
}
