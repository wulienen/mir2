using Shared.StreamingAssets;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBuilder;

internal static class V3Builder
{
    private const string StateFileName = ".assetbuilder-v3-state.json";
    private const string FailureReportFileName = "assetbuilder-v3-failures.log";
    private const int BufferSize = 1024 * 1024;
    private const int LibraryFrameRecordSize = sizeof(byte) + (8 * sizeof(int)) + (2 * sizeof(byte));

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "self-test-v3", StringComparison.OrdinalIgnoreCase))
                return SelfTest(args.Skip(1).FirstOrDefault());
            if (args.Length > 0 && string.Equals(args[0], "diagnose-v3", StringComparison.OrdinalIgnoreCase))
                return Diagnose(V3DiagnosticOptions.Parse(args.Skip(1).ToArray()));

            V3BuildOptions options = V3BuildOptions.Parse(args);
            return Build(options);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int Build(V3BuildOptions options)
    {
        ValidateDirectory(options.LibraryRoot, "LibraryRoot");
        ValidateDirectory(options.MapRoot, "MapRoot");
        ValidateDirectory(options.SoundRoot, "SoundRoot");
        Directory.CreateDirectory(options.Output);

        string manifestPath = Path.Combine(options.Output, StreamingAssetV3Constants.ManifestFileName);
        string statePath = Path.Combine(options.Output, StateFileName);
        StreamingAssetV3Manifest previousManifest = ReadJson<StreamingAssetV3Manifest>(manifestPath);
        if (previousManifest?.FormatVersion != StreamingAssetV3Constants.FormatVersion) previousManifest = null;
        V3BuildState previousState = ReadJson<V3BuildState>(statePath);
        if (previousState?.FormatVersion != StreamingAssetV3Constants.FormatVersion) previousState = new();

        string previousCatalogPath = previousManifest == null ? null : GetOutputPath(options.Output,
            StreamingAssetV3IO.GetCatalogPath(previousManifest.CatalogHash));
        V3BuildState nextState = new();
        V3Progress progress = new();

        List<LibraryBuildItem> libraries = BuildLibraries(options, previousManifest, previousState,
            nextState, previousCatalogPath, progress);
        List<MapBuildItem> maps = BuildMaps(options, previousManifest, previousState,
            nextState, previousCatalogPath, progress);
        List<V3SoundRecord> sounds = BuildSounds(options, previousManifest, previousState, nextState, progress);
        progress.FinishStage();

        if (progress.FailedCount > 0)
        {
            string failureReportPath = Path.Combine(options.Output, FailureReportFileName);
            progress.WriteFailureReport(failureReportPath);
            Console.Error.WriteLine($"Build failed: {progress.FailedCount} resource(s). Published manifest was not changed.");
            Console.Error.WriteLine($"Failure report: {failureReportPath}");
            return 1;
        }

        string catalogTemp = Path.Combine(options.Output, StreamingAssetV3Constants.CatalogsDirectory,
            $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogTemp)!);
        using (FileStream catalog = File.Create(catalogTemp))
        {
            foreach (LibraryBuildItem item in libraries.OrderBy(item => item.Record.Id, StringComparer.Ordinal))
            {
                item.Record.CatalogOffset = catalog.Position;
                item.Record.CatalogLength = item.CatalogBlock.Length;
                item.Record.CatalogBlockHash = StreamingAssetIO.ComputeSha256(item.CatalogBlock);
                catalog.Write(item.CatalogBlock);
            }
            foreach (MapBuildItem item in maps.OrderBy(item => item.Record.Id, StringComparer.Ordinal))
            {
                item.Record.CatalogOffset = catalog.Position;
                item.Record.CatalogLength = item.CatalogBlock.Length;
                item.Record.CatalogBlockHash = StreamingAssetIO.ComputeSha256(item.CatalogBlock);
                catalog.Write(item.CatalogBlock);
            }
        }

        FileInfo catalogInfo = new(catalogTemp);
        string catalogHash;
        using (FileStream stream = File.OpenRead(catalogTemp)) catalogHash = StreamingAssetIO.ComputeSha256(stream);
        string catalogPath = GetOutputPath(options.Output, StreamingAssetV3IO.GetCatalogPath(catalogHash));
        PublishTemp(catalogTemp, catalogPath, catalogInfo.Length);

        StreamingAssetV3Manifest manifest = new()
        {
            CreatedUtc = DateTime.UtcNow,
            CatalogHash = catalogHash,
            CatalogLength = catalogInfo.Length,
            Libraries = libraries.Select(item => item.Record).OrderBy(item => item.Id, StringComparer.Ordinal).ToList(),
            Maps = maps.Select(item => item.Record).OrderBy(item => item.Id, StringComparer.Ordinal).ToList(),
            Sounds = sounds.OrderBy(item => item.Id, StringComparer.Ordinal).ToList()
        };

        bool changed = previousManifest == null || !ManifestContentEquals(previousManifest, manifest) ||
                       (!string.IsNullOrWhiteSpace(options.Version) &&
                        !string.Equals(options.Version, previousManifest.Version, StringComparison.Ordinal));
        manifest.Version = !string.IsNullOrWhiteSpace(options.Version)
            ? options.Version
            : changed ? DateTime.UtcNow.ToString("yyyyMMddHHmmss") : previousManifest.Version;
        if (!changed) manifest.CreatedUtc = previousManifest.CreatedUtc;

        if (changed) WriteJsonAtomically(manifestPath, manifest);
        WriteJsonAtomically(statePath, nextState);
        try { File.Delete(Path.Combine(options.Output, FailureReportFileName)); }
        catch (Exception ex) { Console.Error.WriteLine($"Could not remove the old failure report: {ex.Message}"); }

        Console.WriteLine($"Streaming assets V3 built at {options.Output}");
        Console.WriteLine($"Libraries: {manifest.Libraries.Count}, Maps: {manifest.Maps.Count}, Sounds: {manifest.Sounds.Count}");
        Console.WriteLine($"Rebuilt: {progress.RebuiltCount}, Reused: {progress.ReusedCount}, Failed: {progress.FailedCount}");
        Console.WriteLine($"Manifest version: {manifest.Version}");
        return 0;
    }

    private static int Diagnose(V3DiagnosticOptions options)
    {
        ValidateDirectory(options.LibraryRoot, "LibraryRoot");
        ValidateDirectory(options.MapRoot, "MapRoot");
        ValidateDirectory(options.SoundRoot, "SoundRoot");

        List<(string File, string Error)> failures = new();
        List<string> libraries = Directory.EnumerateFiles(options.LibraryRoot, "*.Lib", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        DiagnoseFiles("Libraries", libraries, file =>
        {
            string id = ToAssetId(Path.GetRelativePath(options.LibraryRoot, Path.ChangeExtension(file, null)));
            _ = ReadLibraryIndex(file, id, null);
        }, failures);

        List<string> maps = Directory.EnumerateFiles(options.MapRoot, "*.map", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        DiagnoseFiles("Maps", maps, file => _ = MapData.Read(file), failures);

        List<string> sounds = Directory.EnumerateFiles(options.SoundRoot, "*.*", SearchOption.AllDirectories)
            .Where(IsSoundFile).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        DiagnoseFiles("Sounds", sounds, file =>
        {
            using FileStream stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        }, failures);

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("AssetBuilder V3 diagnostics passed. No invalid source resources were found.");
            return 0;
        }

        Console.Error.WriteLine($"AssetBuilder V3 diagnostics found {failures.Count} invalid resource(s):");
        foreach ((string file, string error) in failures)
        {
            Console.Error.WriteLine($"Failed: {file}");
            Console.Error.WriteLine(error);
        }
        return 1;
    }

    private static void DiagnoseFiles(string stage, IReadOnlyList<string> files, Action<string> validate,
        ICollection<(string File, string Error)> failures)
    {
        Stopwatch timer = Stopwatch.StartNew();
        int failuresBeforeStage = failures.Count;
        Console.WriteLine($"{stage}: diagnosing {files.Count} files");
        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            try
            {
                validate(file);
            }
            catch (Exception ex)
            {
                failures.Add((file, ex.Message));
                Console.WriteLine();
                Console.Error.WriteLine($"Failed: {file}");
                Console.Error.WriteLine(ex.Message);
            }

            double progress = files.Count == 0 ? 1 : (double)(i + 1) / files.Count;
            TimeSpan eta = progress <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks((long)(timer.Elapsed.Ticks * (1 - progress) / progress));
            Console.Write($"\r[{stage}] {progress,7:P1} ({i + 1}/{files.Count}), " +
                          $"failed {failures.Count - failuresBeforeStage}, elapsed {timer.Elapsed:hh\\:mm\\:ss}, ETA {eta:hh\\:mm\\:ss} | " +
                          Path.GetFileName(file).PadRight(80));
        }
        Console.WriteLine();
    }

    private static List<LibraryBuildItem> BuildLibraries(V3BuildOptions options,
        StreamingAssetV3Manifest previousManifest, V3BuildState previousState, V3BuildState nextState,
        string previousCatalogPath, V3Progress progress)
    {
        List<string> files = Directory.EnumerateFiles(options.LibraryRoot, "*.Lib", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        progress.StartStage("Libraries", files);
        Dictionary<string, V3LibraryRecord> previous = previousManifest?.Libraries
            .ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        List<LibraryBuildItem> output = new(files.Count);

        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            string id = ToAssetId(Path.GetRelativePath(options.LibraryRoot, Path.ChangeExtension(file, null)));
            V3SourceState source = CreateSourceState("library", id, options.LibraryRoot, file, previousState, options.Verify);
            nextState.Sources.Add(source);
            try
            {
                if (!options.Full && !source.Changed && previous.TryGetValue(id, out V3LibraryRecord old) &&
                    PublishedFileExists(options.Output, StreamingAssetV3IO.GetLibraryPath(old.FileHash), old.FileLength) &&
                    TryReadCatalogBlock(previousCatalogPath, old.CatalogOffset, old.CatalogLength,
                        old.CatalogBlockHash, out byte[] oldBlock))
                {
                    output.Add(new LibraryBuildItem(Clone(old), oldBlock));
                    progress.Complete(file, false);
                    continue;
                }

                string knownHash = source.SourceHash;
                string publishedPath;
                long fileLength = new FileInfo(file).Length;
                if (StreamingAssetIO.IsValidSha256(knownHash))
                {
                    publishedPath = GetOutputPath(options.Output, StreamingAssetV3IO.GetLibraryPath(knownHash));
                    CopyKnownFile(file, publishedPath, fileLength);
                }
                else
                {
                    (knownHash, publishedPath) = CopyHashedFile(file, options.Output,
                        StreamingAssetV3Constants.LibrariesDirectory, ".lib");
                    source.SourceHash = knownHash;
                }

                V3LibraryIndex index = ReadLibraryIndex(publishedPath, id,
                    (current, total) => progress.ReportSubItem(file, i, current, total));
                byte[] block = StreamingAssetV3IO.WriteLibraryIndex(index);
                output.Add(new LibraryBuildItem(new V3LibraryRecord
                {
                    Id = id,
                    ImageCount = index.ImageCount,
                    FileHash = knownHash,
                    FileLength = fileLength
                }, block));
                progress.Complete(file, true);
            }
            catch (Exception ex)
            {
                progress.Fail(file, ex);
            }
        }
        return output;
    }

    private static List<MapBuildItem> BuildMaps(V3BuildOptions options,
        StreamingAssetV3Manifest previousManifest, V3BuildState previousState, V3BuildState nextState,
        string previousCatalogPath, V3Progress progress)
    {
        List<string> files = Directory.EnumerateFiles(options.MapRoot, "*.map", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        progress.StartStage("Maps", files);
        Dictionary<string, V3MapRecord> previous = previousManifest?.Maps
            .ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        List<MapBuildItem> output = new(files.Count);

        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            string id = ToAssetId(Path.GetFileNameWithoutExtension(file));
            V3SourceState source = CreateSourceState("map", id, options.MapRoot, file, previousState, options.Verify);
            nextState.Sources.Add(source);
            try
            {
                if (!options.Full && !source.Changed && previous.TryGetValue(id, out V3MapRecord old) &&
                    PublishedFileExists(options.Output, StreamingAssetV3IO.GetMapPath(old.FileHash), old.FileLength) &&
                    TryReadCatalogBlock(previousCatalogPath, old.CatalogOffset, old.CatalogLength,
                        old.CatalogBlockHash, out byte[] oldBlock))
                {
                    output.Add(new MapBuildItem(Clone(old), oldBlock));
                    progress.Complete(file, false);
                    continue;
                }

                (V3MapIndex index, string packTemp) = BuildMapPack(file, id,
                    StreamingAssetV3Constants.DefaultMapChunkSize,
                    (current, total) => progress.ReportSubItem(file, i, current, total), options.Output);
                FileInfo packInfo = new(packTemp);
                string hash;
                using (FileStream stream = File.OpenRead(packTemp)) hash = StreamingAssetIO.ComputeSha256(stream);
                string publishedPath = GetOutputPath(options.Output, StreamingAssetV3IO.GetMapPath(hash));
                PublishTemp(packTemp, publishedPath, packInfo.Length);
                source.SourceHash = ComputeFileHash(file);

                byte[] block = StreamingAssetV3IO.WriteMapIndex(index);
                output.Add(new MapBuildItem(new V3MapRecord
                {
                    Id = id,
                    Width = index.Width,
                    Height = index.Height,
                    ChunkSize = index.ChunkSize,
                    FileHash = hash,
                    FileLength = packInfo.Length
                }, block));
                progress.Complete(file, true);
            }
            catch (Exception ex)
            {
                progress.Fail(file, new InvalidDataException($"Failed to build map '{file}': {ex.Message}", ex));
            }
        }
        return output;
    }

    private static List<V3SoundRecord> BuildSounds(V3BuildOptions options,
        StreamingAssetV3Manifest previousManifest, V3BuildState previousState, V3BuildState nextState,
        V3Progress progress)
    {
        List<string> files = Directory.EnumerateFiles(options.SoundRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => IsSoundFile(path)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        progress.StartStage("Sounds", files);
        Dictionary<string, V3SoundRecord> previous = previousManifest?.Sounds
            .ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        List<V3SoundRecord> output = new(files.Count);

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(options.SoundRoot, file);
            string id = ToAssetId(relative, removeKnownExtension: false);
            V3SourceState source = CreateSourceState("sound", id, options.SoundRoot, file, previousState, options.Verify);
            nextState.Sources.Add(source);
            try
            {
                if (!options.Full && !source.Changed && previous.TryGetValue(id, out V3SoundRecord old) &&
                    PublishedFileExists(options.Output, StreamingAssetV3IO.GetSoundPath(old.Hash, old.Extension), old.Length))
                {
                    output.Add(Clone(old));
                    progress.Complete(file, false);
                    continue;
                }

                string extension = Path.GetExtension(file).ToLowerInvariant();
                string hash = source.SourceHash;
                string publishedPath;
                if (StreamingAssetIO.IsValidSha256(hash))
                {
                    publishedPath = GetOutputPath(options.Output, StreamingAssetV3IO.GetSoundPath(hash, extension));
                    CopyKnownFile(file, publishedPath, new FileInfo(file).Length);
                }
                else
                {
                    (hash, publishedPath) = CopyHashedFile(file, options.Output,
                        StreamingAssetV3Constants.SoundsDirectory, extension);
                    source.SourceHash = hash;
                }

                output.Add(new V3SoundRecord
                {
                    Id = id,
                    Hash = hash,
                    Length = new FileInfo(publishedPath).Length,
                    Extension = extension
                });
                progress.Complete(file, true);
            }
            catch (Exception ex)
            {
                progress.Fail(file, ex);
            }
        }
        return output;
    }

    private static V3LibraryIndex ReadLibraryIndex(string file, string id, Action<int, int> report)
    {
        using FileStream stream = File.OpenRead(file);
        using BinaryReader reader = new(stream);
        RequireRemaining(stream, 8, file, "Lib header");
        int version = reader.ReadInt32();
        int count = reader.ReadInt32();
        if (version < 2 || version > 3) throw new InvalidDataException($"Unsupported Lib version {version}: {file}");
        if (count < 0 || count > 10_000_000) throw new InvalidDataException($"Invalid Lib image count {count}: {file}");

        int frameSeek = 0;
        if (version >= 3)
        {
            RequireRemaining(stream, 4, file, "frame offset");
            frameSeek = reader.ReadInt32();
        }
        RequireRemaining(stream, checked((long)count * 4), file, "image offset table");
        long headerLength = stream.Position + (long)count * 4;
        int[] offsets = new int[count];
        for (int i = 0; i < count; i++) offsets[i] = reader.ReadInt32();

        V3LibraryIndex index = new() { Id = id, ImageCount = count };
        for (int i = 0; i < count; i++)
        {
            int offset = offsets[i];
            if (offset <= 0)
            {
                index.Images.Add(new V3LibraryImageRecord { Index = i });
                report?.Invoke(i + 1, count);
                continue;
            }
            if (offset < headerLength || offset >= stream.Length)
                throw new InvalidDataException($"Image {i} has invalid offset {offset}: {file}");

            byte[] recordBytes = ReadLibraryRecord(stream, reader, offset, i, file);
            V3LibraryImagePayload payload = StreamingAssetV3IO.ReadLibraryImageRecord(recordBytes);
            (short trueWidth, short trueHeight) = StreamingAssetV3IO.ComputeTrueSize(
                payload.Width, payload.Height, payload.ImageData);
            index.Images.Add(new V3LibraryImageRecord
            {
                Index = i,
                Offset = offset,
                Length = recordBytes.Length,
                Width = payload.Width,
                Height = payload.Height,
                X = payload.X,
                Y = payload.Y,
                ShadowX = payload.ShadowX,
                ShadowY = payload.ShadowY,
                Shadow = payload.Shadow,
                TrueWidth = trueWidth,
                TrueHeight = trueHeight,
                Hash = StreamingAssetIO.ComputeSha256(recordBytes)
            });
            report?.Invoke(i + 1, count);
        }

        if (version >= 3 && frameSeek > 0)
        {
            if (frameSeek < headerLength || frameSeek > stream.Length - 4)
                throw new InvalidDataException($"Invalid frame offset {frameSeek}: {file}");
            stream.Position = frameSeek;
            int frameCount = reader.ReadInt32();
            if (frameCount < 0 || frameCount > 1_000_000)
                throw new InvalidDataException($"Invalid frame count {frameCount}: {file}");
            for (int i = 0; i < frameCount; i++)
            {
                RequireRemaining(stream, LibraryFrameRecordSize, file, $"frame {i}");
                index.Frames.Add(new LibraryFrameRecord
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
        return index;
    }

    private static byte[] ReadLibraryRecord(FileStream stream, BinaryReader reader, long offset, int index, string file)
    {
        stream.Position = offset;
        RequireRemaining(stream, 17, file, $"image {index} header");
        stream.Position += 12;
        byte shadow = reader.ReadByte();
        int imageLength = reader.ReadInt32();
        if (imageLength < 0 || imageLength > stream.Length - stream.Position)
            throw new InvalidDataException($"Image {index} has invalid payload length {imageLength}: {file}");
        stream.Position += imageLength;

        if ((shadow & 0x80) != 0)
        {
            RequireRemaining(stream, 12, file, $"image {index} mask header");
            stream.Position += 8;
            int maskLength = reader.ReadInt32();
            if (maskLength < 0 || maskLength > stream.Length - stream.Position)
                throw new InvalidDataException($"Image {index} has invalid mask length {maskLength}: {file}");
            stream.Position += maskLength;
        }

        long length = stream.Position - offset;
        if (length > 256L * 1024 * 1024)
            throw new InvalidDataException($"Image {index} record is too large ({length} bytes): {file}");
        stream.Position = offset;
        byte[] bytes = reader.ReadBytes((int)length);
        if (bytes.Length != length) throw new EndOfStreamException($"Truncated image {index}: {file}");
        return bytes;
    }

    private static (V3MapIndex Index, string TempPath) BuildMapPack(string file, string id, int chunkSize,
        Action<int, int> report, string output)
    {
        MapData map = MapData.Read(file);
        V3MapIndex index = new() { Id = id, Width = map.Width, Height = map.Height, ChunkSize = chunkSize };
        string directory = Path.Combine(output, StreamingAssetV3Constants.MapsDirectory);
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        int total = ((map.Width + chunkSize - 1) / chunkSize) * ((map.Height + chunkSize - 1) / chunkSize);
        int current = 0;

        using FileStream pack = File.Create(temp);
        for (int chunkX = 0; chunkX < map.Width; chunkX += chunkSize)
        for (int chunkY = 0; chunkY < map.Height; chunkY += chunkSize)
        {
            int width = Math.Min(chunkSize, map.Width - chunkX);
            int height = Math.Min(chunkSize, map.Height - chunkY);
            StreamingMapCell[] cells = new StreamingMapCell[width * height];
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                cells[x * height + y] = map.Cells[chunkX + x, chunkY + y];

            byte[] bytes = StreamingAssetIO.WriteMapChunk(new StreamingMapChunk
            {
                X = chunkX, Y = chunkY, Width = width, Height = height, Cells = cells
            });
            long offset = pack.Position;
            pack.Write(bytes);
            index.Chunks.Add(new V3MapChunkRecord
            {
                X = chunkX,
                Y = chunkY,
                Width = width,
                Height = height,
                Offset = offset,
                Length = bytes.Length,
                UncompressedLength = checked(20 + cells.Length * 32),
                Hash = StreamingAssetIO.ComputeSha256(bytes)
            });
            report?.Invoke(++current, total);
        }
        return (index, temp);
    }

    private static V3SourceState CreateSourceState(string kind, string id, string root, string file,
        V3BuildState previousState, bool verify)
    {
        FileInfo info = new(file);
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        V3SourceState previous = previousState.Find(kind, id);
        bool metadataMatches = previous != null && previous.Length == info.Length &&
                               previous.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                               string.Equals(previous.RelativePath, relative, StringComparison.OrdinalIgnoreCase);
        V3SourceState state = new()
        {
            Kind = kind,
            Id = id,
            RelativePath = relative,
            Length = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            SourceHash = previous?.SourceHash ?? string.Empty,
            Changed = !metadataMatches
        };
        if (verify || (!metadataMatches && StreamingAssetIO.IsValidSha256(previous?.SourceHash)))
        {
            state.SourceHash = ComputeFileHash(file);
            state.Changed = !string.Equals(state.SourceHash, previous?.SourceHash, StringComparison.OrdinalIgnoreCase);
        }
        return state;
    }

    private static bool TryReadCatalogBlock(string catalogPath, long offset, int length, string expectedHash,
        out byte[] block)
    {
        block = null;
        if (string.IsNullOrEmpty(catalogPath) || !File.Exists(catalogPath) || offset < 0 || length <= 0 ||
            !StreamingAssetIO.IsValidSha256(expectedHash)) return false;
        try
        {
            using FileStream stream = File.OpenRead(catalogPath);
            if (offset > stream.Length || length > stream.Length - offset) return false;
            stream.Position = offset;
            block = new byte[length];
            stream.ReadExactly(block);
            return string.Equals(StreamingAssetIO.ComputeSha256(block), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            block = null;
            return false;
        }
    }

    private static (string Hash, string Path) CopyHashedFile(string source, string output, string directory, string extension)
    {
        string targetDirectory = Path.Combine(output, directory);
        Directory.CreateDirectory(targetDirectory);
        string temp = Path.Combine(targetDirectory, $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (FileStream input = File.OpenRead(source))
        using (FileStream destination = File.Create(temp))
        {
            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                destination.Write(buffer, 0, read);
            }
        }
        string hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        string path = Path.Combine(targetDirectory, hash + extension);
        PublishTemp(temp, path, new FileInfo(source).Length);
        return (hash, path);
    }

    private static void CopyKnownFile(string source, string destination, long length)
    {
        if (new FileInfo(destination).Exists && new FileInfo(destination).Length == length) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temp = destination + $".{Environment.ProcessId}.tmp";
        File.Copy(source, temp, true);
        PublishTemp(temp, destination, length);
    }

    private static void PublishTemp(string temp, string destination, long expectedLength)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        FileInfo existing = new(destination);
        if (existing.Exists && existing.Length == expectedLength)
        {
            File.Delete(temp);
            return;
        }
        File.Move(temp, destination, true);
    }

    private static bool PublishedFileExists(string output, string relativePath, long length)
    {
        try
        {
            FileInfo info = new(GetOutputPath(output, relativePath));
            return info.Exists && info.Length == length;
        }
        catch { return false; }
    }

    private static string GetOutputPath(string output, string relativePath) =>
        Path.Combine(output, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ComputeFileHash(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return StreamingAssetIO.ComputeSha256(stream);
    }

    private static bool ManifestContentEquals(StreamingAssetV3Manifest left, StreamingAssetV3Manifest right)
    {
        if (!string.Equals(left.CatalogHash, right.CatalogHash, StringComparison.OrdinalIgnoreCase) ||
            left.CatalogLength != right.CatalogLength) return false;
        string leftJson = JsonSerializer.Serialize(new { left.Libraries, left.Maps, left.Sounds }, StreamingAssetIO.JsonOptions);
        string rightJson = JsonSerializer.Serialize(new { right.Libraries, right.Maps, right.Sounds }, StreamingAssetIO.JsonOptions);
        return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
    }

    private static T ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? StreamingAssetIO.ReadJson<T>(path) : null; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ignoring invalid metadata '{path}': {ex.Message}");
            return null;
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        string temp = path + $".{Environment.ProcessId}.tmp";
        StreamingAssetIO.WriteJson(temp, value);
        File.Move(temp, path, true);
    }

    private static string ToAssetId(string value, bool removeKnownExtension = true)
    {
        string id = value.Replace('\\', '/').TrimStart('.', '/');
        if (removeKnownExtension && (id.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) ||
                                     id.EndsWith(".map", StringComparison.OrdinalIgnoreCase)))
            id = id[..id.LastIndexOf('.')];
        return id.ToLowerInvariant();
    }

    private static bool IsSoundFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lst", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireRemaining(Stream stream, long count, string file, string part)
    {
        if (count < 0 || stream.Position > stream.Length || count > stream.Length - stream.Position)
            throw new EndOfStreamException($"Truncated {part} at offset {stream.Position}: {file}");
    }

    private static void ValidateDirectory(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new ArgumentException($"{name} directory not found: {path}");
    }

    private static V3LibraryRecord Clone(V3LibraryRecord value) => new()
    {
        Id = value.Id, ImageCount = value.ImageCount, FileHash = value.FileHash, FileLength = value.FileLength
    };

    private static V3MapRecord Clone(V3MapRecord value) => new()
    {
        Id = value.Id, Width = value.Width, Height = value.Height, ChunkSize = value.ChunkSize,
        FileHash = value.FileHash, FileLength = value.FileLength
    };

    private static V3SoundRecord Clone(V3SoundRecord value) => new()
    {
        Id = value.Id, Hash = value.Hash, Length = value.Length, Extension = value.Extension
    };

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: AssetBuilder build-v3 --library-root <dir> --map-root <dir> --sound-root <dir> --output <dir> [--version <v>] [--verify] [--full]");
        Console.Error.WriteLine("       AssetBuilder diagnose-v3 --library-root <dir> --map-root <dir> --sound-root <dir>");
        Console.Error.WriteLine("       AssetBuilder self-test-v3 [temp-dir]");
    }

    private static int SelfTest(string requestedRoot)
    {
        string root = requestedRoot == null
            ? Path.Combine(Path.GetTempPath(), "YangfeiCrystal-AssetBuilder-V3-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(requestedRoot);
        bool delete = requestedRoot == null;
        try
        {
            string libraries = Path.Combine(root, "Data_Full");
            string maps = Path.Combine(root, "Map");
            string sounds = Path.Combine(root, "Sound");
            string output = Path.Combine(root, "Output");
            Directory.CreateDirectory(libraries);
            Directory.CreateDirectory(maps);
            Directory.CreateDirectory(sounds);
            WriteTestLibrary(Path.Combine(libraries, "Title.Lib"));
            WriteTestMap(Path.Combine(maps, "test.map"));
            File.WriteAllBytes(Path.Combine(sounds, "test.wav"), new byte[] { 1, 2, 3, 4 });

            int result = Build(new V3BuildOptions
            {
                LibraryRoot = libraries, MapRoot = maps, SoundRoot = sounds, Output = output, Version = "self-test"
            });
            if (result != 0) return result;
            StreamingAssetV3Manifest manifest = StreamingAssetIO.ReadJson<StreamingAssetV3Manifest>(
                Path.Combine(output, StreamingAssetV3Constants.ManifestFileName));
            if (manifest.FormatVersion != 3 || manifest.Libraries.Count != 1 || manifest.Maps.Count != 1 ||
                manifest.Sounds.Count != 1)
                throw new InvalidDataException("Unexpected V3 self-test manifest.");

            V3LibraryRecord library = manifest.Libraries.Single();
            byte[] catalog = File.ReadAllBytes(GetOutputPath(output, StreamingAssetV3IO.GetCatalogPath(manifest.CatalogHash)));
            byte[] block = catalog.AsSpan((int)library.CatalogOffset, library.CatalogLength).ToArray();
            V3LibraryIndex index = StreamingAssetV3IO.ReadLibraryIndex(block, library.Id,
                library.ImageCount, library.FileLength);
            if (index.Images[0].TrueWidth != 1 || index.Images[0].TrueHeight != 1)
                throw new InvalidDataException("V3 true-size metadata self-test failed.");
            LibraryFrameRecord frame = index.Frames.Single();
            if (frame.Action != 2 || frame.Start != 10 || frame.Count != 4 || frame.Skip != 1 ||
                frame.Interval != 80 || frame.EffectStart != 20 || frame.EffectCount != 3 ||
                frame.EffectSkip != 2 || frame.EffectInterval != 60 || !frame.Reverse || frame.Blend)
                throw new InvalidDataException("V3 library frame metadata self-test failed.");
            byte[] paddedPixels = new byte[4 * 4 * 4];
            paddedPixels[2 * 4 + 3] = 255;
            byte[] paddedImage;
            using (MemoryStream compressed = new())
            {
                using (GZipStream gzip = new(compressed, CompressionLevel.Optimal, true))
                    gzip.Write(paddedPixels);
                paddedImage = compressed.ToArray();
            }
            if (StreamingAssetV3IO.ComputeTrueSize(4, 1, paddedImage) != (1, 1))
                throw new InvalidDataException("V3 aligned library image self-test failed.");
            try
            {
                StreamingAssetV3IO.ComputeTrueSize(4, 1, CompressTestPixels(new byte[32]));
                throw new InvalidDataException("Invalid aligned library image was accepted.");
            }
            catch (InvalidDataException ex) when (ex.Message == "Unexpected decompressed image size.")
            {
            }

            V3MapRecord mapRecord = manifest.Maps.Single();
            block = catalog.AsSpan((int)mapRecord.CatalogOffset, mapRecord.CatalogLength).ToArray();
            V3MapIndex mapIndex = StreamingAssetV3IO.ReadMapIndex(block, mapRecord.Id, mapRecord.FileLength);
            V3MapChunkRecord chunkRecord = mapIndex.Chunks.Single();
            byte[] pack = File.ReadAllBytes(GetOutputPath(output, StreamingAssetV3IO.GetMapPath(mapRecord.FileHash)));
            byte[] chunkBytes = pack.AsSpan((int)chunkRecord.Offset, chunkRecord.Length).ToArray();
            StreamingMapChunk chunk = StreamingAssetV3IO.ReadMapChunk(chunkBytes, chunkRecord);
            if (chunk.Width != 1 || chunk.Height != 1 || chunk.Cells.Length != 1)
                throw new InvalidDataException("V3 map pack self-test failed.");

            DateTime manifestWrite = File.GetLastWriteTimeUtc(Path.Combine(output,
                StreamingAssetV3Constants.ManifestFileName));
            Thread.Sleep(20);
            result = Build(new V3BuildOptions
            {
                LibraryRoot = libraries, MapRoot = maps, SoundRoot = sounds, Output = output, Version = "self-test"
            });
            if (result != 0 || File.GetLastWriteTimeUtc(Path.Combine(output,
                    StreamingAssetV3Constants.ManifestFileName)) != manifestWrite)
                throw new InvalidDataException("V3 incremental reuse self-test failed.");

            string badMap = Path.Combine(root, "truncated.map");
            File.WriteAllBytes(badMap, new byte[] { 1, 0, 0x43, 0x23, 1, 0, 1, 0 });
            try
            {
                MapData.Read(badMap);
                throw new InvalidDataException("Truncated map was accepted.");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("type 100") && ex.Message.Contains("offset"))
            {
            }

            Console.WriteLine("AssetBuilder V3 self-test passed.");
            return 0;
        }
        finally
        {
            if (delete)
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }

    private static void WriteTestLibrary(string path)
    {
        byte[] pixels = { 1, 2, 3, 255 };
        byte[] compressed;
        using (MemoryStream output = new())
        {
            using (GZipStream gzip = new(output, CompressionLevel.Optimal, true)) gzip.Write(pixels);
            compressed = output.ToArray();
        }
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        int recordOffset = 16;
        int frameOffset = recordOffset + 17 + compressed.Length;
        writer.Write(3);
        writer.Write(1);
        writer.Write(frameOffset);
        writer.Write(recordOffset);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((byte)0);
        writer.Write(compressed.Length);
        writer.Write(compressed);
        writer.Write(1);
        writer.Write((byte)2);
        writer.Write(10);
        writer.Write(4);
        writer.Write(1);
        writer.Write(80);
        writer.Write(20);
        writer.Write(3);
        writer.Write(2);
        writer.Write(60);
        writer.Write(true);
        writer.Write(false);
    }

    private static byte[] CompressTestPixels(byte[] pixels)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, true)) gzip.Write(pixels);
        return output.ToArray();
    }

    private static void WriteTestMap(string path)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((byte)0x43);
        writer.Write((byte)0x23);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write((short)0);
        writer.Write(0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
    }

    private sealed record LibraryBuildItem(V3LibraryRecord Record, byte[] CatalogBlock);
    private sealed record MapBuildItem(V3MapRecord Record, byte[] CatalogBlock);

    internal sealed class V3BuildOptions
    {
        public string LibraryRoot { get; set; }
        public string MapRoot { get; set; }
        public string SoundRoot { get; set; }
        public string Output { get; set; }
        public string Version { get; set; }
        public bool Verify { get; set; }
        public bool Full { get; set; }

        public static V3BuildOptions Parse(string[] args)
        {
            V3BuildOptions options = new();
            int index = args.Length > 0 && string.Equals(args[0], "build-v3", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            while (index < args.Length)
            {
                string arg = args[index++];
                switch (arg.ToLowerInvariant())
                {
                    case "--library-root": options.LibraryRoot = Next(args, ref index, arg); break;
                    case "--map-root": options.MapRoot = Next(args, ref index, arg); break;
                    case "--sound-root": options.SoundRoot = Next(args, ref index, arg); break;
                    case "--output": options.Output = Next(args, ref index, arg); break;
                    case "--version": options.Version = Next(args, ref index, arg); break;
                    case "--verify": options.Verify = true; break;
                    case "--full": options.Full = true; break;
                    default: throw new ArgumentException($"Unknown argument: {arg}");
                }
            }
            if (string.IsNullOrWhiteSpace(options.LibraryRoot) || string.IsNullOrWhiteSpace(options.MapRoot) ||
                string.IsNullOrWhiteSpace(options.SoundRoot) || string.IsNullOrWhiteSpace(options.Output))
                throw new ArgumentException("All of --library-root, --map-root, --sound-root and --output are required.");
            options.LibraryRoot = Path.GetFullPath(options.LibraryRoot);
            options.MapRoot = Path.GetFullPath(options.MapRoot);
            options.SoundRoot = Path.GetFullPath(options.SoundRoot);
            options.Output = Path.GetFullPath(options.Output);
            return options;
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (index >= args.Length) throw new ArgumentException($"Missing value for {option}.");
            return args[index++];
        }
    }

    private sealed class V3DiagnosticOptions
    {
        public string LibraryRoot { get; private set; }
        public string MapRoot { get; private set; }
        public string SoundRoot { get; private set; }

        public static V3DiagnosticOptions Parse(string[] args)
        {
            V3DiagnosticOptions options = new();
            int index = 0;
            while (index < args.Length)
            {
                string arg = args[index++];
                switch (arg.ToLowerInvariant())
                {
                    case "--library-root": options.LibraryRoot = Next(args, ref index, arg); break;
                    case "--map-root": options.MapRoot = Next(args, ref index, arg); break;
                    case "--sound-root": options.SoundRoot = Next(args, ref index, arg); break;
                    default: throw new ArgumentException($"Unknown argument: {arg}");
                }
            }
            if (string.IsNullOrWhiteSpace(options.LibraryRoot) || string.IsNullOrWhiteSpace(options.MapRoot) ||
                string.IsNullOrWhiteSpace(options.SoundRoot))
                throw new ArgumentException("All of --library-root, --map-root and --sound-root are required.");
            options.LibraryRoot = Path.GetFullPath(options.LibraryRoot);
            options.MapRoot = Path.GetFullPath(options.MapRoot);
            options.SoundRoot = Path.GetFullPath(options.SoundRoot);
            return options;
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (index >= args.Length) throw new ArgumentException($"Missing value for {option}.");
            return args[index++];
        }
    }

    private sealed class V3BuildState
    {
        public int FormatVersion { get; set; } = StreamingAssetV3Constants.FormatVersion;
        public List<V3SourceState> Sources { get; set; } = new();
        public V3SourceState Find(string kind, string id) => Sources.FirstOrDefault(item =>
            string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class V3SourceState
    {
        public string Kind { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string SourceHash { get; set; } = string.Empty;
        [JsonIgnore] public bool Changed { get; set; }
    }

    private sealed class V3Progress
    {
        private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromMilliseconds(200);

        private string _stage;
        private int _total;
        private int _complete;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private TimeSpan _lastRefresh;
        public int RebuiltCount { get; private set; }
        public int ReusedCount { get; private set; }
        public int FailedCount { get; private set; }
        private readonly List<string> _failures = new();

        public void StartStage(string stage, IReadOnlyCollection<string> files)
        {
            FinishStage();
            _stage = stage;
            _total = files.Count;
            _complete = 0;
            _timer.Restart();
            _lastRefresh = TimeSpan.Zero;
            Console.WriteLine($"{stage}: {_total} files, {FormatBytes(files.Sum(path => new FileInfo(path).Length))}");
        }

        public void ReportSubItem(string file, int fileIndex, int current, int total)
        {
            if (current < total && _timer.Elapsed - _lastRefresh < MinimumRefreshInterval) return;
            double fraction = total == 0 ? 1 : (double)current / total;
            Write(file, fileIndex, fraction, current, total, current >= total);
        }

        public void Complete(string file, bool rebuilt)
        {
            _complete++;
            if (rebuilt) RebuiltCount++; else ReusedCount++;
            Write(file, _complete - 1, 1, 0, 0, true);
        }

        public void Fail(string file, Exception ex)
        {
            _complete++;
            FailedCount++;
            _failures.Add($"Failed: {file}{Environment.NewLine}{ex}");
            Console.WriteLine();
            Console.Error.WriteLine($"Failed: {file}");
            Console.Error.WriteLine(ex.Message);
        }

        public void WriteFailureReport(string path)
        {
            try
            {
                string temp = path + $".{Environment.ProcessId}.tmp";
                File.WriteAllText(temp,
                    $"AssetBuilder V3 failure report{Environment.NewLine}" +
                    $"Created UTC: {DateTime.UtcNow:O}{Environment.NewLine}" +
                    $"Failed resources: {FailedCount}{Environment.NewLine}{Environment.NewLine}" +
                    string.Join(Environment.NewLine + Environment.NewLine, _failures));
                File.Move(temp, path, true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not write failure report '{path}': {ex.Message}");
            }
        }

        public void FinishStage()
        {
            if (!string.IsNullOrEmpty(_stage)) Console.WriteLine();
            _stage = null;
        }

        private void Write(string file, int fileIndex, double fraction, int subCurrent, int subTotal, bool force)
        {
            if (!force && _timer.Elapsed - _lastRefresh < MinimumRefreshInterval) return;
            _lastRefresh = _timer.Elapsed;
            double overall = _total == 0 ? 1 : (fileIndex + fraction) / _total;
            TimeSpan? eta = overall > 0.001 ? TimeSpan.FromTicks((long)(_timer.Elapsed.Ticks * (1 - overall) / overall)) : null;
            string sub = subTotal > 0 ? $" {subCurrent}/{subTotal}" : string.Empty;
            string line = $"[{_stage}] {overall,7:P1} ({Math.Min(fileIndex + 1, _total)}/{_total}){sub} " +
                          $"rebuilt {RebuiltCount}, reused {ReusedCount}, elapsed {_timer.Elapsed:hh\\:mm\\:ss}, " +
                          $"ETA {(eta.HasValue ? eta.Value.ToString("hh\\:mm\\:ss") : "--:--:--")} | {Path.GetFileName(file)}";
            Console.Write($"\r{line.PadRight(220)}");
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return $"{value:0.##} {units[unit]}";
        }
    }
}
