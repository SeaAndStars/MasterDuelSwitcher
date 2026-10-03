using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>独立管理账号列表、人工备注、资源绑定与 Steam 切换业务。</summary>
public sealed class AccountsPageViewModel : ObservableViewModel, IDisposable
{
    /// <summary>保存账号人工信息的本地数据库服务。</summary>
    private readonly ISettingsStore _store;
    /// <summary>执行安全切换与启动的 Steam 服务。</summary>
    private readonly ISteamAccountService _steam;
    /// <summary>关联账号资源目录的事务服务。</summary>
    private readonly IResourceSharingService _resources;
    /// <summary>是否显示工具内隐藏的账号。</summary>
    private bool _showHidden;
    /// <summary>当前编辑和启动的账号。</summary>
    private AccountItem? _selectedAccount;
    /// <summary>账号编辑器中的备注文字。</summary>
    private string _note = "";
    /// <summary>账号编辑器中的手工资源绑定。</summary>
    private ResourceBindingOption? _selectedBinding;
    /// <summary>账号列表的即时模糊搜索关键词。</summary>
    private string _searchText = "";
    /// <summary>按账号隔离未保存的备注和资源绑定，筛选与排序不会覆盖编辑内容。</summary>
    private readonly Dictionary<string, (string Note, string Folder)> _drafts = [];
    /// <summary>可选头像服务，测试或无头像模式保持首字显示。</summary>
    private readonly IAccountAvatarService? _avatars;
    /// <summary>页面关闭时取消仍在进行的头像请求。</summary>
    private readonly CancellationTokenSource _avatarCancellation = new();
    /// <summary>每个账号唯一的后台任务，排序和筛选不重复请求。</summary>
    private readonly Dictionary<string, Task> _avatarTasks = [];
    /// <summary>跨异步完成任务复用的本地路径或已确认的首字回退结果。</summary>
    private readonly ConcurrentDictionary<string, string?> _avatarPaths = new(StringComparer.Ordinal);
    /// <summary>当前完整可见偏好快照中的账号，包含暂被搜索排除的账号。</summary>
    private AccountItem[] _allAccounts = [];
    /// <summary>页面是否已结束，迟到响应不再更新绑定。</summary>
    private bool _disposed;

    /// <summary>供账号页绑定共享安装、忙碌和操作状态的工作区。</summary>
    public WorkspaceService Workspace { get; }
    /// <summary>按隐藏偏好过滤后的真实 Steam 账号。</summary>
    public ObservableCollection<AccountItem> Accounts { get; } = [];
    /// <summary>资源绑定选项，保留已保存但当前失效的目录。</summary>
    public ObservableCollection<ResourceBindingOption> BindingOptions { get; } = [];
    /// <summary>保存备注和手工绑定的命令。</summary>
    public AsyncCommand SaveAccountCommand { get; }
    /// <summary>切换工具内隐藏状态的命令。</summary>
    public AsyncCommand HideAccountCommand { get; }
    /// <summary>显示通过 Steam 正常添加账号的操作说明。</summary>
    public AsyncCommand AddAccountCommand { get; }
    /// <summary>按绑定关联资源并通过 Steam 切换启动的命令。</summary>
    public AsyncCommand SwitchAndLaunchCommand { get; }
    /// <summary>更新指定行或当前账号星标偏好的命令。</summary>
    public AsyncCommand ToggleStarCommand { get; }
    /// <summary>本次已安排头像加载的可观察完成任务，工作区初始化保持独立。</summary>
    public Task AvatarLoadingTask { get; private set; } = Task.CompletedTask;
    /// <summary>账号列表即时搜索的原始文字，仅保留在本次运行内存中。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                RebuildAccounts(SelectedAccount?.Account.SteamId);
                Notify(nameof(EmptyTitle), nameof(EmptyDescription));
            }
        }
    }
    /// <summary>账号空列表向用户展示的标题。</summary>
    public string EmptyTitle => string.IsNullOrWhiteSpace(SearchText) ? "还没有可显示的账号" : "没有匹配的账号";
    /// <summary>账号空列表向用户展示的下一步说明。</summary>
    public string EmptyDescription => string.IsNullOrWhiteSpace(SearchText)
        ? "在 Steam 正常登录并记住账号，然后点击刷新。路径未检测到时，请先打开设置。" : "尝试其他关键词。";
    /// <summary>账号列表的可见数量和发现总数。</summary>
    public string AccountCountText => $"本机账号 · {Accounts.Count}/{Workspace.DetectedAccounts.Count}";
    /// <summary>账号列表是否为空。</summary>
    public bool NoAccounts => Accounts.Count == 0;
    /// <summary>是否已有账号供编辑和启动。</summary>
    public bool HasSelectedAccount => SelectedAccount is not null;
    /// <summary>控制隐藏账号是否显示，并保持仍可见的当前选择。</summary>
    public bool ShowHidden
    {
        get => _showHidden;
        set { if (Set(ref _showHidden, value)) RebuildAccounts(SelectedAccount?.Account.SteamId); }
    }
    /// <summary>所选账号发生变化时填入其备注和资源绑定。</summary>
    public AccountItem? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (value is null && _selectedAccount is not null && !Accounts.Contains(_selectedAccount)
                && Workspace.DetectedAccounts.Any(account => account.SteamId == _selectedAccount.Account.SteamId)) return;
            SaveCurrentDraft();
            SetSelection(value);
        }
    }
    /// <summary>等待保存的账号备注。</summary>
    public string Note { get => _note; set => Set(ref _note, value); }
    /// <summary>等待保存的人工资源绑定。</summary>
    public ResourceBindingOption? SelectedBinding { get => _selectedBinding; set => Set(ref _selectedBinding, value); }

    /// <summary>从已有工作区快照建立账号页，并接收后续跨页刷新。</summary>
    public AccountsPageViewModel(WorkspaceService workspace, ISettingsStore store, ISteamAccountService steam,
        IResourceSharingService resources, IUserInteraction interaction, IAccountAvatarService? avatars = null)
    {
        Workspace = workspace;
        _store = store;
        _steam = steam;
        _resources = resources;
        _avatars = avatars;
        SaveAccountCommand = Workspace.CreateCommand(nameof(SaveAccountCommand), _ => Workspace.RunOperationAsync("正在保存账号信息…", () =>
        {
            var id = SaveAccount();
            RebuildAccounts(id, false);
            Workspace.AddLog("账号信息已保存。");
            return Task.CompletedTask;
        }));
        HideAccountCommand = Workspace.CreateCommand(nameof(HideAccountCommand), _ => Workspace.RunOperationAsync("正在更新账号列表…", HideAccountAsync));
        AddAccountCommand = Workspace.CreateCommand(nameof(AddAccountCommand), _ => interaction.ShowNoticeAsync("添加 Steam 账号",
            "请在 Steam 使用“更改账号”正常登录另一个账号，并记住登录状态。完成后返回工具点击“刷新”。"));
        SwitchAndLaunchCommand = Workspace.CreateCommand(nameof(SwitchAndLaunchCommand), _ => Workspace.RunOperationAsync("正在切换账号并启动 Master Duel…", SwitchAndLaunchAsync));
        ToggleStarCommand = Workspace.CreateCommand(nameof(ToggleStarCommand), parameter => Workspace.RunOperationAsync("正在更新账号星标…", () => ToggleStarAsync(parameter)));
        RebuildAccounts(null);
        Workspace.DataChanged += OnDataChanged;
    }

    /// <summary>响应工作区最新数据，同时保留仍存在的选中账号。</summary>
    private void OnDataChanged(object? sender, EventArgs args) => RebuildAccounts(SelectedAccount?.Account.SteamId);

    /// <summary>使用当前快照按星标、最近登录和稳定标识排序，再应用隐藏偏好与即时搜索。</summary>
    private void RebuildAccounts(string? selectedId, bool preserveDraft = true)
    {
        if (preserveDraft) SaveCurrentDraft();
        var items = Workspace.DetectedAccounts
            .Where(account => ShowHidden || !Workspace.Settings.HiddenAccounts.Contains(account.SteamId))
            .Select(account => new AccountItem
            {
                Account = account,
                Note = Workspace.Settings.AccountNotes.GetValueOrDefault(account.SteamId, ""),
                ResourceFolder = Workspace.Settings.AccountBindings.GetValueOrDefault(account.SteamId, ""),
                IsHidden = Workspace.Settings.HiddenAccounts.Contains(account.SteamId),
                IsStarred = Workspace.Settings.StarredAccounts.Contains(account.SteamId)
            })
            .OrderByDescending(item => item.IsStarred)
            .ThenByDescending(item => item.Account.MostRecent)
            .ThenByDescending(item => item.Account.LastLoginTimestamp)
            .ThenBy(item => item.Account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Account.SteamId, StringComparer.Ordinal)
            .ToArray();
        var terms = SearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _allAccounts = items;
        Replace(Accounts, items.Where(item => MatchesSearch(item, terms)));
        SetSelection(items.FirstOrDefault(item => item.Account.SteamId == selectedId));
        LoadAvatars();
        Notify(nameof(NoAccounts), nameof(AccountCountText));
    }

    /// <summary>所有词均可在昵称、登录名、账号标识或保存的备注中按顺序找到才匹配。</summary>
    private static bool MatchesSearch(AccountItem item, string[] terms)
    {
        var values = new[] { item.Account.DisplayName, item.Account.AccountName, item.Account.SteamId, item.Note };
        return terms.All(term => values.Any(value => MatchesSubsequence(value, term)));
    }

    /// <summary>用大小写不敏感的有序子序列匹配，连续子串自然包含在匹配结果中。</summary>
    private static bool MatchesSubsequence(string value, string term)
    {
        var position = 0;
        foreach (var character in term)
        {
            var match = value.IndexOf(character.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (match < 0) return false;
            position = match + 1;
        }
        return true;
    }

    /// <summary>只缓存实际修改的编辑字段，保留新刷新快照中未编辑的持久偏好。</summary>
    private void SaveCurrentDraft()
    {
        if (SelectedAccount is not { } selected) return;
        var folder = SelectedBinding?.FolderName ?? "";
        if (Note != selected.Note || folder != selected.ResourceFolder)
            _drafts[selected.Account.SteamId] = (Note, folder);
        else _drafts.Remove(selected.Account.SteamId);
    }

    /// <summary>内部发布真实选中状态，允许明确隐藏或账号消失时清空编辑器。</summary>
    private void SetSelection(AccountItem? account)
    {
        if (Set(ref _selectedAccount, account, nameof(SelectedAccount)))
        {
            LoadAccountEditor();
            Notify(nameof(HasSelectedAccount));
        }
    }

    /// <summary>加载人工信息；失效绑定继续显示，直到用户明确重新选择。</summary>
    private void LoadAccountEditor()
    {
        var id = SelectedAccount?.Account.SteamId;
        var hasDraft = id is not null && _drafts.ContainsKey(id);
        Note = hasDraft ? _drafts[id!].Note : SelectedAccount?.Note ?? "";
        BindingOptions.Clear();
        BindingOptions.Add(new ResourceBindingOption());
        foreach (var profile in Workspace.Profiles)
            BindingOptions.Add(new ResourceBindingOption { FolderName = profile.FolderName, DisplayName = profile.DisplayName });
        var folder = hasDraft ? _drafts[id!].Folder : SelectedAccount?.ResourceFolder ?? "";
        if (folder.Length != 0 && !BindingOptions.Any(option => option.FolderName == folder))
            BindingOptions.Add(new ResourceBindingOption { FolderName = folder, DisplayName = $"{folder} · 未检测到，请重新绑定" });
        SelectedBinding = BindingOptions.First(option => option.FolderName == folder);
    }

    /// <summary>保存所选账号；失败时恢复原偏好键值并保留编辑草稿，仅未绑定项删除资源映射。</summary>
    private string SaveAccount()
    {
        Workspace.EnsureStorageReady();
        var selected = RequireAccount();
        var id = selected.Account.SteamId;
        var notes = Workspace.Settings.AccountNotes;
        var bindings = Workspace.Settings.AccountBindings;
        var hadNote = notes.TryGetValue(id, out var oldNote);
        var hadBinding = bindings.TryGetValue(id, out var oldBinding);
        notes[id] = Note.Trim();
        var folder = SelectedBinding?.FolderName ?? "";
        if (folder.Length == 0) bindings.Remove(id);
        else bindings[id] = folder;
        try { _store.Save(Workspace.Settings); }
        catch
        {
            if (hadNote) notes[id] = oldNote!;
            else notes.Remove(id);
            if (hadBinding) bindings[id] = oldBinding!;
            else bindings.Remove(id);
            throw;
        }
        _drafts.Remove(id);
        return id;
    }

    /// <summary>保存指定账号星标，异常时恢复原集合状态，使同一按钮重试保持原意图。</summary>
    private Task ToggleStarAsync(object? parameter)
    {
        Workspace.EnsureStorageReady();
        var account = parameter as AccountItem ?? RequireAccount();
        var id = account.Account.SteamId;
        if (!Workspace.DetectedAccounts.Any(item => item.SteamId == id))
            throw new InvalidOperationException("该 Steam 账号已不在当前发现列表中，请刷新后重试。");
        var wasStarred = Workspace.Settings.StarredAccounts.Contains(id);
        if (wasStarred) Workspace.Settings.StarredAccounts.Remove(id);
        else Workspace.Settings.StarredAccounts.Add(id);
        try { _store.Save(Workspace.Settings); }
        catch
        {
            if (wasStarred) Workspace.Settings.StarredAccounts.Add(id);
            else Workspace.Settings.StarredAccounts.Remove(id);
            throw;
        }
        RebuildAccounts(SelectedAccount?.Account.SteamId);
        Workspace.AddLog("账号星标已更新。");
        return Task.CompletedTask;
    }

    /// <summary>只修改本地账号可见性，并同步当前列表。</summary>
    private Task HideAccountAsync()
    {
        Workspace.EnsureStorageReady();
        var selected = RequireAccount();
        var id = selected.Account.SteamId;
        var wasHidden = Workspace.Settings.HiddenAccounts.Contains(id);
        if (wasHidden) Workspace.Settings.HiddenAccounts.Remove(id);
        else Workspace.Settings.HiddenAccounts.Add(id);
        try { _store.Save(Workspace.Settings); }
        catch
        {
            if (wasHidden) Workspace.Settings.HiddenAccounts.Add(id);
            else Workspace.Settings.HiddenAccounts.Remove(id);
            throw;
        }
        RebuildAccounts(selected.Account.SteamId);
        Workspace.AddLog("账号列表已更新；Steam 账号记录保持原样。");
        return Task.CompletedTask;
    }

    /// <summary>校验绑定和来源，再关联资源、切换 Steam 并刷新共享快照。</summary>
    private async Task SwitchAndLaunchAsync()
    {
        Workspace.EnsureSteamPath();
        var account = RequireAccount().Account;
        SaveAccount();
        var target = Workspace.Settings.AccountBindings.GetValueOrDefault(account.SteamId, "");
        if (target.Length != 0 && !Workspace.Profiles.Any(profile => profile.FolderName == target))
            throw new InvalidOperationException("绑定的资源目录已不存在。请刷新并重新绑定。");
        if (Workspace.Settings.SourceProfile.Length != 0 && target.Length != 0 && !target.Equals(Workspace.Settings.SourceProfile, StringComparison.OrdinalIgnoreCase))
        {
            var source = RequireSource(Workspace.Settings.SourceProfile);
            await Task.Run(() => _resources.EnableSharing(Workspace.ActiveGamePath, source.FolderName, new[] { target }));
            Workspace.AddLog("已检查并关联当前账号资源。");
        }
        else if (Workspace.Settings.SourceProfile.Length != 0 && target.Length == 0)
            Workspace.AddLog("本账号未绑定资源目录，本次直接通过 Steam 启动。");
        var result = await _steam.SwitchAndLaunchAsync(Workspace.ActiveSteamPath, account);
        await Workspace.RefreshDataAsync();
        Workspace.AddLog(result);
    }

    /// <summary>要求选中一个当前列表中的 Steam 账号。</summary>
    private AccountItem RequireAccount() => SelectedAccount ?? throw new InvalidOperationException("请先选择一个 Steam 账号。");

    /// <summary>要求工作区在已发现游戏目录中扫描的来源仍为已下载资源的独立目录。</summary>
    private ResourceProfile RequireSource(string folder)
    {
        var source = Workspace.Profiles.FirstOrDefault(profile => profile.FolderName == folder);
        if (source is null || source.IsLinked || source.Bytes <= 0)
            throw new InvalidOperationException("资源来源已改变或尚未完成下载。请刷新并选择有资源的独立目录。");
        return source;
    }

    /// <summary>安排每个账号唯一的后台头像任务，复用已经完成的路径而不影响工作区忙碌状态。</summary>
    private void LoadAvatars()
    {
        if (_avatars is null || _disposed) return;
        foreach (var account in _allAccounts)
        {
            var id = account.Account.SteamId;
            if (_avatarPaths.TryGetValue(id, out var path)) account.AvatarPath = path;
            if (!_avatarTasks.ContainsKey(id)) _avatarTasks.Add(id, LoadAvatarAsync(id));
        }
        AvatarLoadingTask = Task.WhenAll(_avatarTasks.Values);
    }

    /// <summary>保留调用方界面上下文，在异步请求完成后更新同账号的当前绑定对象。</summary>
    private async Task LoadAvatarAsync(string steamId)
    {
        string? path;
        try { path = await _avatars!.GetAvatarPathAsync(Workspace.ActiveSteamPath, steamId, _avatarCancellation.Token); }
        catch (OperationCanceledException) { return; }
        if (_disposed) return;
        _avatarPaths[steamId] = path;
        foreach (var account in _allAccounts)
            if (account.Account.SteamId == steamId) account.AvatarPath = path;
    }

    /// <summary>在图像控件报告解码失败时恢复首字，并记住本次运行内的失效缓存。</summary>
    public void ReportAvatarFailure(string steamId)
    {
        _avatarPaths[steamId] = null;
        foreach (var account in _allAccounts)
            if (account.Account.SteamId == steamId) account.AvatarPath = null;
    }

    /// <summary>应用退出时结束页面订阅及头像后台请求。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Workspace.DataChanged -= OnDataChanged;
        _avatarCancellation.Cancel();
        _avatarCancellation.Dispose();
    }
}
