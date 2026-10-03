using System.IO;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>独立管理当前账号免费卡包循环、停止请求和界面进度。</summary>
public sealed class FreePacksPageViewModel : ObservableViewModel, IDisposable
{
    /// <summary>负责免费核验和有限卡包循环的核心服务。</summary>
    private readonly IFreePackAutomationService _automation;
    /// <summary>复用应用持久日志的运行与进度记录器。</summary>
    private readonly ILogger<FreePacksPageViewModel> _logger;
    /// <summary>当前轮持有的取消源，结束后清除避免访问已释放对象。</summary>
    private CancellationTokenSource? _cancellation;
    /// <summary>应用会话是否已经释放此页面模型。</summary>
    private bool _disposed;
    /// <summary>当前轮是否仍在运行或等待清理。</summary>
    private bool _isRunning;
    /// <summary>当前执行阶段或结束原因。</summary>
    private string _stage = "等待开始";
    /// <summary>本轮已扫描的卡包数量。</summary>
    private int _scannedPacks;
    /// <summary>本轮已确认结果的免费开包数量。</summary>
    private int _openedPacks;

    /// <summary>与其他页面共用的操作互斥及本机日志状态。</summary>
    public WorkspaceService Workspace { get; }
    /// <summary>开始当前账号免费卡包扫描的界面命令。</summary>
    public AsyncCommand StartCommand { get; }
    /// <summary>在全局工作区忙碌时仍然可执行的停止命令。</summary>
    public AsyncCommand StopCommand { get; }
    /// <summary>当前是否持有尚未结束的开包任务。</summary>
    public bool IsRunning { get => _isRunning; private set { Set(ref _isRunning, value); StopCommand.NotifyCanExecuteChanged(); } }
    /// <summary>当前阶段或本轮结束的简短原因。</summary>
    public string Stage { get => _stage; private set => Set(ref _stage, value); }
    /// <summary>本轮已检查的不同卡包数量。</summary>
    public int ScannedPacks { get => _scannedPacks; private set => Set(ref _scannedPacks, value); }
    /// <summary>本轮已确认结果的免费卡包数量。</summary>
    public int OpenedPacks { get => _openedPacks; private set => Set(ref _openedPacks, value); }
    /// <summary>现有运行和调试日志所在的系统数据目录。</summary>
    public string LogDirectory => Path.Combine(Workspace.StateDirectory, "logs");
    /// <summary>包含工作区清理过程、可用于停止等待的当前完整任务。</summary>
    public Task RunningTask { get; private set; } = Task.CompletedTask;

    /// <summary>注入开包服务与共享工作区，构造时不访问游戏。</summary>
    public FreePacksPageViewModel(WorkspaceService workspace, IFreePackAutomationService automation, ILogger<FreePacksPageViewModel>? logger = null)
    {
        Workspace = workspace;
        _automation = automation;
        _logger = logger ?? NullLogger<FreePacksPageViewModel>.Instance;
        StopCommand = new AsyncCommand(_ => RequestStopAsync(), () => IsRunning);
        StartCommand = workspace.CreateCommand(nameof(StartCommand), _ => StartAsync());
    }

    /// <summary>保持完整运行任务，并以工作区互斥阻止刷新和其他事务交叉执行。</summary>
    private Task StartAsync()
    {
        if (_disposed) return Task.CompletedTask;
        RunningTask = Workspace.RunOperationAsync("正在扫描当前账号的免费卡包…", RunAsync);
        return RunningTask;
    }

    /// <summary>运行一轮自动操作，界面上下文负责接收进度，取消作为正常结束处理。</summary>
    private async Task RunAsync()
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        ScannedPacks = 0;
        OpenedPacks = 0;
        Stage = "正在检测当前卡包…";
        IsRunning = true;
        _logger.LogInformation("免费开包开始。");
        var progress = new Progress<FreePackProgress>(value =>
        {
            if (ReferenceEquals(_cancellation, cancellation)) UpdateProgress(value);
        });
        try
        {
            var result = await _automation.RunAsync(progress, cancellation.Token);
            ScannedPacks = result.ScannedPacks;
            OpenedPacks = result.OpenedPacks;
            Stage = result.Reason;
            Workspace.AddLog($"免费开包结束：{Stage} · 已扫描 {ScannedPacks} · 已开包 {OpenedPacks}。");
            _logger.LogInformation("免费开包结束：{Reason}，扫描 {ScannedPacks}，开包 {OpenedPacks}，停止 {IsCancelled}。", result.Reason, ScannedPacks, OpenedPacks, result.IsCancelled);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Stage = "已停止";
            Workspace.AddLog("免费开包已停止。");
            _logger.LogInformation("免费开包已停止，扫描 {ScannedPacks}，开包 {OpenedPacks}。", ScannedPacks, OpenedPacks);
        }
        catch
        {
            Stage = "操作中断";
            throw;
        }
        finally
        {
            _cancellation = null;
            cancellation.Dispose();
            IsRunning = false;
            _logger.LogDebug("免费开包运行资源已释放。");
        }
    }

    /// <summary>更新当前轮的界面进度和有上限的操作记录，不接收旧轮迟到消息。</summary>
    private void UpdateProgress(FreePackProgress progress)
    {
        Stage = progress.Stage;
        ScannedPacks = progress.ScannedPacks;
        OpenedPacks = progress.OpenedPacks;
        Workspace.AddLog($"免费开包：{Stage} · 已扫描 {ScannedPacks} · 已开包 {OpenedPacks}。");
        _logger.LogDebug("免费开包阶段：{Stage}，扫描 {ScannedPacks}，开包 {OpenedPacks}。", Stage, ScannedPacks, OpenedPacks);
    }

    /// <summary>请求停止本页持有的运行任务。</summary>
    public void Stop()
    {
        if (_cancellation is null) return;
        Stage = "正在停止…";
        _logger.LogDebug("收到免费开包停止请求。");
        _cancellation.Cancel();
    }

    /// <summary>请求停止并等待完整运行任务释放共享忙碌状态。</summary>
    public Task RequestStopAsync()
    {
        Stop();
        return RunningTask;
    }

    /// <summary>应用容器释放页面模型时停止其持有的任务。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
