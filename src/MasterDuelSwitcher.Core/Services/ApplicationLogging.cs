using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using System.Globalization;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>创建按日和文件大小滚动的应用信息日志与调试日志。</summary>
public static class ApplicationLogging
{
    /// <summary>两个日志流共用的时间、级别、类别、消息和异常调用栈输出格式。</summary>
    private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

    /// <summary>在数据目录中创建独立日志工厂；释放工厂时关闭并刷新所属日志文件。</summary>
    public static ILoggerFactory CreateFactory(string dataDirectory, long fileSizeLimitBytes = 5 * 1024 * 1024, int retainedFileCountLimit = 14)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSizeLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFileCountLimit);

        string logs = Path.Combine(Path.GetFullPath(dataDirectory), "logs");
        Directory.CreateDirectory(logs);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logs, "run-.log"),
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: OutputTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFileCountLimit)
            .WriteTo.File(
                Path.Combine(logs, "debug-.log"),
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: OutputTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFileCountLimit)
            .CreateLogger();

        return new SerilogLoggerFactory(logger, dispose: true);
    }
}
