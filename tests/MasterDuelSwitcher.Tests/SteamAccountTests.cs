using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Win32;
using System.Text.Json;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证临时登录配置的备份、切换与异常还原。</summary>
public sealed class SteamAccountTests : IDisposable
{
    /// <summary>测试使用的原始登录配置；其附加字段必须完整保留。</summary>
    private const string Original = "\"users\" { \"76561198000000001\" { \"AccountName\" \"first\" \"MostRecent\" \"1\" \"AllowAutoLogin\" \"1\" \"Timestamp\" \"111\" } \"76561198000000002\" { \"AccountName\" \"second\" \"MostRecent\" \"0\" \"AllowAutoLogin\" \"0\" \"PersonaName\" \"中文昵称\" \"Unrelated\" \"kept\" } } \"Extra\" \"preserved\"";
    /// <summary>每次测试独占的临时根目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelAccountTests", Guid.NewGuid().ToString("N"));
    /// <summary>通过真实临时文件测试的 Steam 路径。</summary>
    private readonly string steamPath;
    /// <summary>所有本地事务清单保存的位置。</summary>
    private readonly string statePath;
    /// <summary>隔离进程与用户注册表操作的测试系统边界。</summary>
    private readonly FixturePlatform platform = new();

    /// <summary>创建登录配置和占位可执行文件，始终不运行实际 Steam。</summary>
    public SteamAccountTests()
    {
        steamPath = Path.Combine(root, "Steam");
        statePath = Path.Combine(root, "State");
        Directory.CreateDirectory(Path.Combine(steamPath, "config"));
        File.WriteAllText(Path.Combine(steamPath, "steam.exe"), "fixture");
        File.WriteAllText(LoginPath, Original);
    }

    /// <summary>测试登录配置的实际文件位置。</summary>
    private string LoginPath => Path.Combine(steamPath, "config", "loginusers.vdf");
    /// <summary>测试中明确选择的第二个已存在账号。</summary>
    private static SteamAccount Selected => new() { SteamId = "76561198000000002", AccountName = "second" };

    /// <summary>验证切换选择、无关值保留和原始文件备份。</summary>
    [Fact]
    public async Task SwitchPreservesUnrelatedValuesAndBacksUpOriginalBytes()
    {
        var service = new SteamAccountService(statePath, platform);

        await service.SwitchAndLaunchAsync(steamPath, Selected);

        var document = VdfDocument.Parse(File.ReadAllText(LoginPath));
        var users = document.Find("users")!;
        Assert.Equal("0", users.Find("76561198000000001")!.Find("MostRecent")!.Value);
        Assert.Equal("1", users.Find("76561198000000002")!.Find("MostRecent")!.Value);
        Assert.Equal("1", users.Find("76561198000000002")!.Find("AllowAutoLogin")!.Value);
        Assert.Equal("kept", users.Find("76561198000000002")!.Find("Unrelated")!.Value);
        Assert.Equal("111", users.Find("76561198000000001")!.Find("Timestamp")!.Value);
        Assert.Equal("preserved", document.Find("Extra")!.Value);
        Assert.Equal(Original, File.ReadAllText(Assert.Single(Directory.GetFiles(statePath, "loginusers.vdf", SearchOption.AllDirectories))));
        Assert.Equal("second", platform.Values.Single(value => value.Name == "AutoLoginUser").Text);
    }

    /// <summary>验证游戏运行时拒绝变更，也不产生备份事务。</summary>
    [Fact]
    public async Task SwitchRejectsRunningGameBeforeAnyMutation()
    {
        platform.GameRunning = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected));

        Assert.Equal(Original, File.ReadAllText(LoginPath));
        Assert.False(Directory.Exists(Path.Combine(statePath, "steam-login-backups")));
    }

    /// <summary>验证账号标识不在当前配置中时拒绝变更。</summary>
    [Fact]
    public async Task SwitchRejectsUnknownAccountWithoutChangingConfiguration()
    {
        var unknown = new SteamAccount { SteamId = "76561198099999999", AccountName = "unknown" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, unknown));

        Assert.Equal(Original, File.ReadAllText(LoginPath));
    }

    /// <summary>验证正常退出超时保留原始配置和注册表数据。</summary>
    [Fact]
    public async Task SwitchShutdownFailurePreservesOriginalConfiguration()
    {
        platform.ShutdownError = new TimeoutException("fixture timeout");

        await Assert.ThrowsAsync<TimeoutException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected));

        Assert.Equal(Original, File.ReadAllText(LoginPath));
        Assert.Equal("first", platform.Values.Single(value => value.Name == "AutoLoginUser").Text);
    }

    /// <summary>验证启动失败后自动还原文件与注册表。</summary>
    [Fact]
    public async Task SwitchStartupFailureRollsBackOriginalFileAndRegistry()
    {
        platform.LaunchError = new InvalidOperationException("fixture launch failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected));

        Assert.Equal(Original, File.ReadAllText(LoginPath));
        Assert.Equal("first", platform.Values.Single(value => value.Name == "AutoLoginUser").Text);
        Assert.Equal(0, platform.Values.Single(value => value.Name == "RememberPassword").Number);
    }

    /// <summary>验证用户还原最近备份时恢复原始文件及不存在的注册表值。</summary>
    [Fact]
    public async Task RestoreReturnsOriginalFileAndRegistryExistence()
    {
        platform.Values = [new() { Name = "AutoLoginUser", Exists = false }, new() { Name = "RememberPassword", Exists = true, Kind = RegistryValueKind.DWord, Number = 0 }];
        var service = new SteamAccountService(statePath, platform);
        await service.SwitchAndLaunchAsync(steamPath, Selected);

        await service.RestoreLatestAsync(steamPath);

        Assert.Equal(Original, File.ReadAllText(LoginPath));
        Assert.False(platform.Values.Single(value => value.Name == "AutoLoginUser").Exists);
        Assert.Equal(0, platform.Values.Single(value => value.Name == "RememberPassword").Number);
    }

    /// <summary>验证备份校验失败时保留当前配置。</summary>
    [Fact]
    public async Task RestoreRejectsTamperedBackupWithoutWritingConfiguration()
    {
        var service = new SteamAccountService(statePath, platform);
        await service.SwitchAndLaunchAsync(steamPath, Selected);
        string switched = File.ReadAllText(LoginPath);
        File.WriteAllText(Assert.Single(Directory.GetFiles(statePath, "loginusers.vdf", SearchOption.AllDirectories)), "tampered");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreLatestAsync(steamPath));

        Assert.Equal(switched, File.ReadAllText(LoginPath));
    }

    /// <summary>验证部分还原失败会被下一次事务修复，防止备份不一致的注册表状态。</summary>
    [Fact]
    public async Task InterruptedRestoreIsRecoveredBeforeNextSwitch()
    {
        var service = new SteamAccountService(statePath, platform);
        await service.SwitchAndLaunchAsync(steamPath, Selected);
        platform.FailNextRegistryWrite = true;
        await Assert.ThrowsAsync<IOException>(() => service.RestoreLatestAsync(steamPath));

        await service.SwitchAndLaunchAsync(steamPath, Selected);

        var manifests = Directory.GetFiles(statePath, "manifest.json", SearchOption.AllDirectories)
            .Select(path => JsonSerializer.Deserialize<SteamLoginBackup>(File.ReadAllText(path))!).OrderByDescending(backup => backup.CreatedUtc).ToArray();
        Assert.Equal("first", manifests[0].RegistryValues.Single(value => value.Name == "AutoLoginUser").Text);
        Assert.Equal("rolled-back", manifests[1].Status);
    }

    /// <summary>验证还原操作只选择对应 Steam 安装目录的事务。</summary>
    [Fact]
    public async Task RestoreRejectsDifferentSteamDirectoryWithoutChangingEitherFile()
    {
        var service = new SteamAccountService(statePath, platform);
        await service.SwitchAndLaunchAsync(steamPath, Selected);
        string switched = File.ReadAllText(LoginPath);
        string other = Path.Combine(root, "OtherSteam");
        Directory.CreateDirectory(Path.Combine(other, "config"));
        File.WriteAllText(Path.Combine(other, "steam.exe"), "fixture");
        File.WriteAllText(Path.Combine(other, "config", "loginusers.vdf"), "other config");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreLatestAsync(other));

        Assert.Equal(switched, File.ReadAllText(LoginPath));
        Assert.Equal("other config", File.ReadAllText(Path.Combine(other, "config", "loginusers.vdf")));
    }

    /// <summary>验证账号的重复标识不会同时标记为最近使用账号。</summary>
    [Fact]
    public async Task SwitchRejectsDuplicateAccountIdentifiersWithoutMutation()
    {
        string duplicate = "users { \"76561198000000002\" { AccountName second MostRecent 0 } \"76561198000000002\" { AccountName impostor MostRecent 0 } }";
        File.WriteAllText(LoginPath, duplicate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected));

        Assert.Equal(duplicate, File.ReadAllText(LoginPath));
    }

    /// <summary>验证 users 对象中的额外元数据不被当作 Steam 账号修改。</summary>
    [Fact]
    public async Task SwitchPreservesUnrelatedObjectsInsideUsers()
    {
        File.WriteAllText(LoginPath, "users { \"76561198000000002\" { AccountName second MostRecent 0 } Settings { Theme dark } }");

        await new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected);

        var settings = VdfDocument.Parse(File.ReadAllText(LoginPath)).Find("users")!.Find("Settings")!;
        Assert.Equal("dark", Assert.Single(settings.Children).Value);
    }

    /// <summary>验证启动成功后的清单写入失败保留配置，并准确报告已请求启动。</summary>
    [Fact]
    public async Task PostLaunchManifestFailureKeepsAppliedConfiguration()
    {
        FileStream? manifestLock = null;
        platform.AfterLaunch = () =>
        {
            platform.GameRunning = true;
            string manifest = Assert.Single(Directory.GetFiles(statePath, "manifest.json", SearchOption.AllDirectories));
            manifestLock = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None);
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected));
            var selected = VdfDocument.Parse(File.ReadAllText(LoginPath)).Find("users")!.Find("76561198000000002")!;
            Assert.Equal("1", selected.Find("MostRecent")!.Value);
            Assert.Equal("second", platform.Values.Single(value => value.Name == "AutoLoginUser").Text);
        }
        finally
        {
            manifestLock?.Dispose();
        }
        var backup = JsonSerializer.Deserialize<SteamLoginBackup>(File.ReadAllText(Assert.Single(Directory.GetFiles(statePath, "manifest.json", SearchOption.AllDirectories))))!;
        Assert.Equal("applied", backup.Status);
    }

    /// <summary>验证缺失、标量和无效标识账号配置均在写入之前被拒绝。</summary>
    [Theory]
    [InlineData("Extra value")]
    [InlineData("users scalar")]
    [InlineData("users { invalid { AccountName second } }")]
    [InlineData("users { invalididentifier { AccountName second } }")]
    [InlineData("users { \"76561198000000002\" { } }")]
    [InlineData("users { \"76561198000000002\" { AccountName { } } }")]
    [InlineData("users { \"76561198000000002\" { AccountName \"\" } }")]
    [InlineData("users { \"76561198000000002\" scalar }")]
    public async Task SwitchRejectsMalformedAccountShapesWithoutMutation(string content)
    {
        File.WriteAllText(LoginPath, content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected));
        Assert.Equal(content, File.ReadAllText(LoginPath));
    }

    /// <summary>验证陈旧账号名称与不存在登录配置被明确拒绝。</summary>
    [Fact]
    public async Task SwitchRejectsStaleNameAndMissingLoginConfiguration()
    {
        var service = new SteamAccountService(statePath, platform);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAndLaunchAsync(steamPath, new SteamAccount { SteamId = Selected.SteamId, AccountName = "stale" }));
        Assert.Equal(Original, File.ReadAllText(LoginPath));
        File.Delete(LoginPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAndLaunchAsync(steamPath, Selected));
        Assert.False(File.Exists(LoginPath));
    }

    /// <summary>验证排他事务锁在另一个实例持有时阻止配置变更。</summary>
    [Fact]
    public async Task SwitchRejectsConcurrentTransactionLock()
    {
        Directory.CreateDirectory(statePath);
        using var held = new FileStream(Path.Combine(statePath, "steam-login.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected));
        Assert.Equal(Original, File.ReadAllText(LoginPath));
    }

    /// <summary>验证启动前写入及自动回滚均失败时保留可重试事务。</summary>
    [Fact]
    public async Task DoubleRegistryFailureRetainsRecoverableTransaction()
    {
        platform.RemainingRegistryFailures = 2;
        var service = new SteamAccountService(statePath, platform);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => service.SwitchAndLaunchAsync(steamPath, Selected));
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(Original, File.ReadAllText(LoginPath));
        await service.RestoreLatestAsync(steamPath);
        Assert.Equal("first", platform.Values.Single(value => value.Name == "AutoLoginUser").Text);
    }

    /// <summary>验证无清单的中断备份目录被忽略，损坏或不匹配清单则保留现场并拒绝。</summary>
    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"Id\":\"wrong\"}")]
    public async Task InvalidManifestRejectsOperationAndPreservesConfiguration(string content)
    {
        string orphan = Path.Combine(statePath, "steam-login-backups", "orphan");
        string invalid = Path.Combine(statePath, "steam-login-backups", "invalid");
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "manifest.json"), content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected));
        Assert.Equal(Original, File.ReadAllText(LoginPath));
        Assert.Equal(content, File.ReadAllText(Path.Combine(invalid, "manifest.json")));
    }

    /// <summary>验证无清单的中断目录不会阻断下一次正常切换。</summary>
    [Fact]
    public async Task SwitchIgnoresOrphanBackupDirectoryWithoutManifest()
    {
        Directory.CreateDirectory(Path.Combine(statePath, "steam-login-backups", "orphan"));
        await new SteamAccountService(statePath, platform).SwitchAndLaunchAsync(steamPath, Selected);
        Assert.Equal("second", platform.Values.Single(value => value.Name == "AutoLoginUser").Text);
        Assert.Single(Directory.GetFiles(statePath, "manifest.json", SearchOption.AllDirectories));
    }

    /// <summary>验证备份文件被删除时拒绝还原，当前文件保持完整。</summary>
    [Fact]
    public async Task RestoreRejectsMissingBackupFileWithoutMutation()
    {
        var service = new SteamAccountService(statePath, platform);
        await service.SwitchAndLaunchAsync(steamPath, Selected);
        string switched = File.ReadAllText(LoginPath);
        File.Delete(Assert.Single(Directory.GetFiles(statePath, "loginusers.vdf", SearchOption.AllDirectories)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreLatestAsync(steamPath));
        Assert.Equal(switched, File.ReadAllText(LoginPath));
    }

    /// <summary>验证默认构造、路径和空账号边界在任何系统变更前被处理。</summary>
    [Fact]
    public async Task ArgumentValidationPrecedesSystemMutation()
    {
        _ = new SteamAccountService(statePath);
        Assert.Throws<ArgumentException>(() => new SteamAccountService(" "));
        var service = new SteamAccountService(statePath, platform);
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SwitchAndLaunchAsync(steamPath, null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAndLaunchAsync(Path.Combine(root, "missing"), Selected));
        Assert.Equal(Original, File.ReadAllText(LoginPath));
    }

    /// <summary>清理测试独占目录中的所有隔离文件。</summary>
    public void Dispose() => Directory.Delete(root, true);

    /// <summary>仅替代进程和注册表，不替代事务文件的测试系统边界。</summary>
    private sealed class FixturePlatform : ISteamPlatform
    {
        /// <summary>控制隔离测试中的游戏运行状态。</summary>
        public bool GameRunning { get; set; }
        /// <summary>注入正常退出 Steam 的失败。</summary>
        public Exception? ShutdownError { get; set; }
        /// <summary>注入 Steam 启动失败。</summary>
        public Exception? LaunchError { get; set; }
        /// <summary>在下一次注册表写入时中断，用于检验部分还原恢复。</summary>
        public bool FailNextRegistryWrite { get; set; }
        /// <summary>还需注入的连续注册表写入失败次数。</summary>
        public int RemainingRegistryFailures { get; set; }
        /// <summary>模拟启动成功之后出现的系统状态变化。</summary>
        public Action? AfterLaunch { get; set; }
        /// <summary>隔离测试中保存的注册表快照。</summary>
        public List<SteamRegistryValue> Values { get; set; } = [new() { Name = "AutoLoginUser", Exists = true, Text = "first" }, new() { Name = "RememberPassword", Exists = true, Kind = RegistryValueKind.DWord, Number = 0 }];
        /// <summary>返回测试指定的游戏运行状态。</summary>
        public bool IsGameRunning() => GameRunning;
        /// <summary>模拟正常退出过程，不接触真实 Steam。</summary>
        public Task ShutdownSteamAsync(string steamPath, CancellationToken cancellationToken) => ShutdownError is null ? Task.CompletedTask : Task.FromException(ShutdownError);
        /// <summary>返回隔离环境中的注册表快照。</summary>
        public IReadOnlyList<SteamRegistryValue> ReadLoginRegistry() => Values;
        /// <summary>在隔离内存中应用注册表快照。</summary>
        public void WriteLoginRegistry(IReadOnlyList<SteamRegistryValue> values)
        {
            if (FailNextRegistryWrite)
            {
                FailNextRegistryWrite = false;
                throw new IOException("fixture registry write interrupted");
            }
            if (RemainingRegistryFailures > 0)
            {
                RemainingRegistryFailures--;
                throw new IOException("fixture repeated registry failure");
            }
            Values = values.ToList();
        }
        /// <summary>模拟游戏启动过程，不运行任何真实程序。</summary>
        public Task LaunchGameAsync(string steamPath, CancellationToken cancellationToken)
        {
            if (LaunchError is not null)
                return Task.FromException(LaunchError);
            AfterLaunch?.Invoke();
            return Task.CompletedTask;
        }
    }
}
