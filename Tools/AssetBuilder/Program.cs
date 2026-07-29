using Shared.StreamingAssets;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBuilder;

internal static class Program
{
    private const string BuildStateFileName = ".assetbuilder-state.json";

    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "self-test", StringComparison.OrdinalIgnoreCase))
            return SelfTest(args.Length > 1 ? Path.GetFullPath(args[1]) : null);

        if (args.Length > 0 && string.Equals(args[0], "migrate-v2", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: AssetBuilder migrate-v2 <v1 StreamingAssets> <v2 output>");
                return 1;
            }

            return MigrateV2(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
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
        if (previousManifest?.FormatVersion != StreamingAssetConstants.CurrentFormatVersion)
            previousManifest = null;
        BuildState previousState = TryReadJson<BuildState>(Path.Combine(output, BuildStateFileName)) ?? new BuildState();
        if (previousState.FormatVersion != StreamingAssetConstants.CurrentFormatVersion)
            previousState = new BuildState();
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

        if (progress.FailedCount > 0)
        {
            Console.Error.WriteLine($"Build failed: {progress.FailedCount} resource(s) could not be converted. The published manifest was not changed.");
            return 1;
        }

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
            WriteJsonAtomically(Path.Combine(output, StreamingAssetConstants.ManifestFileName), manifest);
        }

        WriteJsonAtomically(Path.Combine(output, BuildStateFileName), nextState);

        Console.WriteLine($"Streaming assets built at {output}");
        Console.WriteLine($"Libraries: {manifest.Libraries.Count}, Maps: {manifest.Maps.Count}, Sounds: {manifest.Sounds.Count}");
        Console.WriteLine($"Rebuilt: {progress.RebuiltCount}, Reused: {progress.ReusedCount}, Failed: {progress.FailedCount}");
        Console.WriteLine($"Manifest version: {manifest.Version}");
        return 0;
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        string temp = path + ".tmp";
        StreamingAssetIO.WriteJson(temp, value);
        File.Move(temp, path, true);
    }

    private static int MigrateV2(string source, string output)
    {
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), output.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("v1 source and v2 output must be different directories.");
            return 1;
        }

        string sourceManifestPath = Path.Combine(source, StreamingAssetConstants.ManifestFileName);
        if (!File.Exists(sourceManifestPath))
        {
            Console.Error.WriteLine($"v1 manifest not found: {sourceManifestPath}");
            return 1;
        }

        try
        {
            LegacyAssetManifest legacy = JsonSerializer.Deserialize<LegacyAssetManifest>(
                File.ReadAllBytes(sourceManifestPath), StreamingAssetIO.JsonOptions)
                ?? throw new InvalidDataException("Invalid v1 root manifest.");
            if (legacy.FormatVersion != 1)
                throw new InvalidDataException($"Expected v1 resources, found format {legacy.FormatVersion}.");

            Directory.CreateDirectory(output);
            AssetManifest manifest = new()
            {
                Version = DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
                CreatedUtc = DateTime.UtcNow,
                MapChunkSize = legacy.MapChunkSize > 0 ? legacy.MapChunkSize : StreamingAssetConstants.DefaultMapChunkSize
            };

            for (int i = 0; i < legacy.Libraries.Count; i++)
            {
                LegacyAssetLibraryRecord record = legacy.Libraries[i];
                Console.Write($"\r[Libraries] {i + 1}/{legacy.Libraries.Count} {record.Id}".PadRight(160));
                string manifestPath = ResolveLegacyPath(source, source, record.ManifestPath);
                LegacyLibraryManifest library = JsonSerializer.Deserialize<LegacyLibraryManifest>(
                    File.ReadAllBytes(manifestPath), StreamingAssetIO.JsonOptions)
                    ?? throw new InvalidDataException($"Invalid library manifest: {record.Id}");
                List<LegacyLibraryImageRecord> images = ReadLegacyImages(source, manifestPath, library);
                if (images.Count != library.ImageCount)
                    throw new InvalidDataException($"Library image count mismatch: {record.Id}");

                LibraryManifest converted = new()
                {
                    Id = record.Id,
                    ImageCount = library.ImageCount,
                    Frames = library.Frames
                };
                for (int pageIndex = 0; pageIndex * StreamingAssetConstants.LibraryIndexPageSize < images.Count; pageIndex++)
                {
                    int start = pageIndex * StreamingAssetConstants.LibraryIndexPageSize;
                    List<LibraryImageRecord> pageImages = new();
                    foreach (LegacyLibraryImageRecord image in images.Skip(start).Take(StreamingAssetConstants.LibraryIndexPageSize))
                    {
                        LibraryImageRecord convertedImage = new()
                        {
                            Index = image.Index,
                            Width = image.Width,
                            Height = image.Height,
                            X = image.X,
                            Y = image.Y
                        };
                        if (!string.IsNullOrEmpty(image.Path))
                        {
                            string imagePath = ResolveLegacyPath(source, Path.GetDirectoryName(manifestPath)!, image.Path);
                            ObjectRecord imageObject = ObjectStore.ImportVerified(output, imagePath, image.Hash,
                                image.FileLength > 0 ? image.FileLength : new FileInfo(imagePath).Length);
                            convertedImage.Hash = imageObject.Hash;
                            convertedImage.Length = imageObject.Length;
                        }
                        pageImages.Add(convertedImage);
                    }

                    LibraryManifestPage page = new() { PageIndex = pageIndex, Start = start, Images = pageImages };
                    ObjectRecord pageObject = ObjectStore.Write(output, StreamingAssetIO.WriteLibraryIndexPage(page));
                    converted.Pages.Add(new LibraryManifestPageRecord
                    {
                        Start = start,
                        Count = pageImages.Count,
                        Hash = pageObject.Hash,
                        Length = pageObject.Length
                    });
                }

                ObjectRecord libraryObject = ObjectStore.Write(output,
                    JsonSerializer.SerializeToUtf8Bytes(converted, StreamingAssetIO.JsonOptions));
                manifest.Libraries.Add(new AssetLibraryRecord
                {
                    Id = converted.Id,
                    ImageCount = converted.ImageCount,
                    Hash = libraryObject.Hash,
                    Length = libraryObject.Length
                });
            }
            Console.WriteLine();

            for (int i = 0; i < legacy.Maps.Count; i++)
            {
                LegacyAssetMapRecord record = legacy.Maps[i];
                Console.Write($"\r[Maps] {i + 1}/{legacy.Maps.Count} {record.Id}".PadRight(160));
                string manifestPath = ResolveLegacyPath(source, source, record.ManifestPath);
                LegacyMapManifest map = JsonSerializer.Deserialize<LegacyMapManifest>(
                    File.ReadAllBytes(manifestPath), StreamingAssetIO.JsonOptions)
                    ?? throw new InvalidDataException($"Invalid map manifest: {record.Id}");
                MapManifest converted = new()
                {
                    Id = record.Id,
                    Width = map.Width,
                    Height = map.Height,
                    ChunkSize = map.ChunkSize
                };
                foreach (LegacyMapChunkRecord chunk in map.Chunks)
                {
                    string chunkPath = ResolveLegacyPath(source, Path.GetDirectoryName(manifestPath)!, chunk.Path);
                    ObjectRecord chunkObject = ObjectStore.ImportVerified(output, chunkPath, chunk.Hash, chunk.Length);
                    converted.Chunks.Add(new MapChunkRecord
                    {
                        X = chunk.X,
                        Y = chunk.Y,
                        Width = chunk.Width,
                        Height = chunk.Height,
                        Hash = chunkObject.Hash,
                        Length = chunkObject.Length
                    });
                }

                ObjectRecord mapObject = ObjectStore.Write(output,
                    JsonSerializer.SerializeToUtf8Bytes(converted, StreamingAssetIO.JsonOptions));
                manifest.Maps.Add(new AssetMapRecord
                {
                    Id = converted.Id,
                    Width = converted.Width,
                    Height = converted.Height,
                    Hash = mapObject.Hash,
                    Length = mapObject.Length
                });
            }
            Console.WriteLine();

            for (int i = 0; i < legacy.Sounds.Count; i++)
            {
                LegacyAssetSoundRecord record = legacy.Sounds[i];
                Console.Write($"\r[Sounds] {i + 1}/{legacy.Sounds.Count} {record.Id}".PadRight(160));
                string path = ResolveLegacyPath(source, source, record.Path);
                ObjectRecord soundObject = ObjectStore.ImportVerified(output, path, record.Hash, record.Length);
                manifest.Sounds.Add(new AssetSoundRecord
                {
                    Id = record.Id,
                    Hash = soundObject.Hash,
                    Length = soundObject.Length,
                    Extension = Path.GetExtension(path).ToLowerInvariant()
                });
            }
            Console.WriteLine();

            WriteJsonAtomically(Path.Combine(output, StreamingAssetConstants.ManifestFileName), manifest);
            Console.WriteLine($"v2 migration complete: {output}");
            Console.WriteLine($"Libraries: {manifest.Libraries.Count}, Maps: {manifest.Maps.Count}, Sounds: {manifest.Sounds.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"v2 migration failed: {ex.Message}");
            Console.Error.WriteLine("No v2 root manifest was published.");
            return 1;
        }
    }

    private static int SelfTest(string retainedRoot)
    {
        string root = retainedRoot ?? Path.Combine(Path.GetTempPath(),
            "YangfeiCrystal-AssetBuilder-" + Guid.NewGuid().ToString("N"));
        if (retainedRoot != null && Directory.Exists(root))
        {
            Console.Error.WriteLine($"Self-test output already exists: {root}");
            return 1;
        }
        string v1 = Path.Combine(root, "v1");
        string v2 = Path.Combine(root, "v2");
        try
        {
            string libraryRoot = Path.Combine(v1, "libraries", "test");
            string imagePath = Path.Combine(libraryRoot, "images", "0.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            byte[] image = StreamingAssetIO.WriteLibraryImageChunk(2, 3, 4, 5, 0, 0, 0,
                new byte[] { 1, 2, 3, 4 }, 0, 0, 0, 0, Array.Empty<byte>());
            File.WriteAllBytes(imagePath, image);
            LegacyLibraryManifest library = new()
            {
                ImageCount = 1,
                Images = new List<LegacyLibraryImageRecord>
                {
                    new()
                    {
                        Index = 0,
                        Path = "images/0.bin",
                        Width = 2,
                        Height = 3,
                        X = 4,
                        Y = 5,
                        Hash = StreamingAssetIO.ComputeSha256(image),
                        FileLength = image.Length
                    }
                }
            };
            string libraryManifestPath = Path.Combine(libraryRoot, "manifest.json");
            StreamingAssetIO.WriteJson(libraryManifestPath, library);

            StreamingMapChunk mapChunk = new()
            {
                X = 0,
                Y = 0,
                Width = 1,
                Height = 1,
                Cells = new[] { new StreamingMapCell { BackIndex = 1, BackImage = 2 } }
            };
            byte[] mapBytes = StreamingAssetIO.WriteMapChunk(mapChunk);
            string mapChunkPath = Path.Combine(v1, "maps", "test", "chunks", "0_0.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(mapChunkPath)!);
            File.WriteAllBytes(mapChunkPath, mapBytes);
            LegacyMapManifest map = new()
            {
                Width = 1,
                Height = 1,
                ChunkSize = 32,
                Chunks = new List<LegacyMapChunkRecord>
                {
                    new()
                    {
                        X = 0, Y = 0, Width = 1, Height = 1, Path = "chunks/0_0.bin",
                        Hash = StreamingAssetIO.ComputeSha256(mapBytes), Length = mapBytes.Length
                    }
                }
            };
            StreamingAssetIO.WriteJson(Path.Combine(v1, "maps", "test", "manifest.json"), map);

            byte[] sound = { 10, 20, 30, 40 };
            string soundPath = Path.Combine(v1, "sounds", "test.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(soundPath)!);
            File.WriteAllBytes(soundPath, sound);

            LegacyAssetManifest rootManifest = new()
            {
                FormatVersion = 1,
                MapChunkSize = 32,
                Libraries = new List<LegacyAssetLibraryRecord>
                {
                    new() { Id = "test", ManifestPath = "libraries/test/manifest.json" }
                },
                Maps = new List<LegacyAssetMapRecord>
                {
                    new() { Id = "test", ManifestPath = "maps/test/manifest.json" }
                },
                Sounds = new List<LegacyAssetSoundRecord>
                {
                    new()
                    {
                        Id = "test.wav", Path = "sounds/test.wav",
                        Hash = StreamingAssetIO.ComputeSha256(sound), Length = sound.Length
                    }
                }
            };
            StreamingAssetIO.WriteJson(Path.Combine(v1, StreamingAssetConstants.ManifestFileName), rootManifest);

            if (MigrateV2(v1, v2) != 0) throw new InvalidOperationException("Migration returned an error.");
            AssetManifest converted = StreamingAssetIO.ReadJson<AssetManifest>(
                Path.Combine(v2, StreamingAssetConstants.ManifestFileName));
            if (converted.FormatVersion != 2 || converted.Libraries.Count != 1 ||
                converted.Maps.Count != 1 || converted.Sounds.Count != 1)
                throw new InvalidDataException("Invalid converted root manifest.");

            AssetLibraryRecord libraryRecord = converted.Libraries[0];
            LibraryManifest convertedLibrary = JsonSerializer.Deserialize<LibraryManifest>(
                File.ReadAllBytes(ObjectStore.GetPath(v2, libraryRecord.Hash)), StreamingAssetIO.JsonOptions)!;
            LibraryManifestPage convertedPage = StreamingAssetIO.ReadLibraryIndexPage(
                File.ReadAllBytes(ObjectStore.GetPath(v2, convertedLibrary.Pages[0].Hash)));
            LibraryImageRecord convertedImage = convertedPage.Images.Single();
            if (convertedImage.Index != 0 || convertedImage.Width != 2 || convertedImage.Height != 3 ||
                convertedImage.X != 4 || convertedImage.Y != 5 ||
                !ObjectStore.Exists(v2, convertedImage.Hash, convertedImage.Length))
                throw new InvalidDataException("Invalid converted library page.");

            Console.WriteLine("AssetBuilder self-test passed.");
            if (retainedRoot != null) Console.WriteLine($"Retained v2 fixture: {v2}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AssetBuilder self-test failed: {ex}");
            return 1;
        }
        finally
        {
            try { if (retainedRoot == null && Directory.Exists(root)) Directory.Delete(root, true); }
            catch { }
        }
    }

    private static List<LegacyLibraryImageRecord> ReadLegacyImages(string root, string manifestPath,
        LegacyLibraryManifest manifest)
    {
        if (manifest.Images.Count > 0) return manifest.Images.OrderBy(image => image.Index).ToList();
        if (manifest.Pages == null || manifest.Pages.Count == 0) return new();

        List<LegacyLibraryImageRecord> images = new(manifest.ImageCount);
        foreach (LegacyLibraryManifestPageRecord pageRecord in manifest.Pages.OrderBy(page => page.Start))
        {
            string path = ResolveLegacyPath(root, Path.GetDirectoryName(manifestPath)!, pageRecord.Path);
            LegacyLibraryManifestPage page = JsonSerializer.Deserialize<LegacyLibraryManifestPage>(
                File.ReadAllBytes(path), StreamingAssetIO.JsonOptions)
                ?? throw new InvalidDataException($"Invalid legacy library page: {path}");
            images.AddRange(page.Images);
        }
        return images.OrderBy(image => image.Index).ToList();
    }

    private static string ResolveLegacyPath(string root, string baseDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid legacy path: {relativePath}");

        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(baseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Legacy asset not found.", path);
        return path;
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
            SourceStateEntry sourceState = CreateSourceState("library", id, dataPath, file, previousState, options.Verify);
            nextState.Sources.Add(sourceState);

            if (!options.ForceRebuild && (!sourceState.Changed || options.AdoptExisting) &&
                TryReuseLibrary(output, previous, id, options.AdoptExisting || options.Verify,
                    out AssetLibraryRecord existing))
            {
                manifest.Libraries.Add(existing);
                progress.CompleteItem(file, false);
                continue;
            }

            try
            {
                LibraryManifest libraryManifest = StreamingLibraryReader.ReadLibrary(file, id, output,
                    (current, total) => progress.ReportItemProgress(file, index, current, total));
                byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(libraryManifest, StreamingAssetIO.JsonOptions);
                ObjectRecord libraryObject = ObjectStore.Write(output, manifestBytes);
                manifest.Libraries.Add(new AssetLibraryRecord
                {
                    Id = id,
                    ImageCount = libraryManifest.ImageCount,
                    Length = libraryObject.Length,
                    Hash = libraryObject.Hash
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
            SourceStateEntry sourceState = CreateSourceState("map", id, mapPath, file, previousState, options.Verify);
            nextState.Sources.Add(sourceState);

            try
            {
                if (!options.ForceRebuild && (!sourceState.Changed || options.AdoptExisting) &&
                    TryReuseMap(output, previous, id, options.AdoptExisting || options.Verify,
                        out AssetMapRecord existing))
                {
                    manifest.Maps.Add(existing);
                    progress.CompleteItem(file, false);
                    continue;
                }

                MapManifest mapManifest = StreamingMapReader.ReadMap(file, id, output, StreamingAssetConstants.DefaultMapChunkSize);
                byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(mapManifest, StreamingAssetIO.JsonOptions);
                ObjectRecord mapObject = ObjectStore.Write(output, manifestBytes);
                manifest.Maps.Add(new AssetMapRecord
                {
                    Id = id,
                    Width = mapManifest.Width,
                    Height = mapManifest.Height,
                    Length = mapObject.Length,
                    Hash = mapObject.Hash
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
            SourceStateEntry sourceState = CreateSourceState("sound", id, soundPath, file, previousState, options.Verify);
            nextState.Sources.Add(sourceState);

            if (!options.ForceRebuild && (!sourceState.Changed || options.AdoptExisting) &&
                TryReuseSound(output, previous, id, options.AdoptExisting || options.Verify,
                    out AssetSoundRecord existing))
            {
                manifest.Sounds.Add(existing);
                progress.CompleteItem(file, false);
                continue;
            }

            using FileStream stream = File.OpenRead(file);
            ObjectRecord soundObject = ObjectStore.Write(output, stream);
            manifest.Sounds.Add(new AssetSoundRecord
            {
                Id = id,
                Length = soundObject.Length,
                Hash = soundObject.Hash,
                Extension = Path.GetExtension(file).ToLowerInvariant()
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
        bool strict, out AssetLibraryRecord record)
    {
        if (!previous.TryGetValue(id, out record)) return false;
        if (!(strict ? ObjectStore.Verify(output, record.Hash, record.Length) :
                ObjectStore.Exists(output, record.Hash, record.Length))) return false;
        if (!strict) return true;

        try
        {
            LibraryManifest manifest = JsonSerializer.Deserialize<LibraryManifest>(
                File.ReadAllBytes(ObjectStore.GetPath(output, record.Hash)), StreamingAssetIO.JsonOptions);
            return manifest != null && manifest.FormatVersion == StreamingAssetConstants.CurrentFormatVersion &&
                   string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase) &&
                   manifest.ImageCount == record.ImageCount && ValidateLibraryObjects(output, manifest);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReuseMap(string output, Dictionary<string, AssetMapRecord> previous, string id,
        bool strict, out AssetMapRecord record)
    {
        if (!previous.TryGetValue(id, out record)) return false;
        if (!(strict ? ObjectStore.Verify(output, record.Hash, record.Length) :
                ObjectStore.Exists(output, record.Hash, record.Length))) return false;
        if (!strict) return true;

        try
        {
            MapManifest manifest = JsonSerializer.Deserialize<MapManifest>(
                File.ReadAllBytes(ObjectStore.GetPath(output, record.Hash)), StreamingAssetIO.JsonOptions);
            return manifest != null && manifest.FormatVersion == StreamingAssetConstants.CurrentFormatVersion &&
                   string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase) &&
                   manifest.Width == record.Width && manifest.Height == record.Height &&
                   manifest.Chunks.All(chunk => ObjectStore.Verify(output, chunk.Hash, chunk.Length));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReuseSound(string output, Dictionary<string, AssetSoundRecord> previous, string id,
        bool strict, out AssetSoundRecord record)
    {
        if (!previous.TryGetValue(id, out record)) return false;
        return strict
            ? ObjectStore.Verify(output, record.Hash, record.Length)
            : ObjectStore.Exists(output, record.Hash, record.Length);
    }

    private static bool ValidateLibraryObjects(string output, LibraryManifest manifest)
    {
        if (manifest.PageSize != StreamingAssetConstants.LibraryIndexPageSize || manifest.Pages == null)
            return false;

        int expectedStart = 0;
        foreach (LibraryManifestPageRecord page in manifest.Pages)
        {
            if (page.Start != expectedStart || page.Count <= 0 || page.Count > manifest.PageSize ||
                !ObjectStore.Verify(output, page.Hash, page.Length))
                return false;

            LibraryManifestPage pageData;
            try
            {
                pageData = StreamingAssetIO.ReadLibraryIndexPage(
                    File.ReadAllBytes(ObjectStore.GetPath(output, page.Hash)));
            }
            catch
            {
                return false;
            }
            if (pageData.Start != page.Start || pageData.Images.Count != page.Count ||
                pageData.Images.Any(image => image.Exists && !ObjectStore.Verify(output, image.Hash, image.Length)))
                return false;
            expectedStart += page.Count;
        }

        return expectedStart == manifest.ImageCount;
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
        public int FormatVersion { get; set; } = StreamingAssetConstants.CurrentFormatVersion;
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

internal readonly record struct ObjectRecord(string Hash, long Length);

internal static class ObjectStore
{
    private static readonly HashSet<string> VerifiedPaths = new(StringComparer.OrdinalIgnoreCase);

    public static ObjectRecord Write(string output, byte[] data)
    {
        string hash = StreamingAssetIO.ComputeSha256(data);
        string path = GetPath(output, hash);
        if (!HasExpectedLength(path, data.LongLength) || !Verify(output, hash, data.LongLength))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + $".{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, true);
        }

        VerifiedPaths.Add(path);
        return new ObjectRecord(hash, data.LongLength);
    }

    public static ObjectRecord Write(string output, Stream source)
    {
        long originalPosition = source.CanSeek ? source.Position : 0;
        string hash = StreamingAssetIO.ComputeSha256(source);
        if (source.CanSeek) source.Position = originalPosition;

        long length = source.CanSeek ? source.Length - originalPosition : 0;
        string path = GetPath(output, hash);
        if (!HasExpectedLength(path, length) || !Verify(output, hash, length))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + $".{Environment.ProcessId}.tmp";
            using (FileStream destination = File.Create(temp))
                source.CopyTo(destination);
            File.Move(temp, path, true);
        }

        VerifiedPaths.Add(path);
        return new ObjectRecord(hash, length);
    }

    public static ObjectRecord ImportVerified(string output, string sourcePath, string expectedHash, long expectedLength)
    {
        FileInfo info = new(sourcePath);
        if (!info.Exists || info.Length != expectedLength || !StreamingAssetIO.IsValidSha256(expectedHash))
            throw new InvalidDataException($"Invalid legacy object: {sourcePath}");

        string normalizedHash = expectedHash.ToLowerInvariant();
        string destination = GetPath(output, normalizedHash);
        if (Verify(output, normalizedHash, expectedLength))
            return new ObjectRecord(normalizedHash, expectedLength);

        using (FileStream source = File.OpenRead(sourcePath))
        {
            string actualHash = StreamingAssetIO.ComputeSha256(source);
            if (!string.Equals(actualHash, normalizedHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Legacy object hash mismatch: {sourcePath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + $".{Environment.ProcessId}.tmp";
        try { if (File.Exists(temp)) File.Delete(temp); }
        catch { }
        if (!OperatingSystem.IsWindows() ||
            !NativeMethods.CreateHardLink(temp, sourcePath, IntPtr.Zero))
            File.Copy(sourcePath, temp, true);
        File.Move(temp, destination, true);
        VerifiedPaths.Add(destination);
        return new ObjectRecord(normalizedHash, expectedLength);
    }

    public static bool Exists(string output, string hash, long length)
    {
        return StreamingAssetIO.IsValidSha256(hash) && HasExpectedLength(GetPath(output, hash), length);
    }

    public static bool Verify(string output, string hash, long length)
    {
        if (!Exists(output, hash, length)) return false;
        string path = GetPath(output, hash);
        if (VerifiedPaths.Contains(path)) return true;
        try
        {
            using FileStream stream = File.OpenRead(path);
            bool valid = string.Equals(StreamingAssetIO.ComputeSha256(stream), hash,
                StringComparison.OrdinalIgnoreCase);
            if (valid) VerifiedPaths.Add(path);
            return valid;
        }
        catch
        {
            return false;
        }
    }

    public static string GetPath(string output, string hash)
    {
        return Path.Combine(output, StreamingAssetIO.GetObjectRelativePath(hash).Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool HasExpectedLength(string path, long length)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists && info.Length == length;
        }
        catch
        {
            return false;
        }
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}

internal sealed class LegacyAssetManifest
{
    public int FormatVersion { get; set; }
    public int MapChunkSize { get; set; }
    public List<LegacyAssetLibraryRecord> Libraries { get; set; } = new();
    public List<LegacyAssetMapRecord> Maps { get; set; } = new();
    public List<LegacyAssetSoundRecord> Sounds { get; set; } = new();
}

internal sealed class LegacyAssetLibraryRecord
{
    public string Id { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
}

internal sealed class LegacyAssetMapRecord
{
    public string Id { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
}

internal sealed class LegacyAssetSoundRecord
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Length { get; set; }
}

internal sealed class LegacyLibraryManifest
{
    public int ImageCount { get; set; }
    public List<LegacyLibraryImageRecord> Images { get; set; } = new();
    public List<LibraryFrameRecord> Frames { get; set; } = new();
    public List<LegacyLibraryManifestPageRecord> Pages { get; set; } = new();
}

internal sealed class LegacyLibraryManifestPageRecord
{
    public int Start { get; set; }
    public string Path { get; set; } = string.Empty;
}

internal sealed class LegacyLibraryManifestPage
{
    public List<LegacyLibraryImageRecord> Images { get; set; } = new();
}

internal sealed class LegacyLibraryImageRecord
{
    public int Index { get; set; }
    public string Path { get; set; } = string.Empty;
    public short Width { get; set; }
    public short Height { get; set; }
    public short X { get; set; }
    public short Y { get; set; }
    public string Hash { get; set; } = string.Empty;
    public long FileLength { get; set; }
}

internal sealed class LegacyMapManifest
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int ChunkSize { get; set; }
    public List<LegacyMapChunkRecord> Chunks { get; set; } = new();
}

internal sealed class LegacyMapChunkRecord
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Length { get; set; }
}

internal static class StreamingLibraryReader
{
    public static LibraryManifest ReadLibrary(string file, string id, string output, Action<int, int> progress = null)
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
        List<LibraryImageRecord> pageImages = new(StreamingAssetConstants.LibraryIndexPageSize);

        for (int i = 0; i < count; i++)
        {
            if (indexList[i] <= 0 || indexList[i] >= stream.Length)
            {
                pageImages.Add(new LibraryImageRecord { Index = i });
                FlushIndexPageIfNeeded(output, manifest, pageImages, false);
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
            ObjectRecord imageObject = ObjectStore.Write(output, chunk);
            pageImages.Add(new LibraryImageRecord
            {
                Index = i,
                Width = width,
                Height = height,
                X = x,
                Y = y,
                Length = imageObject.Length,
                Hash = imageObject.Hash
            });
            FlushIndexPageIfNeeded(output, manifest, pageImages, false);

            progress?.Invoke(i + 1, count);
        }

        FlushIndexPageIfNeeded(output, manifest, pageImages, true);

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

    private static void FlushIndexPageIfNeeded(string output, LibraryManifest manifest,
        List<LibraryImageRecord> images, bool flushPartial)
    {
        if (images.Count == 0 || (!flushPartial && images.Count < StreamingAssetConstants.LibraryIndexPageSize))
            return;

        int pageIndex = manifest.Pages.Count;
        int start = pageIndex * StreamingAssetConstants.LibraryIndexPageSize;
        LibraryManifestPage page = new()
        {
            PageIndex = pageIndex,
            Start = start,
            Images = images.ToList()
        };
        byte[] data = StreamingAssetIO.WriteLibraryIndexPage(page);
        ObjectRecord pageObject = ObjectStore.Write(output, data);
        manifest.Pages.Add(new LibraryManifestPageRecord
        {
            Start = start,
            Count = images.Count,
            Hash = pageObject.Hash,
            Length = pageObject.Length
        });
        images.Clear();
    }
}

internal static class StreamingMapReader
{
    public static MapManifest ReadMap(string file, string id, string output, int chunkSize)
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
                ObjectRecord chunkObject = ObjectStore.Write(output, data);

                manifest.Chunks.Add(new MapChunkRecord
                {
                    X = chunkX,
                    Y = chunkY,
                    Width = width,
                    Height = height,
                    Length = chunkObject.Length,
                    Hash = chunkObject.Hash
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
