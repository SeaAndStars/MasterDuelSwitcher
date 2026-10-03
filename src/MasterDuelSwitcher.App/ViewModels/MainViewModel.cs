using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>主界面的可独立测试状态和命令，具体文件、Steam 与界面操作均通过接口注入。</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    /// <summary>本地 SQLite 设置与账号存储。</summary>
    private readonly ISettingsStore _store;
    /// <summary>只读安装和 Steam 账号发现。</summary>
    private readonly ISteamDiscoveryService _discovery;
    /// <summary>带备份的 Steam 切换和还原服务。</summary>
    private readonly ISteamAccountService _steam;
    /// <summary>带事务清单的资源共享和还原服务。</summary>
    private readonly IResourceSharingService _resources;
    /// <summary>可替换的通知和文件夹选择交互。</summary>
    private readonly IUserInteraction _interaction;
    /// <summary>可替换的官方 Fluent 主题适配器。</summary>
    private readonly IThemeService _theme;
    /// <summary>当前进程真实权限和系统数据目录。</summary>
    private readonly IApplicationEnvironment _environment;
    /// <summary>命令阶段与异常栈的持久化日志，不记录账号备注或登录数据。</summary>
    private readonly ILogger<MainViewModel> _logger;
    /// <summary>当前全部命令，用于统一更新可执行状态。</summary>
    private readonly List<AsyncCommand> _commands = [];
    /// <summary>已成功加载的用户偏好设置。</summary>
    private AppSettings _settings = new();
    /// <summary>本次发现返回的真实 Steam 账号。</summary>
    private IReadOnlyList<SteamAccount> _detectedAccounts = [];
    /// <summary>已检测并校验的 Steam 目录，独立于未保存的文本编辑。</summary>
    private string _activeSteamPath = "";
    /// <summary>已检测并校验的游戏目录，独立于未保存的文本编辑。</summary>
    private string _activeGamePath = "";
    /// <summary>当前页面标识。</summary>
    private string _currentPage = "Accounts";
    /// <summary>异步操作执行状态。</summary>
    private bool _isBusy;
    /// <summary>本地数据库最近一次读取是否成功。</summary>
    private bool _storageReady;
    /// <summary>用户编辑的 Steam 安装路径。</summary>
    private string _steamPath = "";
    /// <summary>用户编辑的游戏安装路径。</summary>
    private string _gamePath = "";
    /// <summary>始终可见的最新操作结果。</summary>
    private string _status = "等待检测本机安装…";
    /// <summary>遮罩中的当前异步操作说明。</summary>
    private string _busyMessage = "正在处理…";
    /// <summary>资源目录数量、链接数量和容量的概要。</summary>
    private string _resourceSummary = "尚未检测到资源目录";
    /// <summary>侧栏中的安装发现状态。</summary>
    private string _discoveryStatus = "等待检测安装";
    /// <summary>是否显示工具中隐藏的账号。</summary>
    private bool _showHidden;
    /// <summary>用户选择的深色主题偏好。</summary>
    private bool _darkTheme;
    /// <summary>当前账号选择。</summary>
    private AccountItem? _selectedAccount;
    /// <summary>备注编辑框的当前值。</summary>
    private string _note = "";
    /// <summary>当前账号的手动资源绑定选择。</summary>
    private ResourceBindingOption? _selectedBinding;
    /// <summary>当前选择的共享资源来源。</summary>
    private ResourceProfile? _selectedSource;
    /// <summary>当前选择的资源事务清单。</summary>
    private ShareBackup? _selectedBackup;

    /// <summary>通知 XAML 更新可绑定属性。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>当前可见的实际账号及本地备注。</summary>
    public ObservableCollection<AccountItem> Accounts { get; } = [];
    /// <summary>当前实际扫描的全部资源目录。</summary>
    public ObservableCollection<ResourceProfile> Profiles { get; } = [];
    /// <summary>可选择为来源的独立且有下载资源的目录。</summary>
    public ObservableCollection<ResourceProfile> SourceProfiles { get; } = [];
    /// <summary>当前游戏安装对应的资源事务清单。</summary>
    public ObservableCollection<ShareBackup> Backups { get; } = [];
    /// <summary>资源绑定下拉选项，包含明确标记的已保存失效绑定。</summary>
    public ObservableCollection<ResourceBindingOption> BindingOptions { get; } = [];
    /// <summary>最多六十条的内存操作记录，不写入凭证或异常栈。</summary>
    public ObservableCollection<string> Logs { get; } = [];
    /// <summary>切换功能页面的命令。</summary>
    public AsyncCommand NavigateCommand { get; }
    /// <summary>读取本地配置并刷新真实发现数据的命令。</summary>
    public AsyncCommand RefreshCommand { get; }
    /// <summary>保存账号备注和手动绑定的命令。</summary>
    public AsyncCommand SaveAccountCommand { get; }
    /// <summary>隐藏或重新显示选中账号的命令。</summary>
    public AsyncCommand HideAccountCommand { get; }
    /// <summary>显示 Steam 正常添加账号说明的命令。</summary>
    public AsyncCommand AddAccountCommand { get; }
    /// <summary>按绑定关联资源并切号启动的命令。</summary>
    public AsyncCommand SwitchAndLaunchCommand { get; }
    /// <summary>保存资源来源选择的命令。</summary>
    public AsyncCommand SaveSourceCommand { get; }
    /// <summary>给全部其他实际目录启用共享的命令。</summary>
    public AsyncCommand EnableSharingCommand { get; }
    /// <summary>还原所选资源事务的命令。</summary>
    public AsyncCommand RestoreResourcesCommand { get; }
    /// <summary>还原上次 Steam 登录配置的命令。</summary>
    public AsyncCommand RestoreSteamCommand { get; }
    /// <summary>打开系统安装目录选择器的命令。</summary>
    public AsyncCommand BrowseFolderCommand { get; }
    /// <summary>校验并保存手动安装目录的命令。</summary>
    public AsyncCommand SaveSettingsCommand { get; }
    /// <summary>清除手动路径并重新检测的命令。</summary>
    public AsyncCommand AutoDetectCommand { get; }
    /// <summary>保存并应用明暗主题的命令。</summary>
    public AsyncCommand ToggleThemeCommand { get; }

    /// <summary>构建可注入的主界面状态；初始化由应用组合入口显式启动。</summary>
    public MainViewModel(ISettingsStore store, ISteamDiscoveryService discovery, ISteamAccountService steam, IResourceSharingService resources, IUserInteraction interaction, IThemeService theme, IApplicationEnvironment environment, ILogger<MainViewModel>? logger = null)
    {
        _store = store;
        _discovery = discovery;
        _steam = steam;
        _resources = resources;
        _interaction = interaction;
        _theme = theme;
        _environment = environment;
        _logger = logger ?? NullLogger<MainViewModel>.Instance;
        NavigateCommand = CreateCommand("NavigateCommand", parameter => { CurrentPage = parameter as string ?? "Accounts"; return Task.CompletedTask; });
        RefreshCommand = CreateCommand("RefreshCommand", _ => RunOperationAsync("正在检测 Steam 与游戏资源…", LoadAndRefreshAsync));
        SaveAccountCommand = CreateCommand("SaveAccountCommand", _ => RunOperationAsync("正在保存账号信息…", () => { var id = SaveAccount(); RebuildAccounts(id); AddLog("账号信息已保存。"); return Task.CompletedTask; }));
        HideAccountCommand = CreateCommand("HideAccountCommand", _ => RunOperationAsync("正在更新账号列表…", HideAccountAsync));
        AddAccountCommand = CreateCommand("AddAccountCommand", _ => _interaction.ShowNoticeAsync("添加 Steam 账号", "请在 Steam 使用“更改账号”正常登录另一个账号，并记住登录状态。完成后返回工具点击“刷新”。"));
        SwitchAndLaunchCommand = CreateCommand("SwitchAndLaunchCommand", _ => RunOperationAsync("正在切换账号并启动 Master Duel…", SwitchAndLaunchAsync));
        SaveSourceCommand = CreateCommand("SaveSourceCommand", _ => RunOperationAsync("正在保存资源来源…", () => { SaveSource(); return Task.CompletedTask; }));
        EnableSharingCommand = CreateCommand("EnableSharingCommand", _ => RunOperationAsync("正在备份并关联共享资源…", EnableSharingAsync));
        RestoreResourcesCommand = CreateCommand("RestoreResourcesCommand", _ => RunOperationAsync("正在还原独立资源目录…", RestoreResourcesAsync));
        RestoreSteamCommand = CreateCommand("RestoreSteamCommand", _ => RunOperationAsync("正在还原 Steam 登录配置…", async () => { EnsureSteamPath(); var result = await _steam.RestoreLatestAsync(_activeSteamPath); await RefreshDataAsync(); AddLog(result); }));
        BrowseFolderCommand = CreateCommand("BrowseFolderCommand", parameter => RunOperationAsync("正在选择安装目录…", () => { BrowseFolder(parameter as string); return Task.CompletedTask; }));
        SaveSettingsCommand = CreateCommand("SaveSettingsCommand", _ => RunOperationAsync("正在保存并检测安装路径…", SaveSettingsAsync));
        AutoDetectCommand = CreateCommand("AutoDetectCommand", _ => RunOperationAsync("正在自动检测安装路径…", async () => { EnsureStorageReady(); _settings.SteamPath = ""; _settings.GamePath = ""; _store.Save(_settings); await RefreshDataAsync(); AddLog("已重新自动检测安装路径。"); }));
        ToggleThemeCommand = CreateCommand("ToggleThemeCommand", _ => RunOperationAsync("正在保存外观设置…", () => { EnsureStorageReady(); _settings.DarkTheme = DarkTheme; _store.Save(_settings); _theme.Apply(DarkTheme); AddLog("主题偏好已保存。"); return Task.CompletedTask; }));
    }

    /// <summary>当前页面标识，改变后通知所有页面可见性和标题。</summary>
    public string CurrentPage { get => _currentPage; set { if (Set(ref _currentPage, value)) Notify(nameof(Title), nameof(Subtitle), nameof(IsAccountsPage), nameof(IsResourcesPage), nameof(IsBackupsPage), nameof(IsSettingsPage)); } }
    /// <summary>当前页面标题。</summary>
    public string Title => CurrentPage switch { "Resources" => "资源共享", "Backups" => "备份还原", "Settings" => "设置", _ => "账号切换" };
    /// <summary>当前页面的简短操作说明。</summary>
    public string Subtitle => CurrentPage switch { "Resources" => "一次更新，多个账号共用已下载的游戏资源。", "Backups" => "检查操作记录，按需恢复独立资源与登录配置。", "Settings" => "检查安装位置，调整本机的工具偏好。", _ => "选择本机账号，继续下一场决斗。" };
    /// <summary>账号页是否为当前页面。</summary>
    public bool IsAccountsPage => CurrentPage is not ("Resources" or "Backups" or "Settings");
    /// <summary>资源页是否为当前页面。</summary>
    public bool IsResourcesPage => CurrentPage == "Resources";
    /// <summary>备份页是否为当前页面。</summary>
    public bool IsBackupsPage => CurrentPage == "Backups";
    /// <summary>设置页是否为当前页面。</summary>
    public bool IsSettingsPage => CurrentPage == "Settings";
    /// <summary>当前是否正在执行异步操作。</summary>
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Notify(nameof(IsReady)); foreach (var command in _commands) command.NotifyCanExecuteChanged(); } }
    /// <summary>界面内容是否可交互。</summary>
    public bool IsReady => !IsBusy;
    /// <summary>本地数据库是否已健康读取，损坏时阻止保存及账号同步。</summary>
    public bool StorageReady { get => _storageReady; private set => Set(ref _storageReady, value); }
    /// <summary>用户可编辑的 Steam 安装目录。</summary>
    public string SteamPath { get => _steamPath; set => Set(ref _steamPath, value); }
    /// <summary>用户可编辑的游戏安装目录。</summary>
    public string GamePath { get => _gamePath; set => Set(ref _gamePath, value); }
    /// <summary>持久状态目录，不随 EXE 所在目录变化。</summary>
    public string StateDirectory => _store.StateDirectory;
    /// <summary>当前进程是否实际具有管理员权限。</summary>
    public bool IsElevated => _environment.IsElevated;
    /// <summary>供用户和真实窗口验证读取的权限状态。</summary>
    public string AdminStatus => IsElevated ? "管理员权限" : "普通权限";
    /// <summary>底部状态栏最新操作结果。</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>异步遮罩中的操作说明。</summary>
    public string BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }
    /// <summary>当前可见账号数量和本机发现总量。</summary>
    public string AccountCountText => $"本机账号 · {Accounts.Count}/{_detectedAccounts.Count}";
    /// <summary>资源数量与容量摘要。</summary>
    public string ResourceSummary { get => _resourceSummary; private set => Set(ref _resourceSummary, value); }
    /// <summary>侧栏安装发现结果。</summary>
    public string DiscoveryStatus { get => _discoveryStatus; private set => Set(ref _discoveryStatus, value); }
    /// <summary>是否显示隐藏账号，改变时立即重建过滤列表。</summary>
    public bool ShowHidden { get => _showHidden; set { if (Set(ref _showHidden, value)) RebuildAccounts(SelectedAccount?.Account.SteamId); } }
    /// <summary>选择的明暗主题，通过外观命令保存。</summary>
    public bool DarkTheme { get => _darkTheme; set => Set(ref _darkTheme, value); }
    /// <summary>当前账号；变更时填入备注和保留失效绑定的下拉选项。</summary>
    public AccountItem? SelectedAccount { get => _selectedAccount; set { if (Set(ref _selectedAccount, value)) { LoadAccountEditor(); Notify(nameof(HasSelectedAccount)); } } }
    /// <summary>是否显示账号编辑区。</summary>
    public bool HasSelectedAccount => SelectedAccount is not null;
    /// <summary>账号列表是否处于空状态。</summary>
    public bool NoAccounts => Accounts.Count == 0;
    /// <summary>资源列表是否处于空状态。</summary>
    public bool NoProfiles => Profiles.Count == 0;
    /// <summary>资源备份列表是否处于空状态。</summary>
    public bool NoBackups => Backups.Count == 0;
    /// <summary>当前账号的备注编辑值。</summary>
    public string Note { get => _note; set => Set(ref _note, value); }
    /// <summary>账号资源目录绑定的当前选择。</summary>
    public ResourceBindingOption? SelectedBinding { get => _selectedBinding; set => Set(ref _selectedBinding, value); }
    /// <summary>共享来源的当前选择。</summary>
    public ResourceProfile? SelectedSource { get => _selectedSource; set => Set(ref _selectedSource, value); }
    /// <summary>资源备份的当前选择。</summary>
    public ShareBackup? SelectedBackup { get => _selectedBackup; set => Set(ref _selectedBackup, value); }

    /// <summary>首次打开真实窗口时加载本地设置和发现数据。</summary>
    public Task InitializeAsync() => RefreshCommand.ExecuteAsync();

    /// <summary>创建由统一忙碌状态约束的命令。</summary>
    private AsyncCommand CreateCommand(string name, Func<object?, Task> execute)
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

    /// <summary>重新尝试读取数据库，损坏时保留原文件并停用所有设置写入。</summary>
    private async Task LoadAndRefreshAsync()
    {
        StorageReady = false;
        _settings = _store.Load();
        StorageReady = true;
        SteamPath = _settings.SteamPath;
        GamePath = _settings.GamePath;
        DarkTheme = _settings.DarkTheme;
        _theme.Apply(DarkTheme);
        await RefreshDataAsync();
    }

    /// <summary>在后台只读扫描安装、资源和清单，然后更新界面及健康数据库中的账号记录。</summary>
    private async Task RefreshDataAsync()
    {
        var selectedId = SelectedAccount?.Account.SteamId;
        var snapshot = await Task.Run(() =>
        {
            var discovery = _discovery.Discover(EmptyToNull(_settings.SteamPath), EmptyToNull(_settings.GamePath));
            var profiles = string.IsNullOrEmpty(discovery.GamePath) ? [] : _resources.ScanProfiles(discovery.GamePath);
            var backups = string.IsNullOrEmpty(discovery.GamePath) ? [] : _resources.GetBackups(discovery.GamePath);
            return (Discovery: discovery, Profiles: profiles, Backups: backups);
        });
        _activeSteamPath = snapshot.Discovery.SteamPath;
        _activeGamePath = snapshot.Discovery.GamePath;
        _detectedAccounts = snapshot.Discovery.Accounts;
        if (StorageReady) _store.SynchronizeAccounts(_detectedAccounts);
        SteamPath = string.IsNullOrEmpty(_activeSteamPath) ? _settings.SteamPath : _activeSteamPath;
        GamePath = string.IsNullOrEmpty(_activeGamePath) ? _settings.GamePath : _activeGamePath;
        Replace(Profiles, snapshot.Profiles);
        Replace(SourceProfiles, Profiles.Where(profile => !profile.IsLinked && profile.Bytes > 0));
        SelectedSource = SourceProfiles.FirstOrDefault(profile => profile.FolderName == _settings.SourceProfile);
        Replace(Backups, snapshot.Backups);
        SelectedBackup = null;
        ResourceSummary = NoProfiles ? "尚未检测到资源目录" : $"{Profiles.Count} 个目录 · {Profiles.Count(profile => profile.IsLinked)} 个已共享 · 独立资源 {FormatBytes(Profiles.Where(profile => !profile.IsLinked).Sum(profile => profile.Bytes))}";
        DiscoveryStatus = string.IsNullOrEmpty(_activeSteamPath) ? "Steam 待设置" : string.IsNullOrEmpty(_activeGamePath) ? "游戏路径待设置" : "Steam 与游戏已检测";
        RebuildAccounts(selectedId);
        Notify(nameof(NoProfiles), nameof(NoBackups));
        AddLog($"已刷新：{_detectedAccounts.Count} 个 Steam 账号，{Profiles.Count} 个资源目录。");
    }

    /// <summary>按用户隐藏偏好重建真实账号列表并尝试恢复选择。</summary>
    private void RebuildAccounts(string? selectedId)
    {
        Replace(Accounts, _detectedAccounts.Where(account => ShowHidden || !_settings.HiddenAccounts.Contains(account.SteamId)).OrderByDescending(account => account.MostRecent).Select(account => new AccountItem
        {
            Account = account,
            Note = _settings.AccountNotes.GetValueOrDefault(account.SteamId, ""),
            ResourceFolder = _settings.AccountBindings.GetValueOrDefault(account.SteamId, ""),
            IsHidden = _settings.HiddenAccounts.Contains(account.SteamId)
        }));
        SelectedAccount = Accounts.FirstOrDefault(account => account.Account.SteamId == selectedId);
        Notify(nameof(NoAccounts), nameof(AccountCountText));
    }

    /// <summary>填写所选账号编辑区；历史绑定失效时保留原值并明确提示。</summary>
    private void LoadAccountEditor()
    {
        Note = SelectedAccount?.Note ?? "";
        BindingOptions.Clear();
        BindingOptions.Add(new ResourceBindingOption());
        foreach (var profile in Profiles) BindingOptions.Add(new ResourceBindingOption { FolderName = profile.FolderName, DisplayName = profile.DisplayName });
        var folder = SelectedAccount?.ResourceFolder ?? "";
        if (folder.Length != 0 && !BindingOptions.Any(option => option.FolderName == folder))
            BindingOptions.Add(new ResourceBindingOption { FolderName = folder, DisplayName = $"{folder} · 未检测到，请重新绑定" });
        SelectedBinding = BindingOptions.First(option => option.FolderName == folder);
    }

    /// <summary>保存用户明确选择的绑定，只有选择未绑定项时才删除映射。</summary>
    private string SaveAccount()
    {
        EnsureStorageReady();
        var selected = RequireAccount();
        _settings.AccountNotes[selected.Account.SteamId] = Note.Trim();
        var folder = SelectedBinding?.FolderName ?? "";
        if (folder.Length == 0) _settings.AccountBindings.Remove(selected.Account.SteamId);
        else _settings.AccountBindings[selected.Account.SteamId] = folder;
        _store.Save(_settings);
        return selected.Account.SteamId;
    }

    /// <summary>只改变工具中的账号可见性，不修改 Steam 登录记录。</summary>
    private Task HideAccountAsync()
    {
        EnsureStorageReady();
        var selected = RequireAccount();
        if (!_settings.HiddenAccounts.Add(selected.Account.SteamId)) _settings.HiddenAccounts.Remove(selected.Account.SteamId);
        _store.Save(_settings);
        RebuildAccounts(selected.Account.SteamId);
        AddLog("账号列表已更新；Steam 账号记录保持原样。");
        return Task.CompletedTask;
    }

    /// <summary>按手工绑定关联资源，随后让核心服务检查游戏进程并通过 Steam 切换启动。</summary>
    private async Task SwitchAndLaunchAsync()
    {
        EnsureSteamPath();
        var account = RequireAccount().Account;
        SaveAccount();
        var target = _settings.AccountBindings.GetValueOrDefault(account.SteamId, "");
        if (target.Length != 0 && !Profiles.Any(profile => profile.FolderName == target))
            throw new InvalidOperationException("绑定的资源目录已不存在。请刷新并重新绑定。");
        if (_settings.SourceProfile.Length != 0 && target.Length != 0 && !target.Equals(_settings.SourceProfile, StringComparison.OrdinalIgnoreCase))
        {
            var source = RequireSource(_settings.SourceProfile);
            await Task.Run(() => _resources.EnableSharing(_activeGamePath, source.FolderName, new[] { target }));
            AddLog("已检查并关联当前账号资源。");
        }
        else if (_settings.SourceProfile.Length != 0 && target.Length == 0) AddLog("本账号未绑定资源目录，本次直接通过 Steam 启动。");
        var result = await _steam.SwitchAndLaunchAsync(_activeSteamPath, account);
        await RefreshDataAsync();
        AddLog(result);
    }

    /// <summary>记录来源选择，仅修改本地偏好。</summary>
    private void SaveSource()
    {
        EnsureStorageReady();
        var source = RequireSelectedSource();
        _settings.SourceProfile = source.FolderName;
        _store.Save(_settings);
        AddLog($"资源来源已保存：{source.FolderName}。");
    }

    /// <summary>为全部其他真实资源目录启用有备份的共享。</summary>
    private async Task EnableSharingAsync()
    {
        EnsureStorageReady();
        var source = RequireSelectedSource();
        var targets = Profiles.Where(profile => profile.FolderName != source.FolderName).Select(profile => profile.FolderName).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("当前只有来源目录。请先使用其他账号启动游戏，生成对应目录后刷新。");
        SaveSource();
        var backup = await Task.Run(() => _resources.EnableSharing(_activeGamePath, source.FolderName, targets));
        await RefreshDataAsync();
        AddLog(backup is null ? "资源共享已是最新状态。" : $"资源共享已启用；已保留 {backup.Entries.Count} 个目录的还原记录。");
    }

    /// <summary>还原选中的资源事务，并显示已还原的幂等结果。</summary>
    private async Task RestoreResourcesAsync()
    {
        var backup = SelectedBackup ?? throw new InvalidOperationException("请先选择一条资源共享备份。");
        if (backup.Restored) { AddLog("这条备份已经完成还原。"); return; }
        await Task.Run(() => _resources.Restore(backup.Id));
        await RefreshDataAsync();
        AddLog("选中的资源备份已还原。");
    }

    /// <summary>打开用户要求的安装目录选择器，取消时保留编辑值。</summary>
    private void BrowseFolder(string? target)
    {
        var steam = target == "Steam";
        var folder = _interaction.PickFolder(steam ? "选择包含 Steam.exe 的目录" : "选择包含 masterduel.exe 的目录", steam ? SteamPath : GamePath);
        if (folder is null) return;
        if (steam) SteamPath = folder;
        else GamePath = folder;
    }

    /// <summary>先让安装发现服务验证编辑路径，验证成功后持久保存并刷新。</summary>
    private async Task SaveSettingsAsync()
    {
        EnsureStorageReady();
        var steamPath = SteamPath.Trim();
        var gamePath = GamePath.Trim();
        await Task.Run(() => _discovery.Discover(EmptyToNull(steamPath), EmptyToNull(gamePath)));
        _settings.SteamPath = steamPath;
        _settings.GamePath = gamePath;
        _store.Save(_settings);
        await RefreshDataAsync();
        AddLog("安装路径已保存。");
    }

    /// <summary>统一处理忙碌反馈和可恢复错误，不输出异常栈。</summary>
    private async Task RunOperationAsync(string message, Func<Task> operation)
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

    /// <summary>记录有长度和条数上限的本次运行状态。</summary>
    private void AddLog(string message)
    {
        var compact = message.Replace('\r', ' ').Replace('\n', ' ');
        if (compact.Length > 600) compact = compact[..600] + "…";
        Status = compact;
        Logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {compact}");
        while (Logs.Count > 60) Logs.RemoveAt(Logs.Count - 1);
    }

    /// <summary>要求数据库健康读取后再保存或同步账号。</summary>
    private void EnsureStorageReady()
    {
        if (!StorageReady) throw new InvalidOperationException("本地 accounts.db 尚未成功读取。请修复或还原数据库，再点击刷新。");
    }

    /// <summary>要求选中本次真实发现的 Steam 账号。</summary>
    private AccountItem RequireAccount() => SelectedAccount ?? throw new InvalidOperationException("请先选择一个 Steam 账号。");

    /// <summary>要求用户选择独立且已下载资源的共享来源。</summary>
    private ResourceProfile RequireSelectedSource() => SelectedSource is null ? throw new InvalidOperationException("请先选择已完成更新的独立资源来源。") : RequireSource(SelectedSource.FolderName);

    /// <summary>验证扫描中的来源仍是独立目录；实际路径和游戏运行状态由核心服务再次验证。</summary>
    private ResourceProfile RequireSource(string folder)
    {
        var source = Profiles.FirstOrDefault(profile => profile.FolderName == folder);
        if (source is null || source.IsLinked || source.Bytes <= 0) throw new InvalidOperationException("资源来源已改变或尚未完成下载。请刷新并选择有资源的独立目录。");
        if (string.IsNullOrEmpty(_activeGamePath)) throw new InvalidOperationException("请先检测 Master Duel 安装目录。");
        return source;
    }

    /// <summary>要求已检测 Steam 安装，具体可执行文件由核心服务校验。</summary>
    private void EnsureSteamPath()
    {
        if (string.IsNullOrEmpty(_activeSteamPath)) throw new InvalidOperationException("请先在设置中选择 Steam 安装目录。");
    }

    /// <summary>更新属性值并仅在值改变时通知绑定。</summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    /// <summary>通知一组派生属性重新读取。</summary>
    private void Notify(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>替换显示集合内容，先物化输入以允许使用旧集合做筛选。</summary>
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var snapshot = values.ToArray();
        target.Clear();
        foreach (var item in snapshot) target.Add(item);
    }

    /// <summary>空白安装偏好使用自动发现。</summary>
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>格式化实际资源文件大小。</summary>
    private static string FormatBytes(long value) => value >= 1_073_741_824 ? $"{value / 1_073_741_824d:F2} GB" : $"{value / 1_048_576d:F1} MB";
}

