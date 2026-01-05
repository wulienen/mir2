using MicroEndServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Register LibraryParser as singleton service
builder.Services.AddSingleton<LibraryParser>();

// Configure CORS
var serverSettings = builder.Configuration.GetSection("ServerSettings");
var enableCors = serverSettings.GetValue<bool>("EnableCors");

if (enableCors)
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });
}

// Configure file logging
var logPath = serverSettings.GetValue<string>("LogPath") ?? "./logs";
if (!Directory.Exists(logPath))
{
    Directory.CreateDirectory(logPath);
}

builder.Logging.AddProvider(new FileLoggerProvider(logPath));

// Configure Kestrel to use the configured port
var port = serverSettings.GetValue<int>("Port");
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port);
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (enableCors)
{
    app.UseCors();
}

// Request logging middleware
var enableLogging = serverSettings.GetValue<bool>("EnableLogging");
if (enableLogging)
{
    app.Use(async (context, next) =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var startTime = DateTime.UtcNow;
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var queryString = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : "";
        
        logger.LogInformation(
            "[{Timestamp}] Request: {Method} {Path}{Query} from {ClientIP}",
            startTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            context.Request.Method,
            context.Request.Path,
            queryString,
            clientIp);
        
        await next();
        
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        logger.LogInformation(
            "[{Timestamp}] Response: {StatusCode} for {Method} {Path} - {Elapsed:F2}ms",
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            context.Response.StatusCode,
            context.Request.Method,
            context.Request.Path,
            elapsed);
    });
}

app.MapControllers();

Console.WriteLine($"MicroEndServer starting on port {port}...");
Console.WriteLine($"Resource path: {serverSettings.GetValue<string>("ResourcePath")}");
Console.WriteLine($"Log path: {logPath}");

app.Run();

/// <summary>
/// 简单的文件日志提供程序
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    private readonly object _lock = new object();

    public FileLoggerProvider(string logPath)
    {
        _logPath = logPath;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(_logPath, categoryName, _lock);
    }

    public void Dispose() { }
}

/// <summary>
/// 简单的文件日志记录器
/// </summary>
public class FileLogger : ILogger
{
    private readonly string _logPath;
    private readonly string _categoryName;
    private readonly object _lock;

    public FileLogger(string logPath, string categoryName, object lockObj)
    {
        _logPath = logPath;
        _categoryName = categoryName;
        _lock = lockObj;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message))
            return;

        var logFileName = Path.Combine(_logPath, $"server_{DateTime.Now:yyyy-MM-dd}.log");
        var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] {message}";
        
        if (exception != null)
        {
            logEntry += Environment.NewLine + exception.ToString();
        }

        lock (_lock)
        {
            try
            {
                File.AppendAllText(logFileName, logEntry + Environment.NewLine);
            }
            catch
            {
                // 忽略日志写入错误
            }
        }
    }
}
