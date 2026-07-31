namespace Shared.StreamingAssets;

public sealed class LibraryFrameRecord
{
    public byte Action { get; set; }
    public int Start { get; set; }
    public int Count { get; set; }
    public int Skip { get; set; }
    public int Interval { get; set; }
    public int EffectStart { get; set; }
    public int EffectCount { get; set; }
    public int EffectSkip { get; set; }
    public int EffectInterval { get; set; }
    public bool Reverse { get; set; }
    public bool Blend { get; set; }
}

public sealed class StreamingMapChunk
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public StreamingMapCell[] Cells { get; set; } = Array.Empty<StreamingMapCell>();
}

public struct StreamingMapCell
{
    public short BackIndex;
    public int BackImage;
    public short MiddleIndex;
    public int MiddleImage;
    public short FrontIndex;
    public int FrontImage;
    public byte DoorIndex;
    public byte DoorOffset;
    public byte FrontAnimationFrame;
    public byte FrontAnimationTick;
    public byte MiddleAnimationFrame;
    public byte MiddleAnimationTick;
    public short TileAnimationImage;
    public short TileAnimationOffset;
    public byte TileAnimationFrames;
    public byte Light;
    public byte Unknown;
    public bool FishingCell;
}
