using Client.MirObjects;
using Client.MirScenes;
using Shared.StreamingAssets;

namespace Client.Streaming
{
    /// <summary>
    /// Applies a streamed map to a <see cref="MapControl"/>. The whole map pack arrives in one request and
    /// stays resident in compressed form; individual 32x32 chunks are inflated on the game thread, nearest
    /// to the player first, with a per-frame budget so entering a map never stalls a frame.
    /// </summary>
    public sealed class StreamingMapState
    {
        private const int MaxChunksPerCall = 96;

        private readonly string _mapId;
        private readonly V3MapRecord _record;
        private StreamingMapPack _pack;
        private bool[] _applied;
        private int _appliedCount;
        private int _lastUserChunk = -1;
        private bool _lastCallSatisfied;

        private StreamingMapState(string mapId, V3MapRecord record, StreamingMapPack pack)
        {
            _mapId = mapId;
            _record = record;
            SetPack(pack);
        }

        public int Width => _pack?.Width ?? _record.Width;
        public int Height => _pack?.Height ?? _record.Height;
        public bool IsReady => _pack != null;
        public bool IsComplete => _pack != null && _appliedCount >= _pack.ChunkCount;

        public static bool TryCreate(string localMapFile, out StreamingMapState state)
        {
            state = null;
            if (!AssetManager.Enabled) return false;

            string mapId = AssetManager.ToMapId(localMapFile);
            if (!AssetManager.TryGetLoadedMapRecord(mapId, out V3MapRecord record)) return false;
            AssetManager.TryGetCachedMapPack(mapId, out StreamingMapPack pack);

            state = new StreamingMapState(mapId, record, pack);
            if (pack == null) AssetManager.QueueMapPack(mapId);
            return true;
        }

        public CellInfo[,] CreatePlaceholderCells()
        {
            CellInfo[,] cells = new CellInfo[Width, Height];
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    cells[x, y] = new CellInfo();
                }
            }

            return cells;
        }

        public bool IsLoaded(Point point)
        {
            if (_pack == null || point.X < 0 || point.Y < 0 || point.X >= Width || point.Y >= Height) return false;
            int index = _pack.GetChunkIndexForCell(point.X, point.Y);
            return index >= 0 && _applied[index];
        }

        public void EnsureVisibleChunks(MapControl map)
        {
            if (MapControl.User == null || map.M2CellInfo == null) return;
            if (_pack == null && !TryPromotePack())
            {
                AssetManager.QueueMapPack(_mapId);
                return;
            }
            if (IsComplete) return;

            Point user = MapControl.User.Movement;
            int chunkSize = _pack.ChunkSize;
            int userChunk = _pack.GetChunkIndexForCell(Math.Clamp(user.X, 0, Width - 1), Math.Clamp(user.Y, 0, Height - 1));
            if (_lastCallSatisfied && userChunk == _lastUserChunk) return;
            _lastUserChunk = userChunk;

            int startX = Math.Max(0, user.X - MapControl.ViewRangeX - chunkSize);
            int endX = Math.Min(Width - 1, user.X + MapControl.ViewRangeX + chunkSize);
            int startY = Math.Max(0, user.Y - MapControl.ViewRangeY - chunkSize);
            int endY = Math.Min(Height - 1, user.Y + MapControl.ViewRangeY + 25 + chunkSize);

            List<int> pending = new();
            for (int column = startX / chunkSize; column <= endX / chunkSize; column++)
            {
                for (int row = startY / chunkSize; row <= endY / chunkSize; row++)
                {
                    int index = _pack.GetChunkIndex(column, row);
                    if (index < 0 || _applied[index]) continue;
                    pending.Add(index);
                }
            }
            if (pending.Count == 0)
            {
                _lastCallSatisfied = true;
                return;
            }

            pending.Sort((left, right) => DistanceSquared(left, user).CompareTo(DistanceSquared(right, user)));

            bool changed = false;
            int budget = Math.Min(MaxChunksPerCall, pending.Count);
            _lastCallSatisfied = budget == pending.Count;
            for (int i = 0; i < budget; i++)
            {
                int index = pending[i];
                try
                {
                    ApplyChunk(map, _pack.ReadChunk(index));
                }
                catch (Exception ex)
                {
                    CMain.SaveError($"Streaming map chunk {index} of '{_mapId}' failed: {ex.Message}");
                }
                _applied[index] = true;
                _appliedCount++;
                changed = true;
            }

            if (changed)
            {
                map.FloorValid = false;
                map.LightsValid = false;
                map.Redraw();
            }
        }

        private bool TryPromotePack()
        {
            if (_pack != null) return true;
            if (!AssetManager.TryGetCachedMapPack(_mapId, out StreamingMapPack pack)) return false;
            SetPack(pack);
            return true;
        }

        private void SetPack(StreamingMapPack pack)
        {
            _pack = pack;
            _applied = pack == null ? Array.Empty<bool>() : new bool[pack.ChunkCount];
            _appliedCount = 0;
            _lastUserChunk = -1;
            _lastCallSatisfied = false;
        }

        private int DistanceSquared(int chunkIndex, Point user)
        {
            int chunkX = chunkIndex / _pack.Rows * _pack.ChunkSize;
            int chunkY = chunkIndex % _pack.Rows * _pack.ChunkSize;
            int dx = chunkX + _pack.ChunkSize / 2 - user.X;
            int dy = chunkY + _pack.ChunkSize / 2 - user.Y;
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// Copies a chunk into the existing cells. <see cref="CellInfo"/> is a class, so mutating in place
        /// both avoids an allocation per cell and keeps any <c>CellObjects</c> already standing there.
        /// </summary>
        private static void ApplyChunk(MapControl map, StreamingMapChunk chunk)
        {
            for (int x = 0; x < chunk.Width; x++)
            {
                for (int y = 0; y < chunk.Height; y++)
                {
                    int mapX = chunk.X + x;
                    int mapY = chunk.Y + y;
                    if (mapX < 0 || mapY < 0 || mapX >= map.Width || mapY >= map.Height) continue;

                    CellInfo cell = map.M2CellInfo[mapX, mapY];
                    if (cell == null) map.M2CellInfo[mapX, mapY] = cell = new CellInfo();
                    Copy(chunk.Cells[x * chunk.Height + y], cell);
                }
            }
        }

        private static void Copy(StreamingMapCell source, CellInfo target)
        {
            target.BackIndex = source.BackIndex;
            target.BackImage = source.BackImage;
            target.MiddleIndex = source.MiddleIndex;
            target.MiddleImage = source.MiddleImage;
            target.FrontIndex = source.FrontIndex;
            target.FrontImage = source.FrontImage;
            target.DoorIndex = source.DoorIndex;
            target.DoorOffset = source.DoorOffset;
            target.FrontAnimationFrame = source.FrontAnimationFrame;
            target.FrontAnimationTick = source.FrontAnimationTick;
            target.MiddleAnimationFrame = source.MiddleAnimationFrame;
            target.MiddleAnimationTick = source.MiddleAnimationTick;
            target.TileAnimationImage = source.TileAnimationImage;
            target.TileAnimationOffset = source.TileAnimationOffset;
            target.TileAnimationFrames = source.TileAnimationFrames;
            target.Light = source.Light;
            target.Unknown = source.Unknown;
            target.FishingCell = source.FishingCell;
        }
    }
}
