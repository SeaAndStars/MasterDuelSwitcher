using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证 Steam 操作日志的阶段、异常及敏感配置隔离。</summary>
public sealed class SteamLoggingTests : IDisposable
{
    /// <summary>包含敏感占位字段的隔离配置，任何日志均不得输出其内容。</summary>
    private const string Configuration = "users { \"76561198000000002\" { AccountName fixture MostRecent 0 Password fixture-password-secret Token fixture-token-secret Notes fixture-note-secret } }";
    /// <summary>本次测试独占的文件根目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSteamLogTests", Guid.NewGuid().ToString("N"));
    /// <summary>通过真实临时文件验证的 Steam 目录。</summary>
    private readonly string steamPath;

    /// <summary>创建隔离 Steam 安装和登录配置，不运行任何客户端。</summary>
    public SteamLoggingTests()
    {
        steamPath = Path.Combine(root, "Steam");
        Directory.CreateDirectory(Path.Combine(steamPath, "config"));
        File.WriteAllText(Path.Combine(steamPath, "steam.exe"), "fixture");
        File.WriteAllText(Path.Combine(steamPath, "config", "loginusers.vdf"), Configuration);
    }

    /// <summary>所选测试账号，仅参与临时配置事务。</summary>
    private static SteamAccount Selected => new() { SteamId = "76561198000000002", AccountName = "fixture" };

    /// <summary>验证正常切换和还原均记录开始、成功及 Debug 事务阶段。</summary>
    [Fact]
    public async Task SwitchAndRestoreRecordStagesWithoutConfigurationSecrets()
    {
        var logger = new RecordingLogger<SteamAccountService>();
        var service = new SteamAccountService(Path.Combine(root, "State"), new FixturePlatform(), logger);

        await service.SwitchAndLaunchAsync(steamPath, Selected);
        Assert.True(logger.Entries.Count(entry => entry.Level == LogLevel.Information) >= 2);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
        AssertNoSecrets(logger.Entries);
        logger.Entries.Clear();

        await service.RestoreLatestAsync(steamPath);

        Assert.True(logger.Entries.Count(entry => entry.Level == LogLevel.Information) >= 2);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
        AssertNoSecrets(logger.Entries);
    }

    /// <summary>验证切换被运行中的游戏拒绝时记录实际异常及堆栈。</summary>
    [Fact]
    public async Task RejectedSwitchLogsExceptionWithStack()
    {
        var logger = new RecordingLogger<SteamAccountService>();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(Path.Combine(root, "State"), new FixturePlatform { GameRunning = true }, logger).SwitchAndLaunchAsync(steamPath, Selected));

        var entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Same(failure, entry.Exception);
        Assert.False(string.IsNullOrWhiteSpace(entry.Exception!.StackTrace));
        AssertNoSecrets(logger.Entries);
    }

    /// <summary>验证缺失备份的还原失败会记录异常，且不记录账号信息。</summary>
    [Fact]
    public async Task MissingRestoreLogsFailureWithoutSecrets()
    {
        var logger = new RecordingLogger<SteamAccountService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(Path.Combine(root, "State"), new FixturePlatform(), logger).RestoreLatestAsync(steamPath));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is not null);
        AssertNoSecrets(logger.Entries);
    }

    /// <summary>验证发现与账号读取日志包含计数和阶段，不输出 VDF 敏感字段。</summary>
    [Fact]
    public void DiscoveryRecordsStagesAndCountsWithoutSecrets()
    {
        var logger = new RecordingLogger<SteamDiscoveryService>();
        var discovery = new SteamDiscoveryService(logger: logger);

        var result = discovery.Discover(steamPath);

        Assert.Single(result.Accounts);
        Assert.True(logger.Entries.Count(entry => entry.Level == LogLevel.Information) >= 2);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
        AssertNoSecrets(logger.Entries);
    }

    /// <summary>验证发现故障和账号格式错误分别记录异常，错误文本不包含配置正文。</summary>
    [Fact]
    public void DiscoveryAndAccountReadFailuresAreLoggedWithoutInputText()
    {
        var logger = new RecordingLogger<SteamDiscoveryService>();
        var discovery = new SteamDiscoveryService(logger: logger);
        Assert.Throws<InvalidOperationException>(() => discovery.Discover(Path.Combine(root, "missing")));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is not null);
        File.WriteAllText(Path.Combine(steamPath, "config", "loginusers.vdf"), "users { fixture-password-secret");
        Assert.Throws<FormatException>(() => discovery.ReadAccounts(steamPath));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is not null);
        AssertNoSecrets(logger.Entries);
    }

    /// <summary>逐项核验事件正文与异常描述不含敏感占位值或完整 VDF。</summary>
    private static void AssertNoSecrets(IEnumerable<LogEntry> entries)
    {
        string text = string.Join("\n", entries.Select(entry => entry.Message + entry.Exception));
        Assert.DoesNotContain("fixture-password-secret", text);
        Assert.DoesNotContain("fixture-token-secret", text);
        Assert.DoesNotContain("fixture-note-secret", text);
        Assert.DoesNotContain(Configuration, text);
    }

    /// <summary>清理独占的隔离文件目录。</summary>
    public void Dispose() => Directory.Delete(root, true);

    /// <summary>捕获日志事件的级别、文本与实际异常。</summary>
    private sealed class LogEntry
    {
        /// <summary>当前事件的日志级别。</summary>
        public LogLevel Level { get; init; }
        /// <summary>格式化后的非敏感事件文本。</summary>
        public string Message { get; init; } = "";
        /// <summary>真实操作异常及其堆栈。</summary>
        public Exception? Exception { get; init; }
    }

    /// <summary>只在测试内保存事件的标准 Microsoft 日志边界。</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        /// <summary>当前测试捕获的全部日志事件。</summary>
        public List<LogEntry> Entries { get; } = [];
        /// <summary>测试不添加额外作用域数据。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>测试捕获全部级别，包括 Debug。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保存经实际日志调用格式化后的事件。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add(new() { Level = logLevel, Message = formatter(state, exception), Exception = exception });
    }

    /// <summary>只替代客户端进程和用户注册表，保留真实文件事务的隔离平台。</summary>
    private sealed class FixturePlatform : ISteamPlatform
    {
        /// <summary>模拟游戏是否运行。</summary>
        public bool GameRunning { get; set; }
        /// <summary>隔离测试使用的原始自动登录字段。</summary>
        private IReadOnlyList<SteamRegistryValue> values = [new() { Name = "AutoLoginUser", Exists = false }, new() { Name = "RememberPassword", Exists = false }];
        /// <summary>返回测试指定的游戏状态。</summary>
        public bool IsGameRunning() => GameRunning;
        /// <summary>模拟退出客户端，不接触真实进程。</summary>
        public Task ShutdownSteamAsync(string steamPath, CancellationToken cancellationToken) => Task.CompletedTask;
        /// <summary>返回隔离注册表快照。</summary>
        public IReadOnlyList<SteamRegistryValue> ReadLoginRegistry() => values;
        /// <summary>在隔离内存中保存注册表状态。</summary>
        public void WriteLoginRegistry(IReadOnlyList<SteamRegistryValue> snapshot) => values = snapshot.ToArray();
        /// <summary>模拟请求启动，不运行任何客户端。</summary>
        public Task LaunchGameAsync(string steamPath, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
