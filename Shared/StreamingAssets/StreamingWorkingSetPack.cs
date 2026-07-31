using System.Text;

namespace Shared.StreamingAssets;

/// <summary>
/// First-run working set: the exact image records a cold client touches on the way from the login screen
/// into the first map, published as one file. Prefetching whole startup libraries was measured at 88.96 MB
/// and rejected; the recorded set is a small fraction of that, and one request replaces the hundreds of
/// ranged requests the first minutes used to issue.
/// </summary>
public static class StreamingWorkingSetPack
{
    private const int MaxLibraries = 4096;
    private const int MaxEntriesPerLibrary = 1 << 20;
    private const long MaxPayloadLength = 512L * 1024 * 1024;

    public static byte[] Write(IReadOnlyList<WorkingSetLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        if (libraries.Count > MaxLibraries) throw new InvalidDataException("Too many libraries in the working set.");

        using MemoryStream output = new();
        using (BinaryWriter writer = new(output, Encoding.UTF8, true))
        {
            writer.Write(StreamingAssetV3Constants.WorkingSetMagic);
            writer.Write(StreamingAssetV3Constants.FormatVersion);
            writer.Write(libraries.Count);

            long payloadLength = 0;
            foreach (WorkingSetLibrary library in libraries)
            {
                if (string.IsNullOrWhiteSpace(library.Id) || !StreamingAssetIO.IsValidSha256(library.FileHash))
                    throw new InvalidDataException("A working set library record is incomplete.");
                if (library.Entries.Count == 0 || library.Entries.Count > MaxEntriesPerLibrary)
                    throw new InvalidDataException($"Working set library '{library.Id}' has no usable entries.");

                writer.Write(library.Id);
                writer.Write(library.FileHash.ToLowerInvariant());
                writer.Write(library.Entries.Count);
                foreach (WorkingSetEntry entry in library.Entries)
                {
                    if (entry.Index < 0 || entry.Offset < 0 || entry.Payload == null ||
                        entry.Payload.Length == 0 || entry.Payload.Length != entry.Length)
                        throw new InvalidDataException($"Working set entry {entry.Index} of '{library.Id}' is invalid.");
                    writer.Write(entry.Index);
                    writer.Write(entry.Offset);
                    writer.Write(entry.Length);
                    payloadLength += entry.Length;
                }
            }

            if (payloadLength > MaxPayloadLength) throw new InvalidDataException("The working set payload is too large.");
            writer.Write(payloadLength);
        }

        foreach (WorkingSetLibrary library in libraries)
            foreach (WorkingSetEntry entry in library.Entries)
                output.Write(entry.Payload, 0, entry.Payload.Length);

        return output.ToArray();
    }

    /// <summary>
    /// Parses the directory without copying payloads: each entry carries the offset of its bytes inside
    /// <paramref name="data"/>, so the caller writes straight from the downloaded buffer into the container.
    /// </summary>
    public static StreamingWorkingSet Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using MemoryStream input = new(data, false);
        using BinaryReader reader = new(input, Encoding.UTF8, true);

        Require(reader.ReadInt32() == StreamingAssetV3Constants.WorkingSetMagic, "Invalid working set magic.");
        Require(reader.ReadInt32() == StreamingAssetV3Constants.FormatVersion, "Unsupported working set version.");
        int libraryCount = reader.ReadInt32();
        Require(libraryCount >= 0 && libraryCount <= MaxLibraries, "Invalid working set library count.");

        StreamingWorkingSet result = new();
        List<(WorkingSetLibraryView Library, int Index, long Offset, int Length)> flat = new();
        long payload = 0;
        for (int i = 0; i < libraryCount; i++)
        {
            string id = reader.ReadString();
            string hash = reader.ReadString();
            Require(!string.IsNullOrWhiteSpace(id) && StreamingAssetIO.IsValidSha256(hash),
                "Invalid working set library header.");
            int entryCount = reader.ReadInt32();
            Require(entryCount > 0 && entryCount <= MaxEntriesPerLibrary, "Invalid working set entry count.");

            WorkingSetLibraryView library = new() { Id = id, FileHash = hash.ToLowerInvariant() };
            result.Libraries.Add(library);
            for (int j = 0; j < entryCount; j++)
            {
                int index = reader.ReadInt32();
                long offset = reader.ReadInt64();
                int length = reader.ReadInt32();
                Require(index >= 0 && offset >= 0 && length > 0, "Invalid working set entry.");
                payload += length;
                Require(payload <= MaxPayloadLength, "The working set payload is too large.");
                flat.Add((library, index, offset, length));
            }
        }

        long declared = reader.ReadInt64();
        Require(declared == payload, "The working set payload length does not match its directory.");
        long start = input.Position;
        Require(start + payload == data.Length, "The working set payload is truncated or has trailing data.");

        long cursor = start;
        foreach ((WorkingSetLibraryView library, int index, long offset, int length) in flat)
        {
            library.Entries.Add(new WorkingSetPayload(index, offset, length, (int)cursor));
            cursor += length;
        }
        return result;
    }

    /// <summary>
    /// Reads a recorded usage file: one <c>libraryId</c> plus image index per line, separated by a tab,
    /// comma or space. Blank lines and <c>#</c> comments are ignored so the file can be hand edited.
    /// </summary>
    public static Dictionary<string, SortedSet<int>> ReadUsage(IEnumerable<string> lines)
    {
        Dictionary<string, SortedSet<int>> usage = new(StringComparer.OrdinalIgnoreCase);
        if (lines == null) return usage;
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string trimmed = line.Trim();
            if (trimmed.StartsWith('#')) continue;
            int split = trimmed.LastIndexOfAny(new[] { '\t', ',', ' ' });
            if (split <= 0 || split >= trimmed.Length - 1) continue;
            string id = trimmed[..split].Trim();
            if (id.Length == 0 || !int.TryParse(trimmed.AsSpan(split + 1).Trim(), out int index) || index < 0) continue;
            if (!usage.TryGetValue(id, out SortedSet<int> indexes)) usage[id] = indexes = new SortedSet<int>();
            indexes.Add(index);
        }
        return usage;
    }

    public static string FormatUsageLine(string libraryId, int index) => libraryId + "\t" + index.ToString();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public sealed class WorkingSetLibrary
{
    public string Id { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public List<WorkingSetEntry> Entries { get; set; } = new();
}

public readonly record struct WorkingSetEntry(int Index, long Offset, int Length, byte[] Payload);

public sealed class StreamingWorkingSet
{
    public List<WorkingSetLibraryView> Libraries { get; } = new();

    public int ImageCount => Libraries.Sum(library => library.Entries.Count);
}

public sealed class WorkingSetLibraryView
{
    public string Id { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public List<WorkingSetPayload> Entries { get; } = new();
}

/// <summary>One image record inside a downloaded working set pack, addressed in the download buffer.</summary>
public readonly record struct WorkingSetPayload(int Index, long Offset, int Length, int PayloadOffset);
