using Shared.StreamingAssets;

namespace Client.Streaming
{
    /// <summary>
    /// Client view of one library's catalog block. The header (segment directory plus frame table) is
    /// fetched once; the segments of packed image records are fetched only when something actually draws
    /// from them. The render thread hits this class every frame, so every lookup is an array index plus a
    /// null check - no I/O, no locking and no hashing.
    /// </summary>
    public sealed class StreamingLibraryIndex
    {
        private readonly V3LibraryImageRecord[][] _segments;
        private readonly int[] _requestEpochs;

        public StreamingLibraryIndex(V3LibraryRecord library, V3LibraryIndexHeader header)
        {
            Library = library;
            Header = header;
            _segments = new V3LibraryImageRecord[header.SegmentCount][];
            _requestEpochs = new int[header.SegmentCount];
            for (int i = 0; i < _requestEpochs.Length; i++) _requestEpochs[i] = int.MinValue;
        }

        public V3LibraryRecord Library { get; }
        public V3LibraryIndexHeader Header { get; }
        public string Id => Library.Id;
        public int ImageCount => Header.ImageCount;
        public int SegmentCount => Header.SegmentCount;
        public List<LibraryFrameRecord> Frames => Header.Frames;

        public bool IsSegmentLoaded(int segment) =>
            segment >= 0 && segment < _segments.Length && Volatile.Read(ref _segments[segment]) != null;

        public bool AreAllSegmentsLoaded()
        {
            for (int i = 0; i < _segments.Length; i++)
                if (Volatile.Read(ref _segments[i]) == null) return false;
            return true;
        }

        /// <summary>Publishes a decoded segment. Only called from background threads.</summary>
        public void SetSegment(int segment, V3LibraryImageRecord[] records)
        {
            if (segment < 0 || segment >= _segments.Length || records == null) return;
            Volatile.Write(ref _segments[segment], records);
        }

        public int GetSegmentIndex(int imageIndex) => Header.GetSegmentIndex(imageIndex);

        /// <summary>
        /// Resolves one image's metadata. Returns false when the owning segment has not arrived yet, in
        /// which case <paramref name="segment"/> tells the caller which segment to request.
        /// </summary>
        public bool TryGetImage(int imageIndex, out V3LibraryImageRecord image, out int segment)
        {
            image = null;
            segment = -1;
            if (imageIndex < 0 || imageIndex >= Header.ImageCount) return false;
            segment = imageIndex / Header.SegmentSize;
            if (segment >= _segments.Length) return false;
            V3LibraryImageRecord[] records = Volatile.Read(ref _segments[segment]);
            if (records == null) return false;
            int offset = imageIndex - segment * Header.SegmentSize;
            if (offset >= records.Length) return false;
            image = records[offset];
            return true;
        }

        /// <summary>
        /// Limits segment requests coming from the render thread to one attempt per retry epoch, so a
        /// missing or failing segment cannot spawn a task per frame.
        /// </summary>
        public bool TryBeginSegmentRequest(int segment, int epoch)
        {
            if (segment < 0 || segment >= _requestEpochs.Length) return false;
            return Interlocked.Exchange(ref _requestEpochs[segment], epoch) != epoch;
        }
    }
}
