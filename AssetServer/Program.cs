using Shared.StreamingAssets;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.AspNetCore.ResponseCompression;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes
        .Concat(new[] { "application/json", "text/plain" });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

string urls = builder.Configuration["Urls"] ?? "http://0.0.0.0:8088";
builder.WebHost.UseUrls(urls);

WebApplication app = builder.Build();
app.UseResponseCompression();

string assetRoot = Path.GetFullPath(builder.Configuration["AssetRoot"] ?? "StreamingAssets");
string assetRootWithSeparator = assetRoot.EndsWith(Path.DirectorySeparatorChar)
    ? assetRoot
    : assetRoot + Path.DirectorySeparatorChar;
int cacheSeconds = builder.Configuration.GetValue("CacheSeconds", 31536000);

Directory.CreateDirectory(assetRoot);

app.MapGet("/", () => Results.Text("YangfeiCrystal AssetServer"));

app.MapGet("/assets/v1/maps/{mapId}/chunks/batch", async (HttpContext context, string mapId, string keys) =>
{
    if (string.IsNullOrWhiteSpace(mapId) || string.IsNullOrWhiteSpace(keys) ||
        mapId.Contains('/', StringComparison.Ordinal) || mapId.Contains('\\', StringComparison.Ordinal) ||
        mapId.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(mapId))
    {
        return Results.BadRequest("Invalid map batch request.");
    }

    string[] requestedKeys = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .Take(64)
        .ToArray();
    if (requestedKeys.Length == 0) return Results.BadRequest("No map chunks requested.");

    List<(string Key, byte[] Data)> chunks = new();
    foreach (string key in requestedKeys)
    {
        string[] parts = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y) || x < 0 || y < 0)
            return Results.BadRequest("Invalid map chunk key.");

        string normalizedKey = $"{x}_{y}";
        string relative = Path.Combine(StreamingAssetConstants.MapsDirectory, mapId,
            StreamingAssetConstants.MapChunksDirectory, normalizedKey + ".bin");
        string fullPath = Path.GetFullPath(Path.Combine(assetRoot, relative));
        if (!fullPath.StartsWith(assetRootWithSeparator, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return Results.NotFound();

        chunks.Add((normalizedKey, await File.ReadAllBytesAsync(fullPath)));
    }

    using MemoryStream output = new();
    using (BinaryWriter writer = new(output, System.Text.Encoding.UTF8, true))
    {
        writer.Write(chunks.Count);
        foreach ((string key, byte[] data) in chunks)
        {
            writer.Write(key);
            writer.Write(data.Length);
            writer.Write(data);
        }
    }

    context.Response.Headers.CacheControl = "no-store";
    return Results.Bytes(output.ToArray(), "application/octet-stream");
});

app.MapGet("/assets/v1/{**assetPath}", async (HttpContext context, string assetPath) =>
{
    if (string.IsNullOrWhiteSpace(assetPath))
    {
        return Results.NotFound();
    }

    string normalized = assetPath.Replace('\\', '/');
    if (normalized.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(normalized))
    {
        return Results.BadRequest("Invalid asset path.");
    }

    string fullPath = Path.GetFullPath(Path.Combine(assetRoot, normalized));
    bool insideRoot = string.Equals(fullPath, assetRoot, StringComparison.OrdinalIgnoreCase) ||
                      fullPath.StartsWith(assetRootWithSeparator, StringComparison.OrdinalIgnoreCase);
    if (!insideRoot || !File.Exists(fullPath))
    {
        return Results.NotFound();
    }

    FileInfo info = new(fullPath);
    string etag = $"\"{Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(fullPath + info.Length + info.LastWriteTimeUtc.Ticks))).ToLowerInvariant()}\"";

    context.Response.Headers.ETag = etag;
    bool contentAddressed = context.Request.Query.ContainsKey("h");
    context.Response.Headers.CacheControl = contentAddressed
        ? $"public,max-age={cacheSeconds},immutable"
        : "no-cache";

    if (string.Equals(context.Request.Headers.IfNoneMatch, etag, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status304NotModified;
        return Results.Empty;
    }

    string contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
    {
        ".json" => "application/json; charset=utf-8",
        ".bin" => "application/octet-stream",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".lst" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    context.Response.Headers.AcceptRanges = "bytes";
    return Results.File(fullPath, contentType, enableRangeProcessing: true);
});

app.MapGet("/health", () => Results.Json(new
{
    ok = true,
    assetRoot,
    manifest = File.Exists(Path.Combine(assetRoot, StreamingAssetConstants.ManifestFileName))
}));

app.Run();
