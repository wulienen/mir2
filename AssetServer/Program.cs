using Shared.StreamingAssets;
using System.Security.Cryptography;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string urls = builder.Configuration["Urls"] ?? "http://0.0.0.0:8088";
builder.WebHost.UseUrls(urls);

WebApplication app = builder.Build();

string assetRoot = Path.GetFullPath(builder.Configuration["AssetRoot"] ?? "StreamingAssets");
string assetRootWithSeparator = assetRoot.EndsWith(Path.DirectorySeparatorChar)
    ? assetRoot
    : assetRoot + Path.DirectorySeparatorChar;
int cacheSeconds = builder.Configuration.GetValue("CacheSeconds", 31536000);

Directory.CreateDirectory(assetRoot);

app.MapGet("/", () => Results.Text("YangfeiCrystal AssetServer"));

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
    context.Response.Headers.CacheControl = $"public,max-age={cacheSeconds}";

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
