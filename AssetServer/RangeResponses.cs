using System.Buffers;
using Microsoft.Net.Http.Headers;

namespace AssetServer;

/// <summary>
/// Byte-range and multi-segment responses served straight out of the pooled handles, without buffering the
/// payload on the managed heap. A <c>?segments=</c> batch is capped at 4 MB, which is large enough to hit
/// the large object heap on every request if it were materialised as one array.
/// </summary>
internal static class RangeResponses
{
    private const int CopyBufferSize = 64 * 1024;

    /// <summary>Writes one satisfiable range as a 206, or the correct 416 when the client asked for nonsense.</summary>
    public static async Task SendRangeAsync(HttpContext context, PublishedFile file, string contentType)
    {
        if (!TryParseSingleRange(context.Request.Headers.Range.ToString(), file.Length,
                out long offset, out long count))
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers.ContentRange = $"bytes */{file.Length}";
            return;
        }

        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = count;
        context.Response.Headers.ContentRange = $"bytes {offset}-{offset + count - 1}/{file.Length}";
        await CopyAsync(context, file, offset, count).ConfigureAwait(false);
    }

    /// <summary>
    /// One request for many scattered image records: the body is the request-order sequence of
    /// <c>[int64 offset][int32 length][bytes]</c>.
    /// </summary>
    public static async Task SendSegmentsAsync(HttpContext context, PublishedFile file,
        List<(long Offset, int Length)> wanted)
    {
        long total = wanted.Count * 12L;
        foreach ((long _, int length) in wanted) total += length;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = total;

        byte[] header = new byte[12];
        foreach ((long offset, int length) in wanted)
        {
            BitConverter.TryWriteBytes(header.AsSpan(0), offset);
            BitConverter.TryWriteBytes(header.AsSpan(8), length);
            await context.Response.Body.WriteAsync(header, context.RequestAborted).ConfigureAwait(false);
            await CopyAsync(context, file, offset, length).ConfigureAwait(false);
        }
    }

    private static async Task CopyAsync(HttpContext context, PublishedFile file, long offset, long count)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(CopyBufferSize, count));
        try
        {
            long remaining = count;
            while (remaining > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int read = await RandomAccess.ReadAsync(file.Handle, buffer.AsMemory(0, wanted),
                    offset + count - remaining, context.RequestAborted).ConfigureAwait(false);
                if (read <= 0) throw new EndOfStreamException($"Published file '{file.Path}' ended early.");
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted)
                    .ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Accepts exactly one range spec, which is all the client ever sends: <c>bytes=a-b</c>, <c>bytes=a-</c>
    /// or the suffix form <c>bytes=-n</c>. A multi-range request is rejected rather than answered with a
    /// multipart body.
    /// </summary>
    public static bool TryParseSingleRange(string value, long length, out long offset, out long count)
    {
        offset = 0;
        count = 0;
        if (length <= 0 || string.IsNullOrWhiteSpace(value)) return false;
        if (!RangeHeaderValue.TryParse(value, out RangeHeaderValue header) ||
            !header.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) ||
            header.Ranges.Count != 1) return false;

        RangeItemHeaderValue range = header.Ranges.First();
        if (range.From.HasValue)
        {
            if (range.From.Value >= length) return false;
            offset = range.From.Value;
            long last = range.To.HasValue ? Math.Min(range.To.Value, length - 1) : length - 1;
            if (last < offset) return false;
            count = last - offset + 1;
            return true;
        }

        if (!range.To.HasValue || range.To.Value <= 0) return false;
        count = Math.Min(range.To.Value, length);
        offset = length - count;
        return true;
    }
}
