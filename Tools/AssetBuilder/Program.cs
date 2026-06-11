using Shared.StreamingAssets;
using System.Security.Cryptography;

namespace AssetBuilder;

internal static class Program
{
    private static int Main(string[] args)
    {
        string source = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("Build", "Client", "Debug"));
        string output = Path.GetFullPath(args.Length > 1 ? args[1] : "StreamingAssets");
        string version = args.Length > 2 ? args[2] : DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine($"Source client directory not found: {source}");
            return 1;
        }

        Directory.CreateDirectory(output);

        AssetManifest manifest = new()
        {
            Version = version,
            CreatedUtc = DateTime.UtcNow,
            MapChunkSize = StreamingAssetConstants.DefaultMapChunkSize
        };

        BuildLibraries(source, output, manifest);
        BuildMaps(source, output, manifest);
        BuildSounds(source, output, manifest);

        StreamingAssetIO.WriteJson(Path.Combine(output, StreamingAssetConstants.ManifestFileName), manifest);

        Console.WriteLine($"Streaming assets built at {output}");
        Console.WriteLine($"Libraries: {manifest.Libraries.Count}, Maps: {manifest.Maps.Count}, Sounds: {manifest.Sounds.Count}");
        return 0;
    }

    private static void BuildLibraries(string source, string output, AssetManifest manifest)
    {
        string dataPath = ResolveLibraryDataPath(source);
        if (dataPath == null)
        {
            Console.Error.WriteLine($"No Data or Data_Full library directory found under: {source}");
            return;
        }

        Console.WriteLine($"Library source: {dataPath}");

        foreach (string file in Directory.GetFiles(dataPath, "*.Lib", SearchOption.AllDirectories))
        {
            string id = ToAssetId(Path.GetRelativePath(dataPath, Path.ChangeExtension(file, null)));
            string libraryRoot = Path.Combine(output, StreamingAssetConstants.LibrariesDirectory, id);
            string imagesRoot = Path.Combine(libraryRoot, StreamingAssetConstants.LibraryImagesDirectory);
            Directory.CreateDirectory(imagesRoot);

            LibraryManifest libraryManifest = StreamingLibraryReader.ReadLibrary(file, id, imagesRoot);
            string manifestPath = Path.Combine(libraryRoot, StreamingAssetConstants.ManifestFileName);
            StreamingAssetIO.WriteJson(manifestPath, libraryManifest);

            using FileStream stream = File.OpenRead(file);
            manifest.Libraries.Add(new AssetLibraryRecord
            {
                Id = id,
                ManifestPath = ToWebPath(Path.GetRelativePath(output, manifestPath)),
                ImageCount = libraryManifest.ImageCount,
                Length = stream.Length,
                Hash = StreamingAssetIO.ComputeSha256(stream)
            });
        }
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

    private static void BuildMaps(string source, string output, AssetManifest manifest)
    {
        string mapPath = Path.Combine(source, "Map");
        if (!Directory.Exists(mapPath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(mapPath, "*.map", SearchOption.TopDirectoryOnly))
        {
            string id = ToAssetId(Path.GetFileNameWithoutExtension(file));
            string mapRoot = Path.Combine(output, StreamingAssetConstants.MapsDirectory, id);
            string chunkRoot = Path.Combine(mapRoot, StreamingAssetConstants.MapChunksDirectory);
            Directory.CreateDirectory(chunkRoot);

            try
            {
                MapManifest mapManifest = StreamingMapReader.ReadMap(file, id, chunkRoot, StreamingAssetConstants.DefaultMapChunkSize);
                string manifestPath = Path.Combine(mapRoot, StreamingAssetConstants.ManifestFileName);
                StreamingAssetIO.WriteJson(manifestPath, mapManifest);

                using FileStream stream = File.OpenRead(file);
                manifest.Maps.Add(new AssetMapRecord
                {
                    Id = id,
                    ManifestPath = ToWebPath(Path.GetRelativePath(output, manifestPath)),
                    Width = mapManifest.Width,
                    Height = mapManifest.Height,
                    Length = stream.Length,
                    Hash = StreamingAssetIO.ComputeSha256(stream)
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to build map asset: {file}");
                Console.Error.WriteLine(ex.Message);
            }
        }
    }

    private static void BuildSounds(string source, string output, AssetManifest manifest)
    {
        string soundPath = Path.Combine(source, "Sound");
        if (!Directory.Exists(soundPath))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(soundPath, "*.*", SearchOption.AllDirectories)
                     .Where(path => string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(Path.GetFileName(path), "SoundList.lst", StringComparison.OrdinalIgnoreCase)))
        {
            string relative = Path.GetRelativePath(soundPath, file);
            string id = ToAssetId(relative);
            string destination = Path.Combine(output, StreamingAssetConstants.SoundsDirectory, relative);
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
        }
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
}

internal static class StreamingLibraryReader
{
    public static LibraryManifest ReadLibrary(string file, string id, string imagesRoot)
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
