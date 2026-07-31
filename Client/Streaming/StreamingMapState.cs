using Client.MirObjects;
using Client.MirScenes;
using Shared.StreamingAssets;

namespace Client.Streaming
{
    public sealed class StreamingMapState
    {
        private readonly string _mapId;
        private readonly V3MapRecord _record;
        private V3MapIndex _index;
        private Dictionary<string, V3MapChunkRecord> _chunks;
        private readonly HashSet<string> _loadedChunks = new HashSet<string>();

        private StreamingMapState(string mapId, V3MapRecord record, V3MapIndex index)
        {
            _mapId = mapId;
            _record = record;
            _index = index;
            SetIndex(index);
        }

        public int Width => _index?.Width ?? _record.Width;
        public int Height => _index?.Height ?? _record.Height;

        public static bool TryCreate(string localMapFile, out StreamingMapState state)
        {
            state = null;
            if (!AssetManager.Enabled) return false;

            string mapId = AssetManager.ToMapId(localMapFile);
            if (!AssetManager.TryGetLoadedMapRecord(mapId, out V3MapRecord record)) return false;
            AssetManager.TryGetCachedMapIndex(mapId, out V3MapIndex index);

            state = new StreamingMapState(mapId, record, index);
            if (index == null) AssetManager.QueueMapIndex(mapId);
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
            if (point.X < 0 || point.Y < 0 || point.X >= Width || point.Y >= Height)
            {
                return false;
            }

            if (_index == null) return false;
            int chunkX = point.X / _index.ChunkSize * _index.ChunkSize;
            int chunkY = point.Y / _index.ChunkSize * _index.ChunkSize;
            return _loadedChunks.Contains($"{chunkX}_{chunkY}");
        }

        public void EnsureVisibleChunks(MapControl map)
        {
            if (MapControl.User == null || map.M2CellInfo == null) return;
            if (_index == null && !TryPromoteIndex())
            {
                AssetManager.QueueMapIndex(_mapId);
                return;
            }

            Point user = MapControl.User.Movement;
            int startX = Math.Max(0, user.X - MapControl.ViewRangeX);
            int endX = Math.Min(Width - 1, user.X + MapControl.ViewRangeX);
            int startY = Math.Max(0, user.Y - MapControl.ViewRangeY);
            int endY = Math.Min(Height - 1, user.Y + MapControl.ViewRangeY + 25);

            bool visibleChanged = ApplyCachedChunks(map, GetChunks(startX, endX, startY, endY, user), out List<V3MapChunkRecord> missingVisible);
            if (missingVisible.Count > 0)
            {
                AssetManager.QueueMapChunks(_record, missingVisible);
            }
            else
            {
                int prefetchStartX = Math.Max(0, startX - _index.ChunkSize);
                int prefetchEndX = Math.Min(Width - 1, endX + _index.ChunkSize);
                int prefetchStartY = Math.Max(0, startY - _index.ChunkSize);
                int prefetchEndY = Math.Min(Height - 1, endY + _index.ChunkSize);

                List<V3MapChunkRecord> visible = GetChunks(startX, endX, startY, endY, user);
                HashSet<string> visibleKeys = visible.Select(record => record.Key).ToHashSet();
                List<V3MapChunkRecord> prefetch = GetChunks(prefetchStartX, prefetchEndX, prefetchStartY, prefetchEndY, user)
                    .Where(record => !visibleKeys.Contains(record.Key))
                    .ToList();

                visibleChanged |= ApplyCachedChunks(map, prefetch, out List<V3MapChunkRecord> missingPrefetch);
                if (missingPrefetch.Count > 0)
                    AssetManager.QueueMapChunks(_record, missingPrefetch);
            }

            if (visibleChanged)
            {
                map.FloorValid = false;
                map.LightsValid = false;
                map.Redraw();
            }
        }

        private bool TryPromoteIndex()
        {
            if (_index != null) return true;
            if (!AssetManager.TryGetCachedMapIndex(_mapId, out V3MapIndex index)) return false;
            SetIndex(index);
            return true;
        }

        private void SetIndex(V3MapIndex index)
        {
            _index = index;
            _chunks = index?.Chunks.ToDictionary(x => x.Key, x => x) ??
                      new Dictionary<string, V3MapChunkRecord>();
        }

        private List<V3MapChunkRecord> GetChunks(int startX, int endX, int startY, int endY, Point user)
        {
            List<V3MapChunkRecord> records = new();
            for (int chunkX = startX / _index.ChunkSize * _index.ChunkSize; chunkX <= endX; chunkX += _index.ChunkSize)
            {
                for (int chunkY = startY / _index.ChunkSize * _index.ChunkSize; chunkY <= endY; chunkY += _index.ChunkSize)
                {
                    if (_chunks.TryGetValue($"{chunkX}_{chunkY}", out V3MapChunkRecord record))
                        records.Add(record);
                }
            }

            return records.OrderBy(record => DistanceSquared(record, user)).ToList();
        }

        private bool ApplyCachedChunks(MapControl map, List<V3MapChunkRecord> records, out List<V3MapChunkRecord> missing)
        {
            bool changed = false;
            missing = new List<V3MapChunkRecord>();

            foreach (V3MapChunkRecord record in records)
            {
                if (_loadedChunks.Contains(record.Key)) continue;
                if (!AssetManager.TryReadCachedMapChunk(record, out StreamingMapChunk chunk))
                {
                    missing.Add(record);
                    continue;
                }

                ApplyChunk(map, chunk);
                _loadedChunks.Add(record.Key);
                changed = true;
            }

            return changed;
        }

        private int DistanceSquared(V3MapChunkRecord record, Point user)
        {
            int centerX = record.X + record.Width / 2;
            int centerY = record.Y + record.Height / 2;
            int dx = centerX - user.X;
            int dy = centerY - user.Y;
            return dx * dx + dy * dy;
        }

        private static void ApplyChunk(MapControl map, StreamingMapChunk chunk)
        {
            for (int x = 0; x < chunk.Width; x++)
            {
                for (int y = 0; y < chunk.Height; y++)
                {
                    int mapX = chunk.X + x;
                    int mapY = chunk.Y + y;
                    if (mapX < 0 || mapY < 0 || mapX >= map.Width || mapY >= map.Height) continue;

                    List<MapObject> objects = map.M2CellInfo[mapX, mapY].CellObjects;
                    map.M2CellInfo[mapX, mapY] = ToCellInfo(chunk.Cells[x * chunk.Height + y], objects);
                }
            }
        }

        private static CellInfo ToCellInfo(StreamingMapCell source, List<MapObject> objects)
        {
            return new CellInfo
            {
                BackIndex = source.BackIndex,
                BackImage = source.BackImage,
                MiddleIndex = source.MiddleIndex,
                MiddleImage = source.MiddleImage,
                FrontIndex = source.FrontIndex,
                FrontImage = source.FrontImage,
                DoorIndex = source.DoorIndex,
                DoorOffset = source.DoorOffset,
                FrontAnimationFrame = source.FrontAnimationFrame,
                FrontAnimationTick = source.FrontAnimationTick,
                MiddleAnimationFrame = source.MiddleAnimationFrame,
                MiddleAnimationTick = source.MiddleAnimationTick,
                TileAnimationImage = source.TileAnimationImage,
                TileAnimationOffset = source.TileAnimationOffset,
                TileAnimationFrames = source.TileAnimationFrames,
                Light = source.Light,
                Unknown = source.Unknown,
                FishingCell = source.FishingCell,
                CellObjects = objects
            };
        }
    }
}
