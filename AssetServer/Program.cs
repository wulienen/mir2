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
    if (!File.Exists(path)) return Results.NotFound();

    byte[] bytes;
    try { bytes = File.ReadAllBytes(path); }
    catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    string etag = Quote(StreamingAssetIO.ComputeSha256(bytes));
    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = "no-cache";
    if (Matches(context.Request.Headers.IfNoneMatch, etag))
        return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Bytes(bytes, "application/json; charset=utf-8");
});

app.MapGet("/assets/v3/catalogs/{fileName}", (HttpContext context, string fileName) =>
    ServeImmutable(context, assetRoot, StreamingAssetV3Constants.CatalogsDirectory,
        fileName, ".bin", "application/octet-stream", cacheSeconds, requireRange: true));

app.MapGet("/assets/v3/libraries/{fileName}", (HttpContext context, string fileName) =>
    ServeImmutable(context, assetRoot, StreamingAssetV3Constants.LibrariesDirectory,
        fileName, ".lib", "application/octet-stream", cacheSeconds, requireRange: true));

app.MapGet("/assets/v3/maps/{fileName}", (HttpContext context, string fileName) =>
    ServeImmutable(context, assetRoot, StreamingAssetV3Constants.MapsDirectory,
        fileName, ".mappack", "application/octet-stream", cacheSeconds, requireRange: true));

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
    return ServeImmutable(context, assetRoot, StreamingAssetV3Constants.SoundsDirectory,
        fileName, extension, contentType, cacheSeconds, requireRange: false);
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

static IResult ServeImmutable(HttpContext context, string root, string directory, string fileName,
    string expectedExtension, string contentType, int cacheSeconds, bool requireRange)
{
    if (!TryGetHash(fileName, expectedExtension, out string hash)) return Results.BadRequest();
    string path = Path.Combine(root, directory, hash + expectedExtension);
    if (!File.Exists(path)) return Results.NotFound();

    FileInfo info;
    try { info = new FileInfo(path); }
    catch { return Results.NotFound(); }

    string etag = Quote(hash);
    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = $"public,max-age={cacheSeconds},immutable";
    context.Response.Headers.AcceptRanges = "bytes";

    if (requireRange && !context.Request.Headers.ContainsKey("Range"))
    {
        context.Response.Headers.ContentRange = $"bytes */{info.Length}";
        return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
    }
    if (context.Request.Headers.TryGetValue("Range", out var range) &&
        (!range.ToString().StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || range.ToString().Contains(',')))
    {
        context.Response.Headers.ContentRange = $"bytes */{info.Length}";
        return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
    }
    if (!context.Request.Headers.ContainsKey("Range") && Matches(context.Request.Headers.IfNoneMatch, etag))
        return Results.StatusCode(StatusCodes.Status304NotModified);

    return Results.File(path, contentType, enableRangeProcessing: true);
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
