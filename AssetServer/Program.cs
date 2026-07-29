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

string assetRoot = Path.GetFullPath(builder.Configuration["AssetRoot"] ?? "StreamingAssets");
int cacheSeconds = builder.Configuration.GetValue("CacheSeconds", 31536000);
Directory.CreateDirectory(assetRoot);

app.MapGet("/", () => Results.Text("YangfeiCrystal AssetServer v2"));

app.MapGet("/assets/v2/manifest.json", (HttpContext context) =>
{
    string path = Path.Combine(assetRoot, StreamingAssetConstants.ManifestFileName);
    if (!File.Exists(path)) return Results.NotFound();

    byte[] bytes = File.ReadAllBytes(path);
    string etag = $"\"{StreamingAssetIO.ComputeSha256(bytes)}\"";
    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = "no-cache";
    if (string.Equals(context.Request.Headers.IfNoneMatch, etag, StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status304NotModified);

    return Results.Bytes(bytes, "application/json; charset=utf-8");
});

app.MapGet("/assets/v2/objects/batch", async (HttpContext context, string hashes) =>
{
    string[] requested = (hashes ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(hash => hash.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .Take(64)
        .ToArray();
    if (requested.Length == 0 || requested.Any(hash => !StreamingAssetIO.IsValidSha256(hash)))
        return Results.BadRequest();

    List<(string Hash, byte[] Data)> objects = new(requested.Length);
    foreach (string hash in requested)
    {
        string path = Path.Combine(assetRoot, StreamingAssetConstants.ObjectsDirectory, hash[..2], hash + ".bin");
        if (!File.Exists(path)) return Results.NotFound();
        objects.Add((hash, await File.ReadAllBytesAsync(path)));
    }

    using MemoryStream output = new();
    using (BinaryWriter writer = new(output, System.Text.Encoding.UTF8, true))
    {
        writer.Write(objects.Count);
        foreach ((string hash, byte[] data) in objects)
        {
            writer.Write(hash);
            writer.Write(data.Length);
            writer.Write(data);
        }
    }

    context.Response.Headers.CacheControl = "no-store";
    return Results.Bytes(output.ToArray(), "application/octet-stream");
});

app.MapGet("/assets/v2/objects/{prefix}/{fileName}", (HttpContext context, string prefix, string fileName) =>
{
    if (prefix?.Length != 2 || fileName?.Length != 68 ||
        !fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();

    string hash = fileName[..64].ToLowerInvariant();
    if (!StreamingAssetIO.IsValidSha256(hash) ||
        !string.Equals(prefix, hash[..2], StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();

    string path = Path.Combine(assetRoot, StreamingAssetConstants.ObjectsDirectory, hash[..2], hash + ".bin");
    if (!File.Exists(path)) return Results.NotFound();

    string etag = $"\"{hash}\"";
    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = $"public,max-age={cacheSeconds},immutable";
    context.Response.Headers.AcceptRanges = "bytes";
    if (string.Equals(context.Request.Headers.IfNoneMatch, etag, StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status304NotModified);

    return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
});

app.MapGet("/health", () =>
{
    string manifestPath = Path.Combine(assetRoot, StreamingAssetConstants.ManifestFileName);
    bool manifestValid = false;
    if (File.Exists(manifestPath))
    {
        try
        {
            AssetManifest manifest = StreamingAssetIO.ReadJson<AssetManifest>(manifestPath);
            manifestValid = manifest?.FormatVersion == StreamingAssetConstants.CurrentFormatVersion;
        }
        catch
        {
        }
    }

    return Results.Json(new { ok = manifestValid, formatVersion = StreamingAssetConstants.CurrentFormatVersion, assetRoot });
});

app.Run();
