using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>通过真实临时文件验证日志级别、异常格式、滚动与保留行为。</summary>
public sealed class LoggingTests : IDisposable
{
    /// <summary>每次测试独占的数据目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelLoggingTests", Guid.NewGuid().ToString("N"));

    /// <summary>验证信息流排除调试日志，而调试流包含信息及更高日志级别。</summary>
    [Fact]
    public void CreateFactorySeparatesDebugFromInformationAndHigherLevels()
    {
        using (var factory = ApplicationLogging.CreateFactory(root))
        {
            var logger = factory.CreateLogger("LoggingTests.Levels");
            logger.LogTrace("trace event");
            logger.LogDebug("debug event");
            logger.LogInformation("information event");
            logger.LogWarning("warning event");
            logger.LogError("error event");
            logger.LogCritical("critical event");
        }

        string run = ReadLogStream("run");
        string debug = ReadLogStream("debug");
        Assert.DoesNotContain("trace event", run);
        Assert.DoesNotContain("trace event", debug);
        Assert.DoesNotContain("debug event", run);
        Assert.Contains("debug event", debug);
        foreach (string message in new[] { "information event", "warning event", "error event", "critical event" })
        {
            Assert.Contains(message, run);
            Assert.Contains(message, debug);
        }
    }

    /// <summary>验证两个日志流包含时间、级别、类别、错误消息和实际异常调用栈。</summary>
    [Fact]
    public void CreateFactoryIncludesTimestampCategoryAndExceptionStack()
    {
        using (var factory = ApplicationLogging.CreateFactory(root))
        {
            factory.CreateLogger("LoggingTests.ExceptionCategory").LogError(CaptureFailure(), "operation failed");
        }

        foreach (string stream in new[] { "run", "debug" })
        {
            string log = ReadLogStream(stream);
            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}", log);
            Assert.Contains("[ERR]", log);
            Assert.Contains("LoggingTests.ExceptionCategory", log);
            Assert.Contains("operation failed", log);
            Assert.Contains("System.InvalidOperationException: fixture failure", log);
            Assert.Contains(nameof(CaptureFailure), log);
        }
    }

    /// <summary>验证缺失的数据目录自动创建，日志使用按日命名且释放后可独占打开。</summary>
    [Fact]
    public void CreateFactoryCreatesDailyLogFilesAndDisposalReleasesHandles()
    {
        Assert.False(Directory.Exists(root));
        using (var factory = ApplicationLogging.CreateFactory(root))
        {
            Assert.True(Directory.Exists(Path.Combine(root, "logs")));
            factory.CreateLogger("LoggingTests.Dispose").LogInformation("flush event");
        }

        foreach (string stream in new[] { "run", "debug" })
        {
            string file = Assert.Single(GetLogFiles(stream));
            Assert.Matches("^" + stream + @"-\d{8}\.log$", Path.GetFileName(file));
            using var handle = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(handle.Length > 0);
        }
        Assert.Contains("flush event", ReadLogStream("run"));
        Assert.Contains("flush event", ReadLogStream("debug"));
    }

    /// <summary>验证两个日志流达到指定文件大小时滚动，并各自只保留指定份数。</summary>
    [Fact]
    public void CreateFactoryRollsBothStreamsAndRetainsOnlyNewestConfiguredFiles()
    {
        using (var factory = ApplicationLogging.CreateFactory(root, fileSizeLimitBytes: 256, retainedFileCountLimit: 3))
        {
            var logger = factory.CreateLogger("LoggingTests.Rolling");
            for (int sequence = 0; sequence < 40; sequence++)
                logger.LogInformation("roll-{Sequence}: {Payload}", sequence, new string('x', 128));
        }

        foreach (string stream in new[] { "run", "debug" })
        {
            string[] files = GetLogFiles(stream);
            Assert.Equal(3, files.Length);
            Assert.Contains(files, file => Path.GetFileName(file).Contains('_'));
            string text = ReadLogStream(stream);
            Assert.Contains("roll-39:", text);
            Assert.DoesNotContain("roll-0:", text);
        }
    }

    /// <summary>验证默认保留配置分别清理每个日志流的历史文件到十四份。</summary>
    [Fact]
    public void CreateFactoryDefaultRetentionKeepsFourteenFilesPerStream()
    {
        string logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        foreach (string stream in new[] { "run", "debug" })
        {
            for (int day = 1; day <= 16; day++)
                File.WriteAllText(Path.Combine(logs, $"{stream}-202001{day:00}.log"), "old fixture");
        }

        using (var factory = ApplicationLogging.CreateFactory(root))
        {
            factory.CreateLogger("LoggingTests.Retention").LogInformation("current retention event");
        }

        foreach (string stream in new[] { "run", "debug" })
        {
            Assert.Equal(14, GetLogFiles(stream).Length);
            Assert.False(File.Exists(Path.Combine(logs, $"{stream}-20200101.log")));
            Assert.Contains("current retention event", ReadLogStream(stream));
        }
    }

    /// <summary>验证默认五 MiB 大小限制在持续写入时触发两个日志流的文件滚动。</summary>
    [Fact]
    public void CreateFactoryDefaultSizeLimitRollsAfterFiveMebibytes()
    {
        using (var factory = ApplicationLogging.CreateFactory(root))
        {
            var logger = factory.CreateLogger("LoggingTests.DefaultSize");
            for (int sequence = 0; sequence < 7; sequence++)
                logger.LogInformation("large-{Sequence}: {Payload}", sequence, new string('x', 1024 * 1024));
        }

        foreach (string stream in new[] { "run", "debug" })
        {
            string[] files = GetLogFiles(stream);
            Assert.Equal(2, files.Length);
            Assert.Contains(files, file => Path.GetFileName(file).Contains('_'));
            Assert.Contains("large-6:", ReadLogStream(stream));
        }
    }

    /// <summary>验证日志工厂彼此独立且创建过程不替换全局 Serilog 日志器。</summary>
    [Fact]
    public void CreateFactoryKeepsFactoriesIndependentWithoutChangingGlobalLogger()
    {
        var globalLogger = Serilog.Log.Logger;
        string secondRoot = Path.Combine(root, "second");
        using (var first = ApplicationLogging.CreateFactory(root))
        using (var second = ApplicationLogging.CreateFactory(secondRoot))
        {
            first.CreateLogger("LoggingTests.First").LogInformation("first factory event");
            second.CreateLogger("LoggingTests.Second").LogInformation("second factory event");
        }

        Assert.Same(globalLogger, Serilog.Log.Logger);
        Assert.Contains("first factory event", ReadLogStream("run"));
        Assert.DoesNotContain("second factory event", ReadLogStream("run"));
        string secondLog = string.Join("", Directory.GetFiles(Path.Combine(secondRoot, "logs"), "run-*.log").Select(File.ReadAllText));
        Assert.Contains("second factory event", secondLog);
        Assert.DoesNotContain("first factory event", secondLog);
    }

    /// <summary>验证同一数据目录的两个同时运行实例都持续记录信息和调试消息。</summary>
    [Fact]
    public void ConcurrentFactoriesKeepBothInstancesMessagesInSharedStreams()
    {
        using (var first = ApplicationLogging.CreateFactory(root))
        {
            first.CreateLogger("LoggingTests.FirstInstance").LogInformation("first instance started");
            using (var second = ApplicationLogging.CreateFactory(root))
            {
                second.CreateLogger("LoggingTests.SecondInstance").LogInformation("second instance started");
                second.CreateLogger("LoggingTests.SecondInstance").LogDebug("second instance debug");
                first.CreateLogger("LoggingTests.FirstInstance").LogDebug("first instance debug");
            }
            first.CreateLogger("LoggingTests.FirstInstance").LogInformation("first instance continued");
        }

        foreach (string stream in new[] { "run", "debug" })
        {
            string log = ReadLogStream(stream);
            Assert.Contains("first instance started", log);
            Assert.Contains("second instance started", log);
            Assert.Contains("first instance continued", log);
        }
        Assert.Contains("first instance debug", ReadLogStream("debug"));
        Assert.Contains("second instance debug", ReadLogStream("debug"));
    }

    /// <summary>验证日志工厂拒绝空的数据目录。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateFactoryRejectsMissingDataDirectory(string? dataDirectory)
    {
        Assert.ThrowsAny<ArgumentException>(() => ApplicationLogging.CreateFactory(dataDirectory!));
    }

    /// <summary>验证日志工厂拒绝非正数文件大小限制。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateFactoryRejectsNonPositiveFileSizeLimit(long fileSizeLimitBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ApplicationLogging.CreateFactory(root, fileSizeLimitBytes));
    }

    /// <summary>验证日志工厂拒绝非正数历史文件保留份数。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateFactoryRejectsNonPositiveRetainedFileCount(int retainedFileCountLimit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ApplicationLogging.CreateFactory(root, retainedFileCountLimit: retainedFileCountLimit));
    }

    /// <summary>验证数据目录实际为文件时报告文件系统错误。</summary>
    [Fact]
    public void CreateFactoryRejectsDataDirectoryThatIsAFile()
    {
        Directory.CreateDirectory(root);
        string dataFile = Path.Combine(root, "data-file");
        File.WriteAllText(dataFile, "fixture");

        Assert.Throws<IOException>(() => ApplicationLogging.CreateFactory(dataFile));
    }

    /// <summary>读取指定日志流的所有真实日志内容。</summary>
    private string ReadLogStream(string stream) => string.Join("", GetLogFiles(stream).Select(File.ReadAllText));

    /// <summary>列出指定日志流的真实滚动文件。</summary>
    private string[] GetLogFiles(string stream) => Directory.GetFiles(Path.Combine(root, "logs"), stream + "-*.log");

    /// <summary>生成带实际调用栈的异常，供日志落盘断言使用。</summary>
    private static Exception CaptureFailure()
    {
        try
        {
            throw new InvalidOperationException("fixture failure");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    /// <summary>清理每次测试独占的数据目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
