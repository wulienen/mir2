using AssetServer;
using Microsoft.AspNetCore.ResponseCompression;
using Shared.StreamingAssets;
using System.IO.Compression;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/json" });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:8088");

WebApplication app = builder.Build();
app.UseResponseCompression();

string assetRoot = Path.GetFullPath(builder.Configuration["AssetRoot"] ?? "StreamingAssetsV3");
int cacheSeconds = Math.Max(0, builder.Configuration.GetValue("CacheSeconds", 31536000));
Directory.CreateDirectory(assetRoot);

app.MapGet("/", () => Results.Text("YangfeiCrystal AssetServer v3"));

app.MapGet("/assets/v3/manifest.json", (HttpContext context) =>
{
    string path = Path.Combine(assetRoot, StreamingAssetV3Constants.ManifestFileName);
    if (!ManifestCache.TryGet(path, out byte[] bytes, out string etag))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = "no-cache";
    if (Matches(context.Request.Headers.IfNoneMatch, etag))
        return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Bytes(bytes, "application/json; charset=utf-8");
});

app.MapGet("/assets/v3/catalogs/{fileName}", (HttpContext context, string fileName) =>
    ServeRangedAsync(context, assetRoot, StreamingAssetV3Constants.CatalogsDirectory,
        fileName, ".bin", "application/octet-stream", cacheSeconds));

app.MapGet("/assets/v3/libraries/{fileName}", (HttpContext context, string fileName, string? segments) =>
    string.IsNullOrEmpty(segments)
        ? ServeRangedAsync(context, assetRoot, StreamingAssetV3Constants.LibrariesDirectory,
            fileName, ".lib", "application/octet-stream", cacheSeconds)
        : ServeSegmentsAsync(context, assetRoot, fileName, segments, cacheSeconds));

// Whole map packs are fetched in one plain GET, so a missing Range header must not be answered with 416.
app.MapGet("/assets/v3/maps/{fileName}", (HttpContext context, string fileName) =>
    ServeWholeFile(context, assetRoot, StreamingAssetV3Constants.MapsDirectory,
        fileName, ".mappack", "application/octet-stream", cacheSeconds));

// The first-run working set replaces hundreds of ranged requests with one whole-file GET.
app.MapGet("/assets/v3/worksets/{fileName}", (HttpContext context, string fileName) =>
    ServeWholeFile(context, assetRoot, StreamingAssetV3Constants.WorkingSetsDirectory,
        fileName, ".wsp", "application/octet-stream", cacheSeconds));

app.MapGet("/assets/v3/sounds/{fileName}", (HttpContext context, string fileName) =>
{
    string extension = Path.GetExtension(fileName).ToLowerInvariant();
    if (extension is not ".wav" and not ".mp3" and not ".lst") return Results.BadRequest();
    string contentType = extension switch
    {
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        _ => "application/octet-stream"
    };
    return ServeWholeFile(context, assetRoot, StreamingAssetV3Constants.SoundsDirectory,
        fileName, extension, contentType, cacheSeconds);
});

app.MapGet("/health", () =>
{
    string manifestPath = Path.Combine(assetRoot, StreamingAssetV3Constants.ManifestFileName);
    bool valid = false;
    string version = string.Empty;
    try
    {
        StreamingAssetV3Manifest manifest = StreamingAssetIO.ReadJson<StreamingAssetV3Manifest>(manifestPath);
        valid = manifest?.FormatVersion == StreamingAssetV3Constants.FormatVersion &&
                StreamingAssetIO.IsValidSha256(manifest.CatalogHash) && manifest.CatalogLength > 0 &&
                File.Exists(Path.Combine(assetRoot,
                    StreamingAssetV3IO.GetCatalogPath(manifest.CatalogHash).Replace('/', Path.DirectorySeparatorChar)));
        version = manifest?.Version ?? string.Empty;
    }
    catch { }
    return Results.Json(new
    {
        ok = valid,
        formatVersion = StreamingAssetV3Constants.FormatVersion,
        version,
        assetRoot
    });
});

app.Run();

/// <summary>
/// Catalog and Lib are only ever read in ranges, straight out of the pooled handle. A whole-file GET is
/// refused with a 416 so a client bug cannot pull a 450 MB Lib.
/// </summary>
static async Task ServeRangedAsync(HttpContext context, string root, string directory, string fileName,
    string expectedExtension, string contentType, int cacheSeconds)
{
    if (!TryGetHash(fileName, expectedExtension, out string hash))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    PublishedFile file = PublishedFiles.TryGet(Path.Combine(root, directory, hash + expectedExtension));
    if (file == null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.Headers.ETag = Quote(hash);
    context.Response.Headers.CacheControl = $"public,max-age={cacheSeconds},immutable";
    context.Response.Headers.AcceptRanges = "bytes";

    if (!context.Request.Headers.ContainsKey("Range"))
    {
        context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        context.Response.Headers.ContentRange = $"bytes */{file.Length}";
        return;
    }

    try
    {
        await RangeResponses.SendRangeAsync(context, file, contentType);
    }
    catch (OperationCanceledException) { }
}

/// <summary>Map packs and sounds are whole objects; Kestrel's own file sender is the fastest path for them.</summary>
static IResult ServeWholeFile(HttpContext context, string root, string directory, string fileName,
    string expectedExtension, string contentType, int cacheSeconds)
{
    if (!TryGetHash(fileName, expectedExtension, out string hash)) return Results.BadRequest();
    string path = Path.Combine(root, directory, hash + expectedExtension);
    if (!File.Exists(path)) return Results.NotFound();

    string etag = Quote(hash);
    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = $"public,max-age={cacheSeconds},immutable";
    context.Response.Headers.AcceptRanges = "bytes";
    if (!context.Request.Headers.ContainsKey("Range") && Matches(context.Request.Headers.IfNoneMatch, etag))
        return Results.StatusCode(StatusCodes.Status304NotModified);

    return Results.File(path, contentType, enableRangeProcessing: true);
}

/// <summary>
/// Multi-segment read of one published Lib: <c>?segments=offset-length,offset-length,…</c>. The body is a
/// sequence of [int64 offset][int32 length][bytes] records in request order, so a batch of scattered image
/// records costs one round trip instead of one request per record.
/// </summary>
static async Task ServeSegmentsAsync(HttpContext context, string root, string fileName, string segments,
    int cacheSeconds)
{
    const int MaxSegments = 64;
    const int MaxPayloadBytes = 4 * 1024 * 1024;

    if (!TryGetHash(fileName, ".lib", out string hash))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    PublishedFile file = PublishedFiles.TryGet(Path.Combine(root,
        StreamingAssetV3Constants.LibrariesDirectory, hash + ".lib"));
    if (file == null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    string[] parts = segments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0 || parts.Length > MaxSegments)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    List<(long Offset, int Length)> wanted = new(parts.Length);
    long payload = 0;
    foreach (string part in parts)
    {
        int dash = part.IndexOf('-');
        if (dash <= 0 || dash >= part.Length - 1 ||
            !long.TryParse(part.AsSpan(0, dash), out long offset) ||
            !int.TryParse(part.AsSpan(dash + 1), out int length) ||
            offset < 0 || length <= 0 || offset > file.Length || length > file.Length - offset)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        payload += length;
        if (payload > MaxPayloadBytes)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        wanted.Add((offset, length));
    }

    context.Response.Headers.CacheControl = $"public,max-age={cacheSeconds},immutable";
    try
    {
        await RangeResponses.SendSegmentsAsync(context, file, wanted);
    }
    catch (OperationCanceledException) { }
}

static bool TryGetHash(string fileName, string extension, out string hash)
{
    hash = string.Empty;
    if (string.IsNullOrEmpty(fileName) || fileName.Length != 64 + extension.Length ||
        !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
    hash = fileName[..64].ToLowerInvariant();
    return StreamingAssetIO.IsValidSha256(hash) &&
           string.Equals(fileName, hash + extension, StringComparison.OrdinalIgnoreCase);
}

static bool Matches(string ifNoneMatch, string etag) =>
    !string.IsNullOrWhiteSpace(ifNoneMatch) && ifNoneMatch.Split(',').Any(value =>
        string.Equals(value.Trim(), etag, StringComparison.Ordinal) || value.Trim() == "*");

static string Quote(string value) => $"\"{value}\"";
