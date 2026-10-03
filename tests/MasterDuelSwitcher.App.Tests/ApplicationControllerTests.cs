using System.IO;
using MasterDuelSwitcher.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>独立验证启动组合、末级错误提示及退出释放，不创建 WPF Application。</summary>
public sealed class ApplicationControllerTests
{
    /// <summary>正常启动传递原始参数并释放创建的应用会话。</summary>
    [Fact]
    public async Task ControllerStartsAndDisposesInjectedSession()
    {
        var session = new TestSession();
        var bootstrapper = new TestBootstrapper(session);
        var errors = new TestErrors();
        var controller = new ApplicationController(bootstrapper, errors);
        controller.Dispose();
        await controller.StartAsync(["--state-dir", @"C:\Fixture\State"]);
        Assert.Equal(new[] { "--state-dir", @"C:\Fixture\State" }, bootstrapper.Arguments);
        Assert.Equal(1, session.StartCount);
        Assert.Empty(errors.Notices);
        await controller.HandleUnhandledErrorAsync();
        Assert.Equal("操作中断", Assert.Single(errors.Notices).Title);
        controller.Dispose();
        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>构建和初始化中的失败均显示统一启动错误并使用退出码一。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ControllerReportsFailuresFromBuildingOrStartingSession(bool failBuild)
    {
        var session = new TestSession { StartError = failBuild ? null : new IOException("fixture") };
        var bootstrapper = new TestBootstrapper(session) { BuildError = failBuild ? new IOException("fixture") : null };
        var errors = new TestErrors();
        using var controller = new ApplicationController(bootstrapper, errors, NullLogger<ApplicationController>.Instance);
        await controller.StartAsync([]);
        Assert.Equal("Master Duel Switcher", Assert.Single(errors.Notices).Title);
        Assert.Contains("启动失败", errors.Notices[0].Content);
        Assert.Equal(1, Assert.Single(errors.ExitCodes));
    }

    /// <summary>生产组合会话的构造和释放不启动 Steam 或读取真实账号。</summary>
    [Fact]
    public void DesktopBootstrapperCreatesDisposableIsolatedSessionWithoutStarting()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher-session-" + Guid.NewGuid().ToString("N"));
        var bootstrapper = new DesktopApplicationBootstrapper();
        using var session = bootstrapper.Build(["--state-dir", stateDirectory]);
        Assert.IsType<DesktopApplicationSession>(session);
        Assert.False(File.Exists(Path.Combine(stateDirectory, "accounts.db")));
    }

    /// <summary>测试控制器所持有的轻量应用会话。</summary>
    private sealed class TestSession : IApplicationSession
    {
        /// <summary>控制器测试的无输出日志实现。</summary>
        public ILogger<ApplicationController> Logger => NullLogger<ApplicationController>.Instance;
        /// <summary>会话开始时返回的可注入错误。</summary>
        public Exception? StartError { get; init; }
        /// <summary>开始调用数量。</summary>
        public int StartCount { get; private set; }
        /// <summary>释放调用数量。</summary>
        public int DisposeCount { get; private set; }
        /// <summary>记录开始并返回确定的完成或失败任务。</summary>
        public Task StartAsync()
        {
            StartCount++;
            return StartError is null ? Task.CompletedTask : Task.FromException(StartError);
        }
        /// <summary>记录会话释放。</summary>
        public void Dispose() => DisposeCount++;
    }

    /// <summary>传递参数并模拟组合失败的启动服务。</summary>
    private sealed class TestBootstrapper : IApplicationBootstrapper
    {
        /// <summary>成功时返回的应用会话。</summary>
        private readonly IApplicationSession _session;
        /// <summary>构建时返回的可注入错误。</summary>
        public Exception? BuildError { get; init; }
        /// <summary>最近收到的启动参数。</summary>
        public string[] Arguments { get; private set; } = [];
        /// <summary>绑定需要返回的会话。</summary>
        public TestBootstrapper(IApplicationSession session) => _session = session;
        /// <summary>记录原始参数并提供成功或失败的组合结果。</summary>
        public IApplicationSession Build(string[] args)
        {
            Arguments = args;
            if (BuildError is not null) throw BuildError;
            return _session;
        }
    }

    /// <summary>记录通知和退出意图，不显示窗口。</summary>
    private sealed class TestErrors : IApplicationErrorHandler
    {
        /// <summary>控制器请求显示的错误通知。</summary>
        public List<(string Title, string Content)> Notices { get; } = [];
        /// <summary>控制器请求使用的退出码。</summary>
        public List<int> ExitCodes { get; } = [];
        /// <summary>记录末级错误通知。</summary>
        public Task ShowErrorAsync(string title, string content)
        {
            Notices.Add((title, content));
            return Task.CompletedTask;
        }
        /// <summary>记录退出意图。</summary>
        public void Shutdown(int exitCode) => ExitCodes.Add(exitCode);
    }
}
