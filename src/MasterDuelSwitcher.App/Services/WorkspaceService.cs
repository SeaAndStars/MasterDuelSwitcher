using System.Collections.ObjectModel;
using System.IO;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MasterDuelSwitcher.App.Services;

/// <summary>协调页面共享的发现快照、健康存储、异步互斥与运行状态。</summary>
public sealed class WorkspaceService : ObservableViewModel
{
    /// <summary>本地 SQLite 配置和账号记录。</summary>
    private readonly ISettingsStore _store;
    /// <summary>只读安装与账号发现服务。</summary>
    private readonly ISteamDiscoveryService _discovery;
    /// <summary>共享资源和事务清单扫描服务。</summary>
    private readonly IResourceSharingService _resources;
    /// <summary>可恢复错误的 Fluent 用户通知服务。</summary>
    private readonly IUserInteraction _interaction;
    /// <summary>加载用户偏好时应用官方主题的适配器。</summary>
    private readonly IThemeService _theme;
    /// <summary>命令阶段和异常栈的持久化日志。</summary>
    private readonly ILogger<WorkspaceService> _logger;
    /// <summary>参与全局互斥的页面和刷新命令。</summary>
    private readonly List<AsyncCommand> _commands = [];
    /// <summary>当前是否正在执行异步操作。</summary>
    private bool _isBusy;
    /// <summary>数据库是否已经健康读取。</summary>
    private bool _storageReady;
    /// <summary>底部状态栏的最新短提示。</summary>
    private string _status = "等待检测本机安装…";
    /// <summary>操作遮罩中的进度说明。</summary>
    private string _busyMessage = "正在处理…";
    /// <summary>导航底部显示的安装发现结果。</summary>
    private string _discoveryStatus = "等待检测安装";

    /// <summary>通知各页读取新的共同快照，不携带账号凭据。</summary>
    public event EventHandler? DataChanged;
    /// <summary>最近一次成功读取的可保存用户偏好。</summary>
    public AppSettings Settings { get; private set; } = new();
    /// <summary>本次实际发现的 Steam 账号，旧缓存不作为有效登录列表。</summary>
    public IReadOnlyList<SteamAccount> DetectedAccounts { get; private set; } = [];
    /// <summary>本次实际扫描的资源目录。</summary>
    public IReadOnlyList<ResourceProfile> Profiles { get; private set; } = [];
    /// <summary>当前游戏安装对应的资源事务清单。</summary>
    public IReadOnlyList<ShareBackup> Backups { get; private set; } = [];
    /// <summary>已检测的 Steam 安装目录，与设置页未保存编辑分离。</summary>
    public string ActiveSteamPath { get; private set; } = "";
    /// <summary>已检测的游戏安装目录，与设置页未保存编辑分离。</summary>
    public string ActiveGamePath { get; private set; } = "";
    /// <summary>独立于 EXE 位置的持久状态目录。</summary>
    public string StateDirectory => _store.StateDirectory;
    /// <summary>当前是否正在执行操作，改变后更新所有页面命令。</summary>
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Notify(nameof(IsReady)); foreach (var command in _commands) command.NotifyCanExecuteChanged(); } }
    /// <summary>整个工作区是否可接受下一次操作。</summary>
    public bool IsReady => !IsBusy;
    /// <summary>数据库是否健康，读取失败时阻止保存和账号同步。</summary>
    public bool StorageReady { get => _storageReady; private set => Set(ref _storageReady, value); }
    /// <summary>当前运行的最新用户提示。</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>当前异步操作说明。</summary>
    public string BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }
    /// <summary>导航区显示的安装发现状态。</summary>
    public string DiscoveryStatus { get => _discoveryStatus; private set => Set(ref _discoveryStatus, value); }
    /// <summary>最多六十条、单条有长度上限的内存操作记录。</summary>
    public ObservableCollection<string> Logs { get; } = [];
    /// <summary>读取配置并刷新实际发现快照的全局命令。</summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>注入共享业务边界，构造时保持只创建状态。</summary>
    public WorkspaceService(ISettingsStore store, ISteamDiscoveryService discovery, IResourceSharingService resources, IUserInteraction interaction, IThemeService theme, ILogger<WorkspaceService>? logger = null)
    {
        _store = store;
        _discovery = discovery;
        _resources = resources;
        _interaction = interaction;
        _theme = theme;
        _logger = logger ?? NullLogger<WorkspaceService>.Instance;
        RefreshCommand = CreateCommand("RefreshCommand", _ => RunOperationAsync("正在检测 Steam 与游戏资源…", LoadAndRefreshAsync));
    }

    /// <summary>首次启动时读取配置并执行只读发现。</summary>
    public Task InitializeAsync() => RefreshCommand.ExecuteAsync();

    /// <summary>创建受工作区互斥约束并记录开始、结束阶段的页面命令。</summary>
    public AsyncCommand CreateCommand(string name, Func<object?, Task> execute)
    {
        var command = new AsyncCommand(async parameter =>
        {
            _logger.LogDebug("命令开始：{Command}", name);
            try { await execute(parameter); }
            finally { _logger.LogDebug("命令结束：{Command}", name); }
        }, () => IsReady);
        _commands.Add(command);
        return command;
    }

    /// <summary>重新尝试加载健康数据库，失败时保留原数据库并停用写入。</summary>
    private async Task LoadAndRefreshAsync()
    {
        StorageReady = false;
        Settings = _store.Load();
        StorageReady = true;
        _theme.Apply(Settings.DarkTheme);
        await RefreshDataAsync();
    }

    /// <summary>后台扫描只读快照并通知页面；仅健康数据库接收实际账号同步。</summary>
    public async Task RefreshDataAsync()
    {
        var snapshot = await Task.Run(() =>
        {
            var discovery = _discovery.Discover(EmptyToNull(Settings.SteamPath), EmptyToNull(Settings.GamePath));
            var profiles = string.IsNullOrEmpty(discovery.GamePath) ? [] : _resources.ScanProfiles(discovery.GamePath);
            var backups = string.IsNullOrEmpty(discovery.GamePath) ? [] : _resources.GetBackups(discovery.GamePath);
            return (Discovery: discovery, Profiles: profiles, Backups: backups);
        });
        if (StorageReady) _store.SynchronizeAccounts(snapshot.Discovery.Accounts);
        ActiveSteamPath = snapshot.Discovery.SteamPath;
        ActiveGamePath = snapshot.Discovery.GamePath;
        DetectedAccounts = snapshot.Discovery.Accounts;
        Profiles = snapshot.Profiles;
        Backups = snapshot.Backups;
        DiscoveryStatus = string.IsNullOrEmpty(ActiveSteamPath) ? "Steam 待设置" : string.IsNullOrEmpty(ActiveGamePath) ? "游戏路径待设置" : "Steam 与游戏已检测";
        DataChanged?.Invoke(this, EventArgs.Empty);
        AddLog($"已刷新：{DetectedAccounts.Count} 个 Steam 账号，{Profiles.Count} 个资源目录。");
    }

    /// <summary>统一处理忙碌反馈、可恢复用户错误及持久化异常栈。</summary>
    public async Task RunOperationAsync(string message, Func<Task> operation)
    {
        IsBusy = true;
        BusyMessage = message;
        try { await operation(); }
        catch (Exception exception)
        {
            _logger.LogError(exception, "命令执行失败：{Operation}", message);
            var text = exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                ? exception.Message : "操作遇到错误。请检查路径和文件访问权限，然后刷新后重试。";
            AddLog($"操作中断：{text}");
            await _interaction.ShowNoticeAsync("操作中断", text + "\n\n资源操作记录可在“备份还原”中检查。");
        }
        finally { IsBusy = false; }
    }

    /// <summary>将用户提示压缩为有长度和数量上限的本次运行记录。</summary>
    public void AddLog(string message)
    {
        var compact = message.Replace('\r', ' ').Replace('\n', ' ');
        if (compact.Length > 600) compact = compact[..600] + "…";
        Status = compact;
        Logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {compact}");
        while (Logs.Count > 60) Logs.RemoveAt(Logs.Count - 1);
    }

    /// <summary>要求数据库成功读取后再执行设置或账号写入。</summary>
    public void EnsureStorageReady()
    {
        if (!StorageReady) throw new InvalidOperationException("本地 accounts.db 尚未成功读取。请修复或还原数据库，再点击刷新。");
    }

    /// <summary>要求已发现 Steam 安装，实际文件由核心服务再次校验。</summary>
    public void EnsureSteamPath()
    {
        if (string.IsNullOrEmpty(ActiveSteamPath)) throw new InvalidOperationException("请先在设置中选择 Steam 安装目录。");
    }
}
