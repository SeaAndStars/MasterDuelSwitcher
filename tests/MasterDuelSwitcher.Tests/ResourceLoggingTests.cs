using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证资源操作的持久日志契约、步骤信息和异常堆栈保留。</summary>
public sealed class ResourceLoggingTests : IDisposable
{
    /// <summary>本测试拥有的隔离目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.ResourceLogs", Guid.NewGuid().ToString("N"));
    /// <summary>临时游戏安装目录。</summary>
    private readonly string game;
    /// <summary>临时应用状态目录。</summary>
    private readonly string state;
    /// <summary>记录实际日志事件的测试日志接收器。</summary>
    private readonly EventLogger logger = new();
    /// <summary>注入日志接口的真实资源服务。</summary>
    private readonly ResourceSharingService service;

    /// <summary>建立包含敏感占位内容的资源，确保日志只描述操作不记录内容。</summary>
    public ResourceLoggingTests()
    {
        game = Path.Combine(root, "game");
        state = Path.Combine(root, "state");
        Directory.CreateDirectory(Resource("1234ABCD"));
        Directory.CreateDirectory(Resource("5678EF90"));
        File.WriteAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin"), "resource-content-SECRET");
        File.WriteAllText(Path.Combine(Resource("5678EF90"), "bundle.bin"), "private-save-SECRET");
        service = new ResourceSharingService(state, new WindowsResourceFileSystem(), () => false, logger);
    }

    /// <summary>共享、扫描、读清单与还原记录开始和成功，事务步骤可按 ID 追踪。</summary>
    [Fact]
    public void TransactionsEmitOperationalResultsAndDebugStepsWithoutFileContents()
    {
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Null(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Equal(2, service.ScanProfiles(game).Count);
        Assert.Single(service.GetBackups(game));
        service.Restore(backup.Id);
        service.Restore(backup.Id);
        Assert.Contains(logger.Events, item => item.Level == LogLevel.Information && item.Text.Contains("资源共享开始", StringComparison.Ordinal));
        Assert.Contains(logger.Events, item => item.Level == LogLevel.Information && item.Text.Contains("资源共享完成", StringComparison.Ordinal));
        Assert.Contains(logger.Events, item => item.Level == LogLevel.Information && item.Text.Contains("资源还原完成", StringComparison.Ordinal));
        Assert.Contains(logger.Events, item => item.Level == LogLevel.Debug && item.Text.Contains(backup.Id, StringComparison.Ordinal) && item.Text.Contains("清单", StringComparison.Ordinal));
        Assert.Contains(logger.Events, item => item.Level == LogLevel.Debug && item.Text.Contains("移动", StringComparison.Ordinal));
        Assert.Contains(logger.Events, item => item.Level == LogLevel.Debug && item.Text.Contains("junction", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Events, item => item.Text.Contains("resource-content-SECRET", StringComparison.Ordinal) || item.Text.Contains("private-save-SECRET", StringComparison.Ordinal));
    }

    /// <summary>四个操作的异常均保留原异常对象和堆栈，不静默吞掉故障。</summary>
    [Fact]
    public void EveryPublicOperationRecordsFailureWithOriginalExceptionAndStack()
    {
        Assert.Throws<DirectoryNotFoundException>(() => service.ScanProfiles(Path.Combine(root, "missing-game")));
        Assert.Throws<ArgumentException>(() => service.EnableSharing(game, "1234ABCD", []));
        var manifestFolder = Path.Combine(state, "resource-backups");
        Directory.CreateDirectory(manifestFolder);
        File.WriteAllText(Path.Combine(manifestFolder, new string('a', 32) + ".json"), "{broken");
        Assert.Throws<InvalidDataException>(() => service.GetBackups(game));
        Assert.Throws<ArgumentException>(() => service.Restore("invalid-id"));
        var errors = logger.Events.Where(item => item.Level == LogLevel.Error).ToArray();
        Assert.Equal(4, errors.Length);
        Assert.All(errors, item => { Assert.NotNull(item.Exception); Assert.False(string.IsNullOrEmpty(item.Exception.StackTrace)); });
    }

    /// <summary>返回临时账号资源路径。</summary>
    private string Resource(string folder) => Path.Combine(game, "LocalData", folder, "0000");
    /// <summary>清理测试目录，仅移除链接自身。</summary>
    public void Dispose() => ResourceFailureTests.DeleteTree(root);

    /// <summary>持有日志事件的实际 ILogger 接收器。</summary>
    private sealed class EventLogger : ILogger<ResourceSharingService>
    {
        /// <summary>记录消息级别、格式化文本和原异常。</summary>
        internal List<(LogLevel Level, string Text, Exception? Exception)> Events { get; } = [];
        /// <summary>测试日志无需作用域。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>接收全部日志级别以验证 Debug 步骤。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保存格式化日志和异常对象。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Events.Add((logLevel, formatter(state, exception), exception));
    }
}
