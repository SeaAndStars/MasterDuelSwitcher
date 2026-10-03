using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Win32;
using System.Diagnostics;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证隔离进程边界下的退出等待、启动参数及临时注册表存储。</summary>
public sealed class SteamPlatformTests : IDisposable
{
    /// <summary>仅供本次测试使用的 HKCU 注册表键，不触及 Steam 用户键。</summary>
    private readonly string registryPath = @"Software\MasterDuelSwitcherTests\" + Guid.NewGuid().ToString("N");

    /// <summary>验证正常退出使用参数列表，并且超时后保留原进程。</summary>
    [Fact]
    public async Task ShutdownUsesArgumentListAndStopsAtBoundedTimeout()
    {
        var runtime = new FixtureRuntime { SteamRunning = true };
        var platform = new WindowsSteamPlatform(runtime, new SteamRegistryStore(registryPath), TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => platform.ShutdownSteamAsync("X:\\FixtureSteam", CancellationToken.None));

        Assert.Equal("X:\\FixtureSteam\\steam.exe", runtime.LastStart!.FileName);
        Assert.Equal(["-shutdown"], runtime.LastStart.ArgumentList.ToArray());
        Assert.True(runtime.SteamRunning);
    }

    /// <summary>验证无 Steam 进程时不发送退出请求。</summary>
    [Fact]
    public async Task ShutdownDoesNotStartProcessWhenSteamIsAbsent()
    {
        var runtime = new FixtureRuntime();

        await new WindowsSteamPlatform(runtime).ShutdownSteamAsync("X:\\FixtureSteam", CancellationToken.None);

        Assert.Null(runtime.LastStart);
    }

    /// <summary>验证进程结束后正常返回，并释放退出请求句柄。</summary>
    [Fact]
    public async Task ShutdownReturnsAfterSteamExitsAndDisposesRequest()
    {
        var runtime = new FixtureRuntime { SteamRunning = true, ExitOnDelay = true };

        await new WindowsSteamPlatform(runtime).ShutdownSteamAsync("X:\\FixtureSteam", CancellationToken.None);

        Assert.False(runtime.SteamRunning);
        Assert.True(runtime.Started.Disposed);
    }

    /// <summary>验证启动 Master Duel 始终传递正确 AppID，不拼接命令字符串。</summary>
    [Fact]
    public async Task LaunchUsesMasterDuelArgumentList()
    {
        var runtime = new FixtureRuntime();

        await new WindowsSteamPlatform(runtime).LaunchGameAsync("X:\\FixtureSteam", CancellationToken.None);

        Assert.Equal(["-applaunch", "1449850"], runtime.LastStart!.ArgumentList.ToArray());
        Assert.False(runtime.LastStart.UseShellExecute);
        Assert.True(runtime.Started.Disposed);
    }

    /// <summary>验证 Steam 启动进程立即失败会报告异常。</summary>
    [Fact]
    public async Task LaunchRejectsImmediateNonzeroExit()
    {
        var runtime = new FixtureRuntime();
        runtime.Started.HasExited = true;
        runtime.Started.ExitCode = 7;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsSteamPlatform(runtime).LaunchGameAsync("X:\\FixtureSteam", CancellationToken.None));
    }

    /// <summary>验证 Steam 快速退出且返回成功时不误报启动失败。</summary>
    [Fact]
    public async Task LaunchAcceptsSuccessfulImmediateExit()
    {
        var runtime = new FixtureRuntime();
        runtime.Started.HasExited = true;
        await new WindowsSteamPlatform(runtime).LaunchGameAsync("X:\\FixtureSteam", CancellationToken.None);
        Assert.True(runtime.Started.Disposed);
    }

    /// <summary>验证系统未返回启动句柄时明确报告请求失败。</summary>
    [Fact]
    public async Task MissingProcessHandleRejectsLaunchAndShutdown()
    {
        var runtime = new FixtureRuntime { ReturnNullProcess = true, SteamRunning = true };
        var platform = new WindowsSteamPlatform(runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.LaunchGameAsync("X:\\FixtureSteam", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.ShutdownSteamAsync("X:\\FixtureSteam", CancellationToken.None));
    }

    /// <summary>验证取消请求在退出等待中被执行，游戏运行检测使用实际边界状态。</summary>
    [Fact]
    public async Task CancellationAndGameStateUseInjectedBoundary()
    {
        var runtime = new FixtureRuntime { SteamRunning = true, GameRunning = true };
        var platform = new WindowsSteamPlatform(runtime);
        Assert.True(platform.IsGameRunning());
        runtime.GameRunning = false;
        Assert.False(platform.IsGameRunning());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => platform.ShutdownSteamAsync("X:\\FixtureSteam", cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => platform.LaunchGameAsync("X:\\FixtureSteam", cancellation.Token));
    }

    /// <summary>验证实际临时 HKCU 键可完整读写多种值类型及删除状态。</summary>
    [Fact]
    public void RegistryStorePreservesTypesAndMissingValues()
    {
        var store = new SteamRegistryStore(registryPath);
        Assert.All(store.Read(), value => Assert.False(value.Exists));
        SteamRegistryValue[] values = [new() { Name = "AutoLoginUser", Exists = true, Kind = RegistryValueKind.ExpandString, Text = "%FIXTURE%" }, new() { Name = "RememberPassword", Exists = true, Kind = RegistryValueKind.DWord, Number = 0 }];
        store.Write(values);

        var snapshot = store.Read();

        Assert.Equal("%FIXTURE%", snapshot.Single(value => value.Name == "AutoLoginUser").Text);
        Assert.Equal(RegistryValueKind.ExpandString, snapshot.Single(value => value.Name == "AutoLoginUser").Kind);
        Assert.Equal(0, snapshot.Single(value => value.Name == "RememberPassword").Number);
        store.Write([new() { Name = "AutoLoginUser", Exists = false }, new() { Name = "RememberPassword", Exists = true, Kind = RegistryValueKind.QWord, Number = 1234567890123 }]);
        Assert.False(store.Read().Single(value => value.Name == "AutoLoginUser").Exists);
        Assert.Equal(1234567890123, store.Read().Single(value => value.Name == "RememberPassword").Number);
    }

    /// <summary>验证平台适配器通过专属测试键保留字符串、二进制及多字符串数据。</summary>
    [Fact]
    public void RegistryPlatformPreservesStringsBinaryAndMultiString()
    {
        var platform = new WindowsSteamPlatform(new FixtureRuntime(), new SteamRegistryStore(registryPath));
        platform.WriteLoginRegistry([new() { Name = "AutoLoginUser", Exists = true, Kind = RegistryValueKind.String, Text = "fixture" }, new() { Name = "RememberPassword", Exists = true, Kind = RegistryValueKind.Binary, Bytes = [1, 2, 3] }]);
        Assert.Equal("fixture", platform.ReadLoginRegistry().Single(value => value.Name == "AutoLoginUser").Text);
        Assert.Equal([1, 2, 3], platform.ReadLoginRegistry().Single(value => value.Name == "RememberPassword").Bytes!);
        platform.WriteLoginRegistry([new() { Name = "AutoLoginUser", Exists = true, Kind = RegistryValueKind.MultiString, Texts = ["a", "b"] }, new() { Name = "RememberPassword", Exists = false }]);
        Assert.Equal(["a", "b"], platform.ReadLoginRegistry().Single(value => value.Name == "AutoLoginUser").Texts!);
        Assert.False(platform.ReadLoginRegistry().Single(value => value.Name == "RememberPassword").Exists);
        Assert.Throws<ArgumentException>(() => new SteamRegistryStore(" "));
    }

    /// <summary>验证真实系统边界仅启动无害的临时命令并正确释放退出句柄。</summary>
    [Fact]
    public async Task SystemRuntimeReadsProcessStateAndStartsHarmlessExitCommand()
    {
        var runtime = new SystemSteamProcessRuntime();
        using var current = Process.GetCurrentProcess();
        Assert.True(runtime.HasProcess(current.ProcessName));
        Assert.False(runtime.HasProcess("MasterDuelMissing" + Guid.NewGuid().ToString("N")));
        Assert.InRange(runtime.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-2), DateTimeOffset.UtcNow.AddSeconds(2));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec")!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("exit");
        start.ArgumentList.Add("0");
        using var process = runtime.Start(start)!;
        for (int count = 0; !process.HasExited && count < 100; count++)
            await runtime.DelayAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
        Assert.Null(new SystemSteamProcessRuntime(_ => null).Start(start));
        _ = new WindowsSteamPlatform();
    }

    /// <summary>验证不完整、额外和重复注册表字段在写测试键之前被拒绝。</summary>
    [Fact]
    public void RegistryStoreRejectsInvalidFieldListsBeforeWriting()
    {
        var store = new SteamRegistryStore(registryPath);
        Assert.Throws<InvalidOperationException>(() => store.Write(null!));
        Assert.Throws<InvalidOperationException>(() => store.Write([]));
        Assert.Throws<InvalidOperationException>(() => store.Write([new() { Name = "AutoLoginUser" }, new() { Name = "AutoLoginUser" }]));
        Assert.Throws<InvalidOperationException>(() => store.Write([new() { Name = "AutoLoginUser" }, new() { Name = "unexpected" }]));
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        Assert.Null(key);
    }

    /// <summary>验证每种受支持注册表类型都拒绝缺失数据，DWORD 拒绝超范围数据。</summary>
    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.QWord)]
    [InlineData(RegistryValueKind.MultiString)]
    [InlineData(RegistryValueKind.Binary)]
    [InlineData(RegistryValueKind.Unknown)]
    public void RegistryStoreRejectsMissingTypedData(RegistryValueKind kind)
    {
        var store = new SteamRegistryStore(registryPath);
        Assert.Throws<InvalidOperationException>(() => store.Write([new() { Name = "AutoLoginUser", Exists = true, Kind = kind }, new() { Name = "RememberPassword" }]));
    }

    /// <summary>验证 DWORD 类型范围两侧的越界值均被拒绝。</summary>
    [Theory]
    [InlineData((long)int.MinValue - 1)]
    [InlineData((long)int.MaxValue + 1)]
    public void RegistryStoreRejectsOutOfRangeDword(long number)
    {
        Assert.Throws<InvalidOperationException>(() => new SteamRegistryStore(registryPath).Write([new() { Name = "AutoLoginUser", Exists = true, Kind = RegistryValueKind.DWord, Number = number }, new() { Name = "RememberPassword" }]));
    }

    /// <summary>删除本次测试创建的独占注册表键。</summary>
    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(registryPath, false);

    /// <summary>隔离进程和时间推进，不启动或退出任何实际 Steam。</summary>
    private sealed class FixtureRuntime : ISteamProcessRuntime
    {
        /// <summary>模拟 Steam 进程的存在状态。</summary>
        public bool SteamRunning { get; set; }
        /// <summary>等待时是否模拟 Steam 正常退出。</summary>
        public bool ExitOnDelay { get; set; }
        /// <summary>模拟 Master Duel 进程是否运行。</summary>
        public bool GameRunning { get; set; }
        /// <summary>模拟操作系统启动请求未返回进程句柄。</summary>
        public bool ReturnNullProcess { get; set; }
        /// <summary>捕获实际提交给进程启动边界的参数。</summary>
        public ProcessStartInfo? LastStart { get; private set; }
        /// <summary>本次启动请求对应的隔离句柄。</summary>
        public FixtureProcess Started { get; } = new();
        /// <summary>通过等待推进的隔离时钟。</summary>
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
        /// <summary>返回隔离环境的 Steam 进程状态。</summary>
        public bool HasProcess(string processName) => processName == "steam" ? SteamRunning : GameRunning;
        /// <summary>保存启动参数并返回隔离进程句柄。</summary>
        public ISteamStartedProcess? Start(ProcessStartInfo startInfo)
        {
            LastStart = startInfo;
            return ReturnNullProcess ? null : Started;
        }
        /// <summary>在隔离时钟中等待并模拟进程结束。</summary>
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            if (ExitOnDelay)
                SteamRunning = false;
            return Task.CompletedTask;
        }
    }

    /// <summary>供启动和退出分支验证使用的隔离进程状态。</summary>
    private sealed class FixtureProcess : ISteamStartedProcess
    {
        /// <summary>模拟进程是否已退出。</summary>
        public bool HasExited { get; set; }
        /// <summary>模拟已退出进程的返回码。</summary>
        public int ExitCode { get; set; }
        /// <summary>记录请求句柄是否被释放。</summary>
        public bool Disposed { get; private set; }
        /// <summary>释放隔离请求句柄。</summary>
        public void Dispose() => Disposed = true;
    }
}
