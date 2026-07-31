using Shared.StreamingAssets;

namespace AssetBuilder;

internal sealed class MapData
{
    private const long MaximumCellCount = 25_000_000;

    public int Width;
    public int Height;
    public StreamingMapCell[,] Cells;

    private byte[] _bytes;
    private string _file;
    private byte _type;

    public static MapData Read(string file)
    {
        MapData map = new()
        {
            _file = Path.GetFullPath(file),
            _bytes = File.ReadAllBytes(file)
        };

        map.Load();
        return map;
    }

    private void Load()
    {
        _type = FindType(_bytes);
        try
        {
            switch (_type)
            {
                case 1: LoadType1(); break;
                case 2: LoadType2(); break;
                case 3: LoadType3(); break;
                case 4: LoadType4(); break;
                case 5: LoadType5(); break;
                case 6: LoadType6(); break;
                case 7: LoadType7(); break;
                case 100: LoadType100(); break;
                default: LoadType0(); break;
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw Invalid(_bytes.LongLength, "map data", ex);
        }
    }

    private static byte FindType(byte[] input)
    {
        if (input == null || input.Length < 4)
            throw new InvalidDataException("Map header is shorter than 4 bytes.");

        if (At(input, 2, 0x43) && At(input, 3, 0x23)) return 100;
        if (At(input, 0, 0)) return 5;
        if (At(input, 0, 0x0F) && At(input, 5, 0x53) && At(input, 14, 0x33)) return 6;
        if (At(input, 0, 0x15) && At(input, 4, 0x32) && At(input, 6, 0x41) && At(input, 19, 0x31)) return 4;
        if (At(input, 0, 0x10) && At(input, 2, 0x61) && At(input, 7, 0x31) && At(input, 14, 0x31)) return 1;

        if (At(input, 4, 0x0F) || At(input, 4, 0x03) && At(input, 18, 0x0D) && At(input, 19, 0x0A))
        {
            int width = input[0] + (input[1] << 8);
            int height = input[2] + (input[3] << 8);
            long type2Length = 52L + (long)width * height * 14;
            return input.LongLength > type2Length ? (byte)3 : (byte)2;
        }

        if (At(input, 0, 0x0D) && At(input, 1, 0x4C) && At(input, 7, 0x20) && At(input, 11, 0x6D)) return 7;
        return 0;
    }

    private static bool At(byte[] input, int index, byte value) => index < input.Length && input[index] == value;

    private void InitCells(int width, int height)
    {
        long count = (long)width * height;
        if (width <= 0 || height <= 0 || count > MaximumCellCount)
            throw Invalid(0, $"invalid dimensions {width}x{height}");

        Width = width;
        Height = height;
        Cells = new StreamingMapCell[width, height];
    }

    private void RequireLength(long required, long offset, string section)
    {
        if (required < 0 || _bytes.LongLength < required)
            throw Invalid(offset, $"truncated {section}; requires {required} bytes but has {_bytes.LongLength}");
    }

    private InvalidDataException Invalid(long offset, string detail, Exception inner = null) =>
        new($"Invalid map '{_file}' (type {_type}) at offset {offset}: {detail}.", inner);

    private void SetFishing(int x, int y)
    {
        if (Cells[x, y].Light is >= 100 and <= 119)
            Cells[x, y].FishingCell = true;
    }

    private void LoadType0()
    {
        RequireLength(52, 0, "type 0 header");
        int offset = 0;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 52;
        RequireLength(offset + (long)Width * Height * 12, offset, "type 0 cells");

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = 0;
            Cells[x, y].MiddleIndex = 1;
            Cells[x, y].BackImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 2);
            Cells[x, y].Light = _bytes[offset++];

            if ((Cells[x, y].BackImage & 0x8000) != 0)
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            SetFishing(x, y);
        }
    }

    private void LoadType1()
    {
        RequireLength(54, 21, "type 1 header");
        int offset = 21;
        int width = BitConverter.ToInt16(_bytes, offset); offset += 2;
        int xor = BitConverter.ToInt16(_bytes, offset); offset += 2;
        int height = BitConverter.ToInt16(_bytes, offset);
        InitCells(width ^ xor, height ^ xor);
        offset = 54;
        RequireLength(offset + (long)Width * Height * 15, offset, "type 1 cells");

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = 0;
            Cells[x, y].BackImage = BitConverter.ToInt32(_bytes, offset) ^ unchecked((int)0xAA38AA38); offset += 4;
            Cells[x, y].MiddleIndex = 1;
            Cells[x, y].MiddleImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor); offset += 2;
            Cells[x, y].FrontImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor); offset += 2;
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
        RequireLength(52, 0, "type 2 header");
        int offset = 0;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 52;
        RequireLength(offset + (long)Width * Height * 14, offset, "type 2 cells");

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 120);
            Cells[x, y].Light = _bytes[offset++];
            Cells[x, y].BackIndex = (short)(_bytes[offset++] + 100);
            Cells[x, y].MiddleIndex = (short)(_bytes[offset++] + 110);

            if ((Cells[x, y].BackImage & 0x8000) != 0)
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            SetFishing(x, y);
        }
    }

    private void LoadType3()
    {
        RequireLength(52, 0, "type 3 header");
        int offset = 0;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 52;
        RequireLength(offset + (long)Width * Height * 36, offset, "type 3 cells");

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 120);
            Cells[x, y].Light = _bytes[offset++];
            Cells[x, y].BackIndex = (short)(_bytes[offset++] + 100);
            Cells[x, y].MiddleIndex = (short)(_bytes[offset++] + 110);
            Cells[x, y].TileAnimationImage = BitConverter.ToInt16(_bytes, offset); offset += 7;
            Cells[x, y].TileAnimationFrames = _bytes[offset++];
            Cells[x, y].TileAnimationOffset = BitConverter.ToInt16(_bytes, offset); offset += 14;

            if ((Cells[x, y].BackImage & 0x8000) != 0)
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            SetFishing(x, y);
        }
    }

    private void LoadType4()
    {
        RequireLength(64, 31, "type 4 header");
        int offset = 31;
        int width = BitConverter.ToInt16(_bytes, offset); offset += 2;
        int xor = BitConverter.ToInt16(_bytes, offset); offset += 2;
        int height = BitConverter.ToInt16(_bytes, offset);
        InitCells(width ^ xor, height ^ xor);
        offset = 64;
        RequireLength(offset + (long)Width * Height * 12, offset, "type 4 cells");

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = 0;
            Cells[x, y].MiddleIndex = 1;
            Cells[x, y].BackImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor); offset += 2;
            Cells[x, y].MiddleImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor); offset += 2;
            Cells[x, y].FrontImage = (short)(BitConverter.ToInt16(_bytes, offset) ^ xor); offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 2);
            Cells[x, y].Light = _bytes[offset++];

            if ((Cells[x, y].BackImage & 0x8000) != 0)
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            SetFishing(x, y);
        }
    }

    private void LoadType5()
    {
        RequireLength(28, 22, "type 5 header");
        int offset = 22;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 28;

        long tileBytes = 3L * ((Width / 2) + (Width % 2)) * (Height / 2);
        long cellOffset = offset + tileBytes;
        RequireLength(cellOffset + (long)Width * Height * 14, offset, "type 5 cells");

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

        offset = checked((int)cellOffset);
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
            Cells[x, y].FrontIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 200 : -1); offset++;
            Cells[x, y].MiddleIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 200 : -1); offset++;
            Cells[x, y].MiddleImage = BitConverter.ToUInt16(_bytes, offset) + 1; offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToUInt16(_bytes, offset) + 1;
            if (Cells[x, y].FrontImage == 1 && Cells[x, y].FrontIndex == 200) Cells[x, y].FrontIndex = -1;
            offset += 5;
            Cells[x, y].Light = (byte)(_bytes[offset] & 0x0F); offset += 2;

            if ((flag & 0x01) != 1) Cells[x, y].BackImage |= 0x20000000;
            if ((flag & 0x02) != 2) Cells[x, y].FrontImage = (ushort)(Cells[x, y].FrontImage | 0x8000);
            if (Cells[x, y].Light is >= 100 and <= 119) Cells[x, y].FishingCell = true;
            else Cells[x, y].Light *= 2;
        }
    }

    private void LoadType6()
    {
        RequireLength(40, 16, "type 6 header");
        int offset = 16;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 40;
        RequireLength(offset + (long)Width * Height * 20, offset, "type 6 cells");

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            byte flag = _bytes[offset++];
            Cells[x, y].BackIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 300 : -1); offset++;
            Cells[x, y].MiddleIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 300 : -1); offset++;
            Cells[x, y].FrontIndex = (short)(_bytes[offset] != 255 ? _bytes[offset] + 300 : -1); offset++;
            Cells[x, y].BackImage = BitConverter.ToInt16(_bytes, offset) + 1; offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset) + 1; offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset) + 1; offset += 2;
            if (Cells[x, y].FrontImage == 1 && Cells[x, y].FrontIndex == 200) Cells[x, y].FrontIndex = -1;
            Cells[x, y].MiddleAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset] == 255 ? (byte)0 : _bytes[offset];
            if (Cells[x, y].FrontAnimationFrame > 0x0F)
                Cells[x, y].FrontAnimationFrame &= 0x0F;
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
        RequireLength(54, 21, "type 7 header");
        int offset = 21;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 4));
        offset = 54;
        RequireLength(offset + (long)Width * Height * 15, offset, "type 7 cells");

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = 0;
            Cells[x, y].BackImage = BitConverter.ToInt32(_bytes, offset); offset += 4;
            Cells[x, y].MiddleIndex = 1;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].FrontIndex = (short)(_bytes[offset++] + 2);
            Cells[x, y].Light = _bytes[offset++];
            Cells[x, y].Unknown = _bytes[offset++];

            if ((Cells[x, y].BackImage & 0x8000) != 0)
                Cells[x, y].BackImage = (Cells[x, y].BackImage & 0x7FFF) | 0x20000000;
            SetFishing(x, y);
        }
    }

    private void LoadType100()
    {
        RequireLength(8, 0, "type 100 header");
        if (_bytes[0] != 1 || _bytes[1] != 0)
            throw Invalid(0, "invalid type 100 signature");

        int offset = 4;
        InitCells(BitConverter.ToInt16(_bytes, offset), BitConverter.ToInt16(_bytes, offset + 2));
        offset = 8;
        RequireLength(offset + (long)Width * Height * 26, offset, "type 100 cells");

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height; y++)
        {
            Cells[x, y].BackIndex = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].BackImage = BitConverter.ToInt32(_bytes, offset); offset += 4;
            Cells[x, y].MiddleIndex = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].MiddleImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].FrontIndex = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].FrontImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].DoorIndex = (byte)(_bytes[offset++] & 0x7F);
            Cells[x, y].DoorOffset = _bytes[offset++];
            Cells[x, y].FrontAnimationFrame = _bytes[offset++];
            Cells[x, y].FrontAnimationTick = _bytes[offset++];
            Cells[x, y].MiddleAnimationFrame = _bytes[offset++];
            Cells[x, y].MiddleAnimationTick = _bytes[offset++];
            Cells[x, y].TileAnimationImage = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].TileAnimationOffset = BitConverter.ToInt16(_bytes, offset); offset += 2;
            Cells[x, y].TileAnimationFrames = _bytes[offset++];
            Cells[x, y].Light = _bytes[offset++];
            SetFishing(x, y);
        }
    }
}
