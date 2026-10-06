using System.Collections.Concurrent;
using System.IO;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>验证免费开包页面的互斥、进度、停止与处置，全部自动操作均由可控内存边界替代。</summary>
public sealed class FreePacksPageViewModelTests
{
    /// <summary>构造保持等待状态，展示系统日志目录且不读取存储或启动自动操作。</summary>
    [Fact]
    public void ConstructionHasNoExternalSideEffects()
    {
        using var fixture = new Fixture();
        Assert.Same(fixture.Workspace, fixture.ViewModel.Workspace);
        Assert.Equal("等待开始", fixture.ViewModel.Stage);
        Assert.Equal(Path.Combine("C:\\Fixture\\Data", "logs"), fixture.ViewModel.LogDirectory);
        Assert.Equal(0, fixture.ViewModel.ScannedPacks);
        Assert.Equal(0, fixture.ViewModel.OpenedPacks);
        Assert.False(fixture.ViewModel.IsRunning);
        Assert.True(fixture.ViewModel.RunningTask.IsCompletedSuccessfully);
        Assert.True(fixture.ViewModel.StartCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.StopCommand.CanExecute(null));
        Assert.Equal(0, fixture.Store.LoadCount);
        Assert.Equal(0, fixture.Discovery.DiscoverCount);
        Assert.Empty(fixture.Automation.Runs);
    }

    /// <summary>服务正常返回后展示最终统计和原因，并释放工作区互斥状态。</summary>
    [Fact]
    public async Task CompletedRunPublishesFinalResult()
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Complete(7, 3, "免费包处理完成");
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, fixture.ViewModel.ScannedPacks);
        Assert.Equal(3, fixture.ViewModel.OpenedPacks);
        Assert.Equal("免费包处理完成", fixture.ViewModel.Stage);
        Assert.False(fixture.ViewModel.IsRunning);
        Assert.True(fixture.Workspace.IsReady);
        Assert.True(fixture.ViewModel.RunningTask.IsCompletedSuccessfully);
        Assert.False(fixture.ViewModel.StopCommand.CanExecute(null));
        Assert.Empty(fixture.Interaction.Notices);
        Assert.Contains(fixture.Workspace.Logs, text => text.Contains("免费包处理完成", StringComparison.Ordinal));
    }

    /// <summary>同步完成的服务也应留下已完成的完整运行任务，避免错误保存旧任务。</summary>
    [Fact]
    public async Task SynchronousCompletionLeavesCompletedRunningTask()
    {
        using var fixture = new Fixture();
        fixture.Automation.QueueRun().Complete(2, 1, "同步完成");
        await fixture.ViewModel.StartCommand.ExecuteAsync();
        Assert.Equal("同步完成", fixture.ViewModel.Stage);
        Assert.Equal(1, fixture.ViewModel.OpenedPacks);
        Assert.True(fixture.ViewModel.RunningTask.IsCompletedSuccessfully);
        Assert.False(fixture.Workspace.IsBusy);
    }

    /// <summary>进行中的扫描进度应同时更新阶段、统计和属性通知。</summary>
    [Fact]
    public Task ProgressUpdatesVisibleStateAndNotifications() => RunOnUiContextAsync(async () =>
    {
        using var fixture = new Fixture();
        var changes = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Report("正在识别免费包", 5, 2);
        await Task.Yield();
        Assert.Equal("正在识别免费包", fixture.ViewModel.Stage);
        Assert.Equal(5, fixture.ViewModel.ScannedPacks);
        Assert.Equal(2, fixture.ViewModel.OpenedPacks);
        Assert.Contains(nameof(FreePacksPageViewModel.Stage), changes);
        Assert.Contains(nameof(FreePacksPageViewModel.ScannedPacks), changes);
        Assert.Contains(nameof(FreePacksPageViewModel.OpenedPacks), changes);
        run.Complete(5, 2, "完成");
        await start;
    });

    /// <summary>后台线程报告进度时，模型通知应回到启动命令捕获的界面上下文和线程。</summary>
    [Fact]
    public Task BackgroundProgressUsesCapturedUiContext() => RunOnUiContextAsync(async () =>
    {
        using var fixture = new Fixture();
        var uiThread = Environment.CurrentManagedThreadId;
        var uiContext = SynchronizationContext.Current;
        var notifications = new List<(int Thread, SynchronizationContext? Context)>();
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FreePacksPageViewModel.ScannedPacks))
                notifications.Add((Environment.CurrentManagedThreadId, SynchronizationContext.Current));
        };
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        await Task.Run(() => run.Report("后台识别完成", 4, 1));
        await Task.Yield();
        Assert.Equal(4, fixture.ViewModel.ScannedPacks);
        var notification = Assert.Single(notifications);
        Assert.Equal(uiThread, notification.Thread);
        Assert.Same(uiContext, notification.Context);
        run.Complete(4, 1, "完成");
        await start;
    });

    /// <summary>同样的连续进度不会重复发出未变化字段的通知。</summary>
    [Fact]
    public Task RepeatedProgressDoesNotRepeatUnchangedFieldNotifications() => RunOnUiContextAsync(async () =>
    {
        using var fixture = new Fixture();
        var changes = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Report("识别中", 2, 1);
        await Task.Yield();
        changes.Clear();
        run.Report("识别中", 2, 1);
        await Task.Yield();
        Assert.DoesNotContain(nameof(FreePacksPageViewModel.Stage), changes);
        Assert.DoesNotContain(nameof(FreePacksPageViewModel.ScannedPacks), changes);
        Assert.DoesNotContain(nameof(FreePacksPageViewModel.OpenedPacks), changes);
        run.Complete(2, 1, "完成");
        await start;
    });

    /// <summary>连续点击启动只保留一个运行任务，同时禁用刷新和其他业务命令。</summary>
    [Fact]
    public async Task DuplicateStartDoesNotRunSecondAutomation()
    {
        using var fixture = new Fixture();
        var otherCalls = 0;
        var other = fixture.Workspace.CreateCommand("OtherOperation", _ => { otherCalls++; return Task.CompletedTask; });
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        var running = fixture.ViewModel.RunningTask;
        Assert.True(fixture.ViewModel.IsRunning);
        Assert.True(fixture.Workspace.IsBusy);
        Assert.False(fixture.ViewModel.StartCommand.CanExecute(null));
        Assert.False(fixture.Workspace.RefreshCommand.CanExecute(null));
        Assert.False(other.CanExecute(null));
        await fixture.ViewModel.StartCommand.ExecuteAsync();
        await fixture.Workspace.RefreshCommand.ExecuteAsync();
        await other.ExecuteAsync();
        Assert.Single(fixture.Automation.Runs);
        Assert.Same(running, fixture.ViewModel.RunningTask);
        Assert.Equal(0, otherCalls);
        Assert.Equal(0, fixture.Discovery.DiscoverCount);
        run.Complete(1, 1, "完成");
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(other.CanExecute(null));
        Assert.True(fixture.Workspace.RefreshCommand.CanExecute(null));
    }

    /// <summary>已有全局业务运行时，免费开包启动应保持无副作用。</summary>
    [Fact]
    public async Task ExistingWorkspaceOperationBlocksStart()
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = fixture.Workspace.RunOperationAsync("已有事务", () => release.Task);
        try
        {
            await fixture.ViewModel.StartCommand.ExecuteAsync();
            Assert.Empty(fixture.Automation.Runs);
            Assert.False(fixture.ViewModel.IsRunning);
            Assert.False(fixture.ViewModel.StopCommand.CanExecute(null));
        }
        finally { release.TrySetResult(true); await operation; }
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>停止按钮在忙碌中保持可用，并等待自动服务完成清理后才释放全局忙碌。</summary>
    [Fact]
    public async Task StopCommandWaitsForActualCleanup()
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        Assert.True(fixture.ViewModel.StopCommand.CanExecute(null));
        var stop = fixture.ViewModel.StopCommand.ExecuteAsync();
        Assert.True(run.CancellationToken.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        Assert.False(fixture.ViewModel.RunningTask.IsCompleted);
        Assert.True(fixture.ViewModel.IsRunning);
        Assert.True(fixture.Workspace.IsBusy);
        run.Complete(6, 2, "用户停止", true);
        await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.ViewModel.IsRunning);
        Assert.True(fixture.Workspace.IsReady);
        Assert.False(fixture.ViewModel.StopCommand.CanExecute(null));
    }

    /// <summary>供关闭路径调用的停止请求必须等到完整运行任务结束。</summary>
    [Fact]
    public async Task RequestStopAsyncAwaitsFullRunningTask()
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        var stop = fixture.ViewModel.RequestStopAsync();
        Assert.True(run.CancellationToken.CanBeCanceled);
        Assert.True(run.CancellationToken.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        run.Complete(0, 0, "已停止", true);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.ViewModel.RunningTask.IsCompleted);
        Assert.False(fixture.Workspace.IsBusy);
        await start;
    }

    /// <summary>空闲时停止和等待停止均为无操作，不启动服务也不显示错误。</summary>
    [Fact]
    public async Task IdleStopIsHarmless()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Stop();
        await fixture.ViewModel.StopCommand.ExecuteAsync();
        await fixture.ViewModel.RequestStopAsync();
        Assert.Empty(fixture.Automation.Runs);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.Equal("等待开始", fixture.ViewModel.Stage);
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>服务自行响应 F8 后返回取消结果，应保留最终统计和原因且没有错误通知。</summary>
    [Fact]
    public async Task CancelledServiceResultPublishesReasonAndCounts()
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Complete(9, 4, "F8停止", true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("F8停止", fixture.ViewModel.Stage);
        Assert.Equal(9, fixture.ViewModel.ScannedPacks);
        Assert.Equal(4, fixture.ViewModel.OpenedPacks);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.DoesNotContain(fixture.WorkspaceLogger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(fixture.Workspace.Logs, text => text.Contains("F8停止", StringComparison.Ordinal));
    }

    /// <summary>本轮取消令牌已被请求时，服务取消异常转换为正常停止。</summary>
    [Fact]
    public async Task RequestedCancellationExceptionDoesNotDisplayError()
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        fixture.ViewModel.Stop();
        run.Fail(new OperationCanceledException(run.CancellationToken));
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("已停止", fixture.ViewModel.Stage);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.DoesNotContain(fixture.WorkspaceLogger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.False(fixture.ViewModel.IsRunning);
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>没有收到本轮停止请求的取消异常仍按错误处理，避免掩盖服务异常。</summary>
    [Fact]
    public async Task UnexpectedCancellationExceptionIsReportedAsError()
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var exception = new OperationCanceledException("外部取消");
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Fail(exception);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Interaction.Notices);
        Assert.Contains(fixture.WorkspaceLogger.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, exception));
        Assert.False(fixture.ViewModel.IsRunning);
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>自动操作错误应由共享工作区通知并记录完整异常，结束后可再次启动。</summary>
    [Fact]
    public async Task AutomationErrorUsesWorkspaceNoticeAndExceptionLog()
    {
        using var fixture = new Fixture(true);
        var run = fixture.Automation.QueueRun();
        var exception = new InvalidOperationException("识别窗口已失效");
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Fail(exception);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("识别窗口已失效", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Contains(fixture.WorkspaceLogger.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, exception));
        Assert.False(fixture.ViewModel.IsRunning);
        Assert.True(fixture.ViewModel.StartCommand.CanExecute(null));
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>再次启动应重置旧统计，并为新轮创建独立取消令牌。</summary>
    [Fact]
    public async Task RestartResetsCountsAndCreatesNewCancellationToken()
    {
        using var fixture = new Fixture();
        var first = fixture.Automation.QueueRun();
        var firstStart = fixture.ViewModel.StartCommand.ExecuteAsync();
        first.Complete(8, 3, "第一轮完成");
        await firstStart.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Automation.QueueRun();
        var secondStart = fixture.ViewModel.StartCommand.ExecuteAsync();
        Assert.Equal(0, fixture.ViewModel.ScannedPacks);
        Assert.Equal(0, fixture.ViewModel.OpenedPacks);
        Assert.NotEqual("第一轮完成", fixture.ViewModel.Stage);
        Assert.NotEqual(first.CancellationToken, second.CancellationToken);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        second.Complete(1, 0, "第二轮完成");
        await secondStart.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Automation.Runs.Count);
    }

    /// <summary>一轮已结束后到达的进度不得覆盖最终结果。</summary>
    [Fact]
    public Task CompletedSessionIgnoresLateProgress() => RunOnUiContextAsync(async () =>
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Complete(3, 1, "最终完成");
        await start;
        run.Report("迟到进度", 99, 98);
        await Task.Yield();
        Assert.Equal("最终完成", fixture.ViewModel.Stage);
        Assert.Equal(3, fixture.ViewModel.ScannedPacks);
        Assert.Equal(1, fixture.ViewModel.OpenedPacks);
    });

    /// <summary>旧轮的进度在新轮运行期间到达时，不得污染新轮统计和阶段。</summary>
    [Fact]
    public Task PreviousSessionProgressCannotOverwriteCurrentSession() => RunOnUiContextAsync(async () =>
    {
        using var fixture = new Fixture();
        var first = fixture.Automation.QueueRun();
        var firstStart = fixture.ViewModel.StartCommand.ExecuteAsync();
        first.Complete(5, 2, "第一轮完成");
        await firstStart;
        var second = fixture.Automation.QueueRun();
        var secondStart = fixture.ViewModel.StartCommand.ExecuteAsync();
        second.Report("第二轮扫描", 4, 1);
        await Task.Yield();
        first.Report("旧轮迟到", 99, 98);
        await Task.Yield();
        Assert.Equal("第二轮扫描", fixture.ViewModel.Stage);
        Assert.Equal(4, fixture.ViewModel.ScannedPacks);
        Assert.Equal(1, fixture.ViewModel.OpenedPacks);
        second.Complete(4, 1, "第二轮完成");
        await secondStart;
    });

    /// <summary>运行中处置立即请求停止，但服务实际清理完成前仍保留互斥。</summary>
    [Fact]
    public async Task DisposeCancelsActiveRunAndPreventsRestart()
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        fixture.ViewModel.Dispose();
        fixture.ViewModel.Dispose();
        Assert.True(run.CancellationToken.IsCancellationRequested);
        Assert.True(fixture.Workspace.IsBusy);
        run.Fail(new OperationCanceledException(run.CancellationToken));
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ViewModel.StartCommand.ExecuteAsync();
        Assert.Single(fixture.Automation.Runs);
        Assert.False(fixture.Workspace.IsBusy);
        Assert.Empty(fixture.Interaction.Notices);
    }

    /// <summary>空闲时处置可重复调用，后续启动保持无自动操作副作用。</summary>
    [Fact]
    public async Task DisposedIdleModelDoesNotStartAutomation()
    {
        using var fixture = new Fixture();
        fixture.ViewModel.Dispose();
        fixture.ViewModel.Dispose();
        fixture.ViewModel.Stop();
        await fixture.ViewModel.RequestStopAsync();
        await fixture.ViewModel.StartCommand.ExecuteAsync();
        Assert.Empty(fixture.Automation.Runs);
        Assert.False(fixture.ViewModel.IsRunning);
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>运行结束后再次请求停止不会访问已释放的取消源或显示错误。</summary>
    [Fact]
    public async Task StopAfterCompletionDoesNotUseDisposedCancellationSource()
    {
        using var fixture = new Fixture();
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Complete(1, 1, "完成");
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ViewModel.Stop();
        await fixture.ViewModel.RequestStopAsync();
        Assert.Empty(fixture.Interaction.Notices);
        Assert.False(fixture.ViewModel.StopCommand.CanExecute(null));
    }

    /// <summary>启动和结束应通知运行状态及命令可用性，使遮罩停止按钮及时更新。</summary>
    [Fact]
    public async Task RunStateAndCommandAvailabilityAreNotified()
    {
        using var fixture = new Fixture();
        var runningStates = new List<bool>();
        var startEvents = 0;
        var stopEvents = 0;
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FreePacksPageViewModel.IsRunning)) runningStates.Add(fixture.ViewModel.IsRunning);
        };
        fixture.ViewModel.StartCommand.CanExecuteChanged += (_, _) => startEvents++;
        fixture.ViewModel.StopCommand.CanExecuteChanged += (_, _) => stopEvents++;
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        Assert.True(fixture.ViewModel.StopCommand.CanExecute(null));
        run.Complete(1, 1, "完成");
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { true, false }, runningStates);
        Assert.True(startEvents >= 2);
        Assert.True(stopEvents >= 2);
        Assert.False(fixture.ViewModel.StopCommand.CanExecute(null));
    }

    /// <summary>显式日志注入应接收运行记录，不依赖默认空日志分支。</summary>
    [Fact]
    public async Task ExplicitLoggerReceivesRunDiagnostics()
    {
        using var fixture = new Fixture(true);
        var run = fixture.Automation.QueueRun();
        var start = fixture.ViewModel.StartCommand.ExecuteAsync();
        run.Complete(0, 0, "F8停止", true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(fixture.ViewModelLogger.Entries);
        Assert.DoesNotContain(fixture.ViewModelLogger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal("F8停止", fixture.ViewModel.Stage);
    }

    /// <summary>在没有 Application 的专用线程泵送真实 SynchronizationContext，验证界面模型进度顺序。</summary>
    private static async Task RunOnUiContextAsync(Func<Task> test)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new QueuedUiContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var operation = test();
                while (!operation.IsCompleted) context.ExecuteNext();
                operation.GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true, Name = "FreePacksPageViewModelTests-Context" };
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>按报告顺序执行进度和异步续体，不创建第二个 WPF Application。</summary>
    private sealed class QueuedUiContext : SynchronizationContext, IDisposable
    {
        /// <summary>线程安全的界面工作队列。</summary>
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        /// <summary>将进度回调或异步续体加入界面队列，测试已结束时忽略残余清理回调。</summary>
        public override void Post(SendOrPostCallback callback, object? state)
        {
            try { _queue.Add((callback, state)); }
            catch (ObjectDisposedException) { }
        }
        /// <summary>在专用界面线程执行下一项回调，并对意外停顿给出明确测试失败。</summary>
        public void ExecuteNext()
        {
            if (!_queue.TryTake(out var work, TimeSpan.FromSeconds(5))) throw new TimeoutException("界面模型测试没有收到预期续体。");
            work.Callback(work.State);
        }
        /// <summary>释放测试持有的队列，不操作任何真实窗口。</summary>
        public void Dispose() => _queue.Dispose();
    }

    /// <summary>组合真实页面模型与真实共享工作区，仅替换磁盘、游戏和用户界面边界。</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>仅记录访问次数的内存偏好存储。</summary>
        public MemoryStore Store { get; } = new();
        /// <summary>不检测真实安装的发现边界。</summary>
        public Discovery Discovery { get; } = new();
        /// <summary>由测试明确推进结果、异常及清理完成的自动操作边界。</summary>
        public Automation Automation { get; } = new();
        /// <summary>记录错误而不打开真实通知窗口的交互边界。</summary>
        public Interaction Interaction { get; } = new();
        /// <summary>收集真实共享工作区记录的异常与阶段。</summary>
        public MemoryLogger<WorkspaceService> WorkspaceLogger { get; } = new();
        /// <summary>收集显式注入的页面运行日志。</summary>
        public MemoryLogger<FreePacksPageViewModel> ViewModelLogger { get; } = new();
        /// <summary>真实全局互斥和错误处理服务。</summary>
        public WorkspaceService Workspace { get; }
        /// <summary>当前被测免费开包页面模型。</summary>
        public FreePacksPageViewModel ViewModel { get; }
        /// <summary>构造全部隔离边界，按要求选择显式或默认页面日志。</summary>
        public Fixture(bool explicitLogger = false)
        {
            Workspace = new WorkspaceService(Store, Discovery, new Resources(), Interaction, new Theme(), WorkspaceLogger);
            ViewModel = explicitLogger
                ? new FreePacksPageViewModel(Workspace, Automation, ViewModelLogger)
                : new FreePacksPageViewModel(Workspace, Automation);
        }
        /// <summary>失败测试也请求取消并结束残余内存任务，避免后台操作遗留。</summary>
        public void Dispose()
        {
            ViewModel.Dispose();
            foreach (var run in Automation.Runs) run.Complete(0, 0, "测试结束", true);
        }
    }

    /// <summary>固定返回内存偏好，不读写 SQLite 或系统数据目录。</summary>
    private sealed class MemoryStore : ISettingsStore
    {
        /// <summary>被测模型展示的固定日志父目录。</summary>
        public string StateDirectory => "C:\\Fixture\\Data";
        /// <summary>工作区实际发出的读取次数。</summary>
        public int LoadCount { get; private set; }
        /// <summary>记录读取并返回完整默认偏好。</summary>
        public AppSettings Load() { LoadCount++; return new AppSettings(); }
        /// <summary>免费开包页不得请求保存用户偏好。</summary>
        public void Save(AppSettings settings) => throw new InvalidOperationException("免费开包页不应保存偏好。");
        /// <summary>隔离刷新不持久化真实账号。</summary>
        public void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts) { }
        /// <summary>返回空账号列表，不读取 Steam 缓存。</summary>
        public IReadOnlyList<SteamAccount> GetAccounts() => [];
    }

    /// <summary>不触及安装或 VDF 文件的发现边界。</summary>
    private sealed class Discovery : ISteamDiscoveryService
    {
        /// <summary>实际安装发现调用次数。</summary>
        public int DiscoverCount { get; private set; }
        /// <summary>记录刷新请求并返回空安装快照。</summary>
        public DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null) { DiscoverCount++; return new DiscoveryResult(); }
        /// <summary>任何测试路径均返回空账号集合。</summary>
        public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath) => [];
    }

    /// <summary>禁止免费开包页面发起资源替换、恢复或修复。</summary>
    private sealed class Resources : IResourceSharingService
    {
        /// <summary>空安装快照没有资源目录。</summary>
        public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath) => [];
        /// <summary>空安装快照没有恢复事务。</summary>
        public IReadOnlyList<ShareBackup> GetBackups(string gamePath) => [];
        /// <summary>意外资源替换立即令测试失败。</summary>
        public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders) => throw new InvalidOperationException("免费开包页不应修改资源。");
        /// <summary>意外资源还原立即令测试失败。</summary>
        public void Restore(string backupId) => throw new InvalidOperationException("免费开包页不应还原资源。");
        /// <summary>意外资源修复立即令测试失败。</summary>
        public ShareBackup? RepairInvalidSharing(string backupId) => throw new InvalidOperationException("免费开包页不应修复资源。");
    }

    /// <summary>保存真实工作区发出的通知，并默认取消所有业务确认。</summary>
    private sealed class Interaction : IUserInteraction
    {
        /// <summary>按调用顺序保存的错误通知标题和正文。</summary>
        public List<(string Title, string Content)> Notices { get; } = [];
        /// <summary>记录通知但不创建窗口或消息框。</summary>
        public Task ShowNoticeAsync(string title, string content) { Notices.Add((title, content)); return Task.CompletedTask; }
        /// <summary>本测试默认取消业务确认。</summary>
        public Task<bool> ConfirmAsync(string title, string content) => Task.FromResult(false);
        /// <summary>本测试不打开系统目录选择器。</summary>
        public string? PickFolder(string title, string initialDirectory) => null;
    }

    /// <summary>保持页面模型测试与真实应用主题隔离。</summary>
    private sealed class Theme : IThemeService
    {
        /// <summary>忽略明暗主题请求，不依赖 Application.Current。</summary>
        public void Apply(bool dark) { }
    }

    /// <summary>使用明确排队的内存运行任务替代截图、识别和游戏输入。</summary>
    private sealed class Automation : IFreePackAutomationService
    {
        /// <summary>尚未由真实模型启动的运行夹具。</summary>
        private readonly Queue<AutomationRun> _pending = new();
        /// <summary>实际启动并捕获了令牌和进度边界的运行夹具。</summary>
        public List<AutomationRun> Runs { get; } = [];
        /// <summary>加入下一轮由测试控制完成时机的自动操作。</summary>
        public AutomationRun QueueRun()
        {
            var run = new AutomationRun();
            _pending.Enqueue(run);
            return run;
        }
        /// <summary>记录真实页面模型的输入，返回尚未释放的实际运行任务。</summary>
        public Task<FreePackRunResult> RunAsync(IProgress<FreePackProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (_pending.Count == 0) throw new InvalidOperationException("模型启动了未安排的自动操作。");
            var run = _pending.Dequeue();
            run.Progress = progress;
            run.CancellationToken = cancellationToken;
            Runs.Add(run);
            return run.Completion.Task;
        }
    }

    /// <summary>独立持有一次服务完成信号和捕获的进度，使清理、停止和晚到通知可确定复现。</summary>
    private sealed class AutomationRun
    {
        /// <summary>服务真正完成或失败的信号，取消请求本身不会提前释放。</summary>
        public TaskCompletionSource<FreePackRunResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>真实模型传入的界面进度接收器。</summary>
        public IProgress<FreePackProgress>? Progress { get; set; }
        /// <summary>真实模型为本轮创建的取消令牌。</summary>
        public CancellationToken CancellationToken { get; set; }
        /// <summary>报告字面指定的完整进度，允许模拟服务结束后的迟到回调。</summary>
        public void Report(string stage, int scanned, int opened) => Progress!.Report(new FreePackProgress { Stage = stage, ScannedPacks = scanned, OpenedPacks = opened });
        /// <summary>模拟服务清理结束后返回完整统计和结束原因。</summary>
        public void Complete(int scanned, int opened, string reason, bool cancelled = false) => Completion.TrySetResult(new FreePackRunResult { ScannedPacks = scanned, OpenedPacks = opened, Reason = reason, IsCancelled = cancelled });
        /// <summary>模拟服务清理结束后传播真实类型的异常。</summary>
        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    /// <summary>收集生产日志接口的实际输出，不依赖文件或控制台日志提供器。</summary>
    private sealed class MemoryLogger<T> : ILogger<T>
    {
        /// <summary>记录日志等级、原异常及格式化消息。</summary>
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];
        /// <summary>此测试没有跨调用日志作用域。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>接收所有等级以核实开始、结束及异常路径。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保存实际格式化消息和未经替换的异常对象。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
