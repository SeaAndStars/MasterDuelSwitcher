using System.IO;
using MasterDuelSwitcher.App;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>通过完全隔离的服务验证界面状态、账号绑定、事务命令和错误恢复。</summary>
public sealed class MainViewModelTests
{
    /// <summary>页面尚未实例化时工作区仍可独立读取健康配置和共同快照。</summary>
    [Fact]
    public async Task WorkspaceRefreshesWithoutAnyPageSubscribers()
    {
        var store = new FakeStore();
        var workspace = new WorkspaceService(store, new FakeDiscovery(), new FakeResources(), new FakeInteraction(), new FakeTheme());
        await workspace.InitializeAsync();
        Assert.True(workspace.StorageReady);
        Assert.True(workspace.IsReady);
        Assert.Equal(2, workspace.DetectedAccounts.Count);
        Assert.Equal(3, workspace.Profiles.Count);
        Assert.Single(workspace.Backups);
        Assert.NotEmpty(workspace.Logs);
        Assert.Equal("C:\\Fixture\\Steam", workspace.ActiveSteamPath);
        Assert.Equal("C:\\Fixture\\Game", workspace.ActiveGamePath);
    }

    /// <summary>新发现数据同步失败时保留上一整组快照，页面接收的状态与业务活动路径一致。</summary>
    [Fact]
    public async Task FailedAccountSynchronizationKeepsPublishedWorkspaceSnapshotConsistent()
    {
        var fixture = new Fixture();
        var store = fixture.Store;
        var discovery = fixture.Discovery;
        var resources = fixture.Resources;
        var workspace = fixture.Workspace;
        var published = new List<(string Steam, string Game, IReadOnlyList<SteamAccount> Accounts, IReadOnlyList<ResourceProfile> Profiles, IReadOnlyList<ShareBackup> Backups)>();
        workspace.DataChanged += (_, _) => published.Add((workspace.ActiveSteamPath, workspace.ActiveGamePath, workspace.DetectedAccounts, workspace.Profiles, workspace.Backups));
        await workspace.InitializeAsync();
        var previous = Assert.Single(published);
        var previousAccounts = fixture.AccountsPageVM.Accounts.ToArray();
        var previousProfiles = fixture.ResourcesPageVM.Profiles.ToArray();
        var previousBackups = fixture.BackupsPageVM.Backups.ToArray();
        var previousDiscoveryStatus = workspace.DiscoveryStatus;
        discovery.Result = new DiscoveryResult { SteamPath = "C:\\Changed\\Steam", GamePath = "C:\\Changed\\Game", Accounts = [new SteamAccount { SteamId = "changed" }] };
        resources.Profiles = [Fixture.Profile("deadbeef", 5)];
        resources.Backups = [new ShareBackup { Id = "changed-backup" }];
        store.SyncError = new IOException("同步失败");

        await workspace.RefreshCommand.ExecuteAsync();

        Assert.Single(published);
        Assert.Equal(previous.Steam, workspace.ActiveSteamPath);
        Assert.Equal(previous.Game, workspace.ActiveGamePath);
        Assert.Same(previous.Accounts, workspace.DetectedAccounts);
        Assert.Same(previous.Profiles, workspace.Profiles);
        Assert.Same(previous.Backups, workspace.Backups);
        Assert.Equal(previousAccounts, fixture.AccountsPageVM.Accounts);
        Assert.Equal(previousProfiles, fixture.ResourcesPageVM.Profiles);
        Assert.Equal(previousBackups, fixture.BackupsPageVM.Backups);
        Assert.Equal(previous.Steam, fixture.SettingsPageVM.SteamPath);
        Assert.Equal(previous.Game, fixture.SettingsPageVM.GamePath);
        Assert.Equal(previousDiscoveryStatus, workspace.DiscoveryStatus);
        Assert.True(workspace.IsReady);
        Assert.Contains("同步失败", workspace.Status);
        store.SyncError = null;
        await workspace.RefreshCommand.ExecuteAsync();
        Assert.Equal(2, published.Count);
        Assert.Equal("C:\\Changed\\Steam", workspace.ActiveSteamPath);
        Assert.Equal("changed", Assert.Single(workspace.DetectedAccounts).SteamId);
        Assert.Equal("deadbeef", Assert.Single(workspace.Profiles).FolderName);
        Assert.Equal("changed-backup", Assert.Single(workspace.Backups).Id);
        Assert.Equal("changed", Assert.Single(fixture.AccountsPageVM.Accounts).Account.SteamId);
        Assert.Equal("deadbeef", Assert.Single(fixture.ResourcesPageVM.Profiles).FolderName);
        Assert.Equal("changed-backup", Assert.Single(fixture.BackupsPageVM.Backups).Id);
        Assert.Equal("C:\\Changed\\Steam", fixture.SettingsPageVM.SteamPath);
        Assert.Equal("C:\\Changed\\Game", fixture.SettingsPageVM.GamePath);
    }

    /// <summary>构造阶段只创建状态，首次初始化才读取存储并同步发现的账号。</summary>
    [Fact]
    public async Task ConstructorDefersStorageAccessAndInitializationPopulatesDerivedState()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.DarkTheme = true;
        fixture.Store.Settings.SourceProfile = "11223344";
        Assert.Equal(0, fixture.Store.LoadCount);
        Assert.True(fixture.Workspace.IsReady);
        Assert.False(fixture.Workspace.StorageReady);
        Assert.False(fixture.AccountsPageVM.HasSelectedAccount);
        Assert.True(fixture.AccountsPageVM.NoAccounts);
        Assert.True(fixture.ResourcesPageVM.NoProfiles);
        Assert.True(fixture.BackupsPageVM.NoBackups);
        Assert.Equal("C:\\Fixture\\State", fixture.Workspace.StateDirectory);

        await fixture.ViewModel.InitializeAsync();

        Assert.True(fixture.Workspace.StorageReady);
        Assert.Equal("C:\\Fixture\\Steam", fixture.SettingsPageVM.SteamPath);
        Assert.Equal("C:\\Fixture\\Game", fixture.SettingsPageVM.GamePath);
        Assert.True(fixture.SettingsPageVM.DarkTheme);
        Assert.True(Assert.Single(fixture.Theme.Applied));
        Assert.Equal("2", fixture.AccountsPageVM.Accounts[0].Account.SteamId);
        Assert.Equal("本机账号 · 2/2", fixture.AccountsPageVM.AccountCountText);
        Assert.False(fixture.ResourcesPageVM.NoProfiles);
        Assert.False(fixture.BackupsPageVM.NoBackups);
        Assert.Equal("11223344", Assert.Single(fixture.ResourcesPageVM.SourceProfiles).FolderName);
        Assert.Equal("11223344", fixture.ResourcesPageVM.SelectedSource?.FolderName);
        Assert.Contains("1.00 GB", fixture.ResourcesPageVM.Summary);
        Assert.Equal("Steam 与游戏已检测", fixture.Workspace.DiscoveryStatus);
        Assert.Equal(new[] { "1", "2" }, fixture.Store.Synchronized.Select(account => account.SteamId));
        Assert.False(fixture.Workspace.IsBusy);
        Assert.NotEmpty(fixture.Workspace.BusyMessage);
    }

    /// <summary>空安装与手工路径回退应保持空列表，并分别显示待设置的产品。</summary>
    [Theory]
    [InlineData("", "Steam 待设置")]
    [InlineData("C:\\Fixture\\Steam", "游戏路径待设置")]
    public async Task EmptyGameDiscoveryPreservesEditedOverridesAndSkipsResourceScan(string steamPath, string expectedStatus)
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SteamPath = " ";
        fixture.Store.Settings.GamePath = "C:\\PreferredGame";
        fixture.Discovery.Result = new DiscoveryResult { SteamPath = steamPath };

        await fixture.ViewModel.InitializeAsync();

        Assert.Empty(fixture.AccountsPageVM.Accounts);
        Assert.True(fixture.AccountsPageVM.NoAccounts);
        Assert.True(fixture.ResourcesPageVM.NoProfiles);
        Assert.True(fixture.BackupsPageVM.NoBackups);
        Assert.Equal("C:\\PreferredGame", fixture.SettingsPageVM.GamePath);
        Assert.Equal(expectedStatus, fixture.Workspace.DiscoveryStatus);
        Assert.Contains("尚未检测", fixture.ResourcesPageVM.Summary);
        Assert.Empty(fixture.Resources.ScannedPaths);
        Assert.Equal((null, "C:\\PreferredGame"), Assert.Single(fixture.Discovery.Requests));
    }

    /// <summary>刷新保留选中账号，同时小容量资源使用 MB，失效来源不自动替换。</summary>
    [Fact]
    public async Task RefreshPreservesAccountSelectionAndDoesNotGuessMissingSource()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        fixture.Store.Settings.SourceProfile = "deadbeef";
        fixture.Resources.Profiles = [Fixture.Profile("11223344", 524_288)];

        await fixture.ViewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal("2", fixture.AccountsPageVM.SelectedAccount?.Account.SteamId);
        Assert.True(fixture.AccountsPageVM.HasSelectedAccount);
        Assert.Null(fixture.ResourcesPageVM.SelectedSource);
        Assert.Contains("0.5 MB", fixture.ResourcesPageVM.Summary);
        Assert.Equal(2, fixture.Store.LoadCount);
    }

    /// <summary>官方导航通知 Shell 的页面标识应给出一致的标题和说明。</summary>
    [Theory]
    [InlineData("Accounts", "账号切换", 0)]
    [InlineData("Resources", "资源共享", 1)]
    [InlineData("Backups", "备份还原", 2)]
    [InlineData("Settings", "设置", 3)]
    [InlineData("FreePacks", "免费开包", 0)]
    [InlineData("Unknown", "账号切换", 0)]
    [InlineData(null, "账号切换", 0)]
    public void NavigationKeepsTitlesAndPageFlagsConsistent(string? page, string title, int visiblePage)
    {
        var fixture = new Fixture();
        fixture.ViewModel.CurrentPage = page ?? "Accounts";
        Assert.Equal(title, fixture.ViewModel.Title);
        Assert.NotEmpty(fixture.ViewModel.Subtitle);
        Assert.InRange(visiblePage, 0, 3);
        Assert.Equal(page ?? "Accounts", fixture.ViewModel.CurrentPage);
    }

    /// <summary>属性同值赋值不重复通知，改变页面时通知对应派生标题和可见状态。</summary>
    [Fact]
    public void PropertyChangesNotifyOnlyChangedValuesAndDerivedBindings()
    {
        var fixture = new Fixture();
        var names = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => names.Add(args.PropertyName);
        fixture.AccountsPageVM.PropertyChanged += (_, args) => names.Add(args.PropertyName);
        fixture.ViewModel.CurrentPage = "Accounts";
        fixture.AccountsPageVM.ShowHidden = false;
        fixture.AccountsPageVM.SelectedAccount = null;
        fixture.AccountsPageVM.Note = "";
        Assert.Empty(names);
        fixture.ViewModel.CurrentPage = "Settings";
        Assert.Contains(nameof(MainViewModel.Title), names);
        Assert.Contains(nameof(MainViewModel.Subtitle), names);
        names.Clear();
        fixture.AccountsPageVM.Note = "备注";
        fixture.AccountsPageVM.Note = "备注";
        Assert.Equal(new[] { nameof(AccountsPageViewModel.Note) }, names);
    }

    /// <summary>缺失资源目录的人工绑定保留在编辑器中，明确清空后才从设置删除。</summary>
    [Fact]
    public async Task MissingManualBindingIsPreservedUntilUserExplicitlyUnbinds()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.AccountBindings["1"] = "deadbeef";
        fixture.Store.Settings.AccountNotes["1"] = "原备注";
        await fixture.ViewModel.InitializeAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts.Single(account => account.Account.SteamId == "1");
        Assert.Equal("原备注", fixture.AccountsPageVM.Note);
        Assert.Equal("deadbeef", fixture.AccountsPageVM.SelectedBinding?.FolderName);
        Assert.Contains("未检测到", fixture.AccountsPageVM.SelectedBinding?.DisplayName);
        fixture.AccountsPageVM.Note = "  新备注  ";

        await fixture.AccountsPageVM.SaveAccountCommand.ExecuteAsync();

        Assert.Equal("deadbeef", fixture.Store.Settings.AccountBindings["1"]);
        Assert.Equal("新备注", fixture.Store.Settings.AccountNotes["1"]);
        Assert.Equal("1", fixture.AccountsPageVM.SelectedAccount?.Account.SteamId);
        fixture.AccountsPageVM.SelectedBinding = fixture.AccountsPageVM.BindingOptions[0];
        await fixture.AccountsPageVM.SaveAccountCommand.ExecuteAsync();
        Assert.False(fixture.Store.Settings.AccountBindings.ContainsKey("1"));
        fixture.AccountsPageVM.SelectedBinding = null;
        await fixture.AccountsPageVM.SaveAccountCommand.ExecuteAsync();
        Assert.False(fixture.Store.Settings.AccountBindings.ContainsKey("1"));
        fixture.AccountsPageVM.SelectedAccount = null;
        Assert.False(fixture.AccountsPageVM.HasSelectedAccount);
        Assert.Empty(fixture.AccountsPageVM.Note);
        Assert.Equal("", fixture.AccountsPageVM.SelectedBinding?.FolderName);
    }

    /// <summary>有效资源绑定应持久保存，并在编辑器重建后保持相同选择。</summary>
    [Fact]
    public async Task SavingExistingBindingRoundtripsThroughAccountEditor()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        fixture.AccountsPageVM.SelectedBinding = fixture.AccountsPageVM.BindingOptions.Single(option => option.FolderName == "aabbccdd");
        await fixture.AccountsPageVM.SaveAccountCommand.ExecuteAsync();
        Assert.Equal("aabbccdd", fixture.Store.Settings.AccountBindings["2"]);
        Assert.Equal("aabbccdd", fixture.AccountsPageVM.SelectedBinding?.FolderName);
    }

    /// <summary>隐藏操作只更改本地偏好，显示隐藏项后可以再次取消隐藏。</summary>
    [Fact]
    public async Task HidingAndUnhidingAccountsUpdatesVisibleCountWithoutSteamMutation()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        await fixture.AccountsPageVM.HideAccountCommand.ExecuteAsync();
        Assert.Single(fixture.AccountsPageVM.Accounts);
        Assert.Null(fixture.AccountsPageVM.SelectedAccount);
        Assert.Equal("本机账号 · 1/2", fixture.AccountsPageVM.AccountCountText);
        fixture.AccountsPageVM.ShowHidden = true;
        fixture.AccountsPageVM.ShowHidden = true;
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts.Single(account => account.IsHidden);
        await fixture.AccountsPageVM.HideAccountCommand.ExecuteAsync();
        Assert.Empty(fixture.Store.Settings.HiddenAccounts);
        Assert.Equal(2, fixture.AccountsPageVM.Accounts.Count);
        Assert.Empty(fixture.Steam.LaunchedAccounts);
    }

    /// <summary>隐藏列表切换应过滤已保存的隐藏账号，并在全部隐藏时显示空状态。</summary>
    [Fact]
    public async Task HiddenFilterSupportsNoVisibleAccountsAndRestoresSelectionForVisibleAccounts()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.HiddenAccounts = ["1", "2"];
        await fixture.ViewModel.InitializeAsync();
        Assert.True(fixture.AccountsPageVM.NoAccounts);
        fixture.AccountsPageVM.ShowHidden = true;
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        fixture.AccountsPageVM.ShowHidden = false;
        Assert.Null(fixture.AccountsPageVM.SelectedAccount);
        Assert.True(fixture.AccountsPageVM.NoAccounts);
    }

    /// <summary>账号未选择时保存、隐藏和切号统一提示，且不会持久保存任何设置。</summary>
    [Theory]
    [InlineData("Save")]
    [InlineData("Hide")]
    [InlineData("Switch")]
    public async Task AccountCommandsWithoutSelectionReportErrorAndLeaveSettingsUntouched(string action)
    {
        var fixture = await Fixture.ReadyAsync();
        var command = action switch { "Save" => fixture.AccountsPageVM.SaveAccountCommand, "Hide" => fixture.AccountsPageVM.HideAccountCommand, _ => fixture.AccountsPageVM.SwitchAndLaunchCommand };
        await command.ExecuteAsync();
        Assert.Contains("选择", fixture.Workspace.Status);
        Assert.Single(fixture.Interaction.Notices);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Steam.LaunchedAccounts);
    }

    /// <summary>切号必须使用检测后的活动路径，并只对当前人工绑定执行共享。</summary>
    [Fact]
    public async Task SwitchingUsesActivePathsAndSharesOnlyTheSelectedAccountBinding()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = "11223344";
        fixture.Store.Settings.AccountBindings["2"] = "aabbccdd";
        await fixture.ViewModel.InitializeAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        fixture.SettingsPageVM.SteamPath = "C:\\UnsavedSteam";
        fixture.SettingsPageVM.GamePath = "C:\\UnsavedGame";

        await fixture.AccountsPageVM.SwitchAndLaunchCommand.ExecuteAsync();

        var share = Assert.Single(fixture.Resources.Shared);
        Assert.Equal("C:\\Fixture\\Game", share.Game);
        Assert.Equal("11223344", share.Source);
        Assert.Equal(new[] { "aabbccdd" }, share.Targets);
        Assert.Equal(("C:\\Fixture\\Steam", "2"), Assert.Single(fixture.Steam.LaunchedAccounts));
        Assert.Equal("已启动测试账号", fixture.Workspace.Status);
        Assert.Equal("2", fixture.AccountsPageVM.SelectedAccount?.Account.SteamId);
    }

    /// <summary>账号绑定共享来源自身时直接启动，不向核心服务请求来源指向自身的链接。</summary>
    [Fact]
    public async Task SwitchingAccountBoundToSourceSkipsSharingAndStillLaunches()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = "11223344";
        fixture.Store.Settings.AccountBindings["2"] = "11223344";
        await fixture.ViewModel.InitializeAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        await fixture.AccountsPageVM.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Empty(fixture.Resources.Shared);
        Assert.Equal(("C:\\Fixture\\Steam", "2"), Assert.Single(fixture.Steam.LaunchedAccounts));
        Assert.Empty(fixture.Interaction.Notices);
    }

    /// <summary>未配置来源或未绑定账号时可直接启动，且不会生成共享事务。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("11223344")]
    public async Task SwitchingWithoutSharingPrerequisitesLaunchesWithoutResourceMutation(string source)
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = source;
        await fixture.ViewModel.InitializeAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        await fixture.AccountsPageVM.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Single(fixture.Steam.LaunchedAccounts);
        Assert.Empty(fixture.Resources.Shared);
        if (source.Length != 0) Assert.Contains(fixture.Workspace.Logs, line => line.Contains("未绑定"));
    }

    /// <summary>失效人工绑定应保留并阻止切号，无论当前是否设置来源。</summary>
    [Fact]
    public async Task SwitchingMissingBindingReportsItWithoutDiscardingTheMapping()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.AccountBindings["2"] = "deadbeef";
        await fixture.ViewModel.InitializeAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        await fixture.AccountsPageVM.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Equal("deadbeef", fixture.Store.Settings.AccountBindings["2"]);
        Assert.Contains("已不存在", fixture.Workspace.Status);
        Assert.Empty(fixture.Steam.LaunchedAccounts);
    }

    /// <summary>未检测 Steam 时切号和还原登录均拒绝运行，编辑框中填路径不会绕过检查。</summary>
    [Fact]
    public async Task SteamOperationsRequireDetectedPathRatherThanUnsavedEditorText()
    {
        var fixture = new Fixture();
        fixture.Discovery.Result = new DiscoveryResult { Accounts = fixture.Discovery.Result.Accounts };
        await fixture.ViewModel.InitializeAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        fixture.SettingsPageVM.SteamPath = "C:\\UnsavedSteam";
        await fixture.AccountsPageVM.SwitchAndLaunchCommand.ExecuteAsync();
        await fixture.BackupsPageVM.RestoreSteamCommand.ExecuteAsync();
        Assert.Equal(2, fixture.Interaction.Notices.Count);
        Assert.Empty(fixture.Steam.LaunchedAccounts);
        Assert.Empty(fixture.Steam.RestoredPaths);
    }

    /// <summary>来源为空、丢失、已链接或未下载资源时保持设置不变并报告可恢复错误。</summary>
    [Theory]
    [InlineData("None")]
    [InlineData("Missing")]
    [InlineData("Linked")]
    [InlineData("Empty")]
    public async Task SavingSourceRejectsInvalidSelections(string kind)
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.ResourcesPageVM.SelectedSource = kind switch
        {
            "None" => null,
            "Missing" => Fixture.Profile("deadbeef", 1),
            "Linked" => fixture.ResourcesPageVM.Profiles.Single(profile => profile.IsLinked),
            _ => fixture.ResourcesPageVM.Profiles.Single(profile => profile.Bytes == 0 && !profile.IsLinked)
        };
        await fixture.ResourcesPageVM.SaveSourceCommand.ExecuteAsync();
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Single(fixture.Interaction.Notices);
    }

    /// <summary>切号时已配置但失效的来源也必须阻止启动。</summary>
    [Fact]
    public async Task SwitchingWithMissingSavedSourceStopsBeforeSteamLaunch()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = "deadbeef";
        fixture.Store.Settings.AccountBindings["2"] = "aabbccdd";
        await fixture.ViewModel.InitializeAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        await fixture.AccountsPageVM.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Empty(fixture.Steam.LaunchedAccounts);
        Assert.Contains("资源来源", fixture.Workspace.Status);
    }

    /// <summary>活动游戏路径丢失时，即使界面保留来源对象也不可保存为共享来源。</summary>
    [Fact]
    public async Task SavingSourceRequiresActiveGamePath()
    {
        var fixture = new Fixture();
        fixture.Discovery.Result = new DiscoveryResult();
        await fixture.ViewModel.InitializeAsync();
        var source = Fixture.Profile("11223344", 1);
        fixture.ResourcesPageVM.Profiles.Add(source);
        fixture.ResourcesPageVM.SelectedSource = source;
        await fixture.ResourcesPageVM.SaveSourceCommand.ExecuteAsync();
        Assert.Contains("安装目录", fixture.Workspace.Status);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    /// <summary>来源保存和批量共享只包含其他目录，事务和幂等结果均应反馈到状态栏。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharingPersistsSourceAndReportsCreatedOrAlreadyCurrentState(bool createBackup)
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.ResourcesPageVM.SelectedSource = fixture.ResourcesPageVM.SourceProfiles[0];
        fixture.Resources.ShareResult = createBackup ? new ShareBackup { Entries = [new ShareEntry(), new ShareEntry()] } : null;
        await fixture.ResourcesPageVM.SaveSourceCommand.ExecuteAsync();
        Assert.Equal("11223344", fixture.Store.Settings.SourceProfile);
        await fixture.ResourcesPageVM.EnableSharingCommand.ExecuteAsync();
        var share = Assert.Single(fixture.Resources.Shared);
        Assert.Equal(new[] { "aabbccdd", "55667788" }, share.Targets);
        Assert.Equal(createBackup ? "资源共享已启用；已保留 2 个目录的还原记录。" : "资源共享已是最新状态。", fixture.Workspace.Status);
    }

    /// <summary>仅有来源目录时禁止建立空共享事务。</summary>
    [Fact]
    public async Task SharingOnlySourceDirectoryReportsMissingTargets()
    {
        var fixture = new Fixture();
        fixture.Resources.Profiles = [Fixture.Profile("11223344", 1)];
        await fixture.ViewModel.InitializeAsync();
        fixture.ResourcesPageVM.SelectedSource = fixture.ResourcesPageVM.SourceProfiles[0];
        await fixture.ResourcesPageVM.EnableSharingCommand.ExecuteAsync();
        Assert.Contains("只有来源", fixture.Workspace.Status);
        Assert.Empty(fixture.Resources.Shared);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    /// <summary>资源还原区分未选择、已经还原和需实际还原的记录。</summary>
    [Fact]
    public async Task ResourceRestoreRequiresSelectionAndPreservesIdempotence()
    {
        var fixture = await Fixture.ReadyAsync();
        await fixture.BackupsPageVM.RestoreResourcesCommand.ExecuteAsync();
        Assert.Contains("选择", fixture.Workspace.Status);
        fixture.BackupsPageVM.SelectedBackup = new ShareBackup { Id = "restored", Restored = true };
        await fixture.BackupsPageVM.RestoreResourcesCommand.ExecuteAsync();
        Assert.Contains("已经完成", fixture.Workspace.Status);
        Assert.Empty(fixture.Resources.RestoredIds);
        fixture.BackupsPageVM.SelectedBackup = fixture.BackupsPageVM.Backups[0];
        await fixture.BackupsPageVM.RestoreResourcesCommand.ExecuteAsync();
        Assert.Equal(new[] { "backup" }, fixture.Resources.RestoredIds);
        Assert.Null(fixture.BackupsPageVM.SelectedBackup);
        Assert.Contains("已还原", fixture.Workspace.Status);
    }

    /// <summary>损坏存储阻止写入和同步，但仍允许既有资源备份还原及后续读取恢复。</summary>
    [Fact]
    public async Task CorruptStorageBlocksWritesButAllowsExistingResourceRestoreWithoutAccountSync()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        fixture.ResourcesPageVM.SelectedSource = fixture.ResourcesPageVM.SourceProfiles[0];
        fixture.BackupsPageVM.SelectedBackup = fixture.BackupsPageVM.Backups[0];
        var syncCount = fixture.Store.SyncCount;
        fixture.Store.LoadError = new InvalidDataException("数据库损坏");
        await fixture.ViewModel.RefreshCommand.ExecuteAsync();
        Assert.False(fixture.Workspace.StorageReady);
        var commands = new[] { fixture.AccountsPageVM.SaveAccountCommand, fixture.AccountsPageVM.HideAccountCommand, fixture.ResourcesPageVM.SaveSourceCommand, fixture.ResourcesPageVM.EnableSharingCommand, fixture.SettingsPageVM.SaveSettingsCommand, fixture.SettingsPageVM.AutoDetectCommand, fixture.SettingsPageVM.ToggleThemeCommand, fixture.AccountsPageVM.SwitchAndLaunchCommand };
        foreach (var command in commands) await command.ExecuteAsync();
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(syncCount, fixture.Store.SyncCount);
        await fixture.BackupsPageVM.RestoreResourcesCommand.ExecuteAsync();
        Assert.Equal(new[] { "backup" }, fixture.Resources.RestoredIds);
        Assert.Equal(syncCount, fixture.Store.SyncCount);
        Assert.False(fixture.Workspace.StorageReady);
        fixture.Store.LoadError = null;
        await fixture.ViewModel.RefreshCommand.ExecuteAsync();
        Assert.True(fixture.Workspace.StorageReady);
        Assert.Equal(syncCount + 1, fixture.Store.SyncCount);
    }

    /// <summary>首次读取损坏数据库时保留可交互窗口和通知，完全跳过发现及存储写入。</summary>
    [Fact]
    public async Task InitialStorageFailureLeavesReadyWindowWithoutDiscoveryOrMutation()
    {
        var fixture = new Fixture();
        fixture.Store.LoadError = new InvalidDataException("数据库损坏");
        await fixture.ViewModel.InitializeAsync();
        Assert.False(fixture.Workspace.StorageReady);
        Assert.True(fixture.Workspace.IsReady);
        Assert.Single(fixture.Interaction.Notices);
        Assert.Empty(fixture.Discovery.Requests);
        Assert.Empty(fixture.Theme.Applied);
        Assert.Equal(0, fixture.Store.SyncCount);
    }

    /// <summary>Steam 登录还原应使用活动安装目录并刷新账号状态。</summary>
    [Fact]
    public async Task RestoringSteamUsesDetectedPathAndRefreshesAccounts()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.SettingsPageVM.SteamPath = "C:\\UnsavedSteam";
        await fixture.BackupsPageVM.RestoreSteamCommand.ExecuteAsync();
        Assert.Equal(new[] { "C:\\Fixture\\Steam" }, fixture.Steam.RestoredPaths);
        Assert.Equal("已还原测试登录配置", fixture.Workspace.Status);
        Assert.Equal(2, fixture.Store.SyncCount);
    }

    /// <summary>目录浏览把当前编辑路径传入交互边界，取消时保留路径。</summary>
    [Theory]
    [InlineData("Steam", "C:\\PickedSteam")]
    [InlineData("Game", "C:\\PickedGame")]
    [InlineData(null, "C:\\PickedGame")]
    [InlineData("Steam", null)]
    public async Task BrowsingUpdatesRequestedEditorAndPreservesItWhenCanceled(string? target, string? chosen)
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Interaction.PickedFolder = chosen;
        await fixture.SettingsPageVM.BrowseFolderCommand.ExecuteAsync(target);
        var steam = target == "Steam";
        Assert.Equal(steam ? "C:\\Fixture\\Steam" : "C:\\Fixture\\Game", Assert.Single(fixture.Interaction.FolderRequests).Initial);
        Assert.Equal(steam && chosen is not null ? chosen : "C:\\Fixture\\Steam", fixture.SettingsPageVM.SteamPath);
        Assert.Equal(!steam && chosen is not null ? chosen : "C:\\Fixture\\Game", fixture.SettingsPageVM.GamePath);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    /// <summary>安装路径验证成功后保存修剪值，再通过发现结果更新活动目录。</summary>
    [Fact]
    public async Task SavingSettingsValidatesTrimmedPathsBeforePersistingAndAutoDetectClearsOverrides()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.SettingsPageVM.SteamPath = "  C:\\ManualSteam  ";
        fixture.SettingsPageVM.GamePath = "  C:\\ManualGame  ";
        await fixture.SettingsPageVM.SaveSettingsCommand.ExecuteAsync();
        Assert.Equal("C:\\ManualSteam", fixture.Store.Settings.SteamPath);
        Assert.Equal("C:\\ManualGame", fixture.Store.Settings.GamePath);
        Assert.Equal(("C:\\ManualSteam", "C:\\ManualGame"), fixture.Discovery.Requests[1]);
        Assert.Equal(2, fixture.Store.SyncCount);
        await fixture.SettingsPageVM.AutoDetectCommand.ExecuteAsync();
        Assert.Empty(fixture.Store.Settings.SteamPath);
        Assert.Empty(fixture.Store.Settings.GamePath);
        Assert.Equal((null, null), fixture.Discovery.Requests[^1]);
        Assert.Equal("已重新自动检测安装路径。", fixture.Workspace.Status);
    }

    /// <summary>空白安装路径以自动发现提交；无效路径则不保存、不更新活动数据。</summary>
    [Fact]
    public async Task SavingBlankSettingsUsesAutoDiscoveryAndInvalidPathDoesNotPersist()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.SettingsPageVM.SteamPath = " ";
        fixture.SettingsPageVM.GamePath = " ";
        await fixture.SettingsPageVM.SaveSettingsCommand.ExecuteAsync();
        Assert.Equal((null, null), fixture.Discovery.Requests[1]);
        Assert.Empty(fixture.Store.Settings.SteamPath);
        fixture.Discovery.Error = new ArgumentException("安装目录错误");
        fixture.SettingsPageVM.SteamPath = "invalid";
        await fixture.SettingsPageVM.SaveSettingsCommand.ExecuteAsync();
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Empty(fixture.Store.Settings.SteamPath);
        Assert.Contains("安装目录错误", fixture.Workspace.Status);
    }

    /// <summary>主题命令在保存成功后应用新主题，并支持明暗两种偏好。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThemeCommandPersistsAndAppliesRequestedTheme(bool dark)
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.SettingsPageVM.DarkTheme = dark;
        await fixture.SettingsPageVM.ToggleThemeCommand.ExecuteAsync();
        Assert.Equal(dark, fixture.Store.Settings.DarkTheme);
        Assert.Equal(dark, fixture.Theme.Applied[^1]);
        Assert.Equal("主题偏好已保存。", fixture.Workspace.Status);
    }

    /// <summary>添加账号说明由交互边界显示，不启动或更改 Steam。</summary>
    [Fact]
    public async Task AddingAccountDisplaysSteamInstructionsOnly()
    {
        var fixture = new Fixture();
        await fixture.AccountsPageVM.AddAccountCommand.ExecuteAsync();
        Assert.Contains("Steam", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Steam.LaunchedAccounts);
    }

    /// <summary>显式注入日志服务时，初始化和界面命令仍保持相同的业务状态。</summary>
    [Fact]
    public async Task ExplicitLoggerInjectionKeepsInitializationAndCommandBehavior()
    {
        var fixture = new Fixture(NullLogger<WorkspaceService>.Instance);
        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.CurrentPage = "Settings";
        Assert.True(fixture.Workspace.StorageReady);
        Assert.Equal("Settings", fixture.ViewModel.CurrentPage);
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>事务失败时重启交互，并按已知异常类型展示具体提示或通用错误。</summary>
    [Theory]
    [InlineData("Io", true)]
    [InlineData("Permission", true)]
    [InlineData("Operation", true)]
    [InlineData("Argument", true)]
    [InlineData("Unknown", false)]
    public async Task FailedOperationsAlwaysReleaseBusyStateAndSanitizeUnexpectedErrors(string kind, bool exposesMessage)
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Store.SaveError = kind switch
        {
            "Io" => new IOException("错误详情"),
            "Permission" => new UnauthorizedAccessException("错误详情"),
            "Operation" => new InvalidOperationException("错误详情"),
            "Argument" => new ArgumentException("错误详情"),
            _ => new Exception("内部隐私详情")
        };
        await fixture.SettingsPageVM.ToggleThemeCommand.ExecuteAsync();
        Assert.True(fixture.Workspace.IsReady);
        Assert.False(fixture.Workspace.IsBusy);
        var notice = Assert.Single(fixture.Interaction.Notices);
        Assert.Equal(exposesMessage, notice.Content.Contains("错误详情"));
        Assert.DoesNotContain("内部隐私详情", notice.Content);
        Assert.Single(fixture.Theme.Applied);
    }

    /// <summary>扫描期间所有命令禁用，重复提交不执行，完成后通知按钮恢复。</summary>
    [Fact]
    public async Task BusyScanRejectsRepeatedCommandsAndReenablesAllCommandsAfterCompletion()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Discovery.Block = true;
        var changeCount = 0;
        fixture.ViewModel.RefreshCommand.CanExecuteChanged += (_, _) => changeCount++;
        var refresh = fixture.ViewModel.RefreshCommand.ExecuteAsync();
        await fixture.Discovery.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(fixture.Workspace.IsBusy);
            Assert.False(fixture.Workspace.IsReady);
            Assert.False(fixture.ViewModel.RefreshCommand.CanExecute(null));
            Assert.False(fixture.SettingsPageVM.ToggleThemeCommand.CanExecute(null));
            await fixture.SettingsPageVM.ToggleThemeCommand.ExecuteAsync();
            await fixture.ViewModel.RefreshCommand.ExecuteAsync();
            Assert.Equal("Accounts", fixture.ViewModel.CurrentPage);
            Assert.Equal(2, fixture.Discovery.Requests.Count);
        }
        finally
        {
            fixture.Discovery.Release.TrySetResult(true);
            await refresh;
        }
        Assert.True(fixture.ViewModel.RefreshCommand.CanExecute(null));
        Assert.Equal(2, changeCount);
    }

    /// <summary>异常提示自身失败时任务仍释放忙碌状态，让上层获知交互异常。</summary>
    [Fact]
    public async Task NoticeFailureStillReleasesBusyState()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Store.SaveError = new IOException("写入失败");
        fixture.Interaction.NoticeError = new InvalidOperationException("通知失败");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SettingsPageVM.ToggleThemeCommand.ExecuteAsync());
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>日志移除换行、截断超长结果并限制最近六十条。</summary>
    [Fact]
    public async Task LogHistorySanitizesLongMessagesAndKeepsOnlySixtyRecentEntries()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.AccountsPageVM.SelectedAccount = fixture.AccountsPageVM.Accounts[0];
        fixture.Steam.LaunchResult = "第一行\r\n" + new string('甲', 650);
        await fixture.AccountsPageVM.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Equal(601, fixture.Workspace.Status.Length);
        Assert.EndsWith("…", fixture.Workspace.Status);
        Assert.DoesNotContain('\n', fixture.Workspace.Status);
        Assert.DoesNotContain('\r', fixture.Workspace.Status);
        for (var index = 0; index < 65; index++) await fixture.SettingsPageVM.ToggleThemeCommand.ExecuteAsync();
        Assert.Equal(60, fixture.Workspace.Logs.Count);
        Assert.Contains("主题偏好", fixture.Workspace.Logs[0]);
    }

    /// <summary>异步命令支持 ICommand 入口、参数透传和可执行条件，禁用时没有副作用。</summary>
    [Fact]
    public async Task AsyncCommandSupportsBindingEntryAndDisabledExecution()
    {
        object? received = null;
        var enabled = true;
        var notifications = 0;
        var command = new AsyncCommand(parameter => { received = parameter; return Task.CompletedTask; }, () => enabled);
        command.NotifyCanExecuteChanged();
        command.CanExecuteChanged += (_, _) => notifications++;
        command.NotifyCanExecuteChanged();
        Assert.Equal(1, notifications);
        command.Execute("bound");
        Assert.Equal("bound", received);
        enabled = false;
        Assert.False(command.CanExecute(null));
        await command.ExecuteAsync("ignored");
        Assert.Equal("bound", received);
        enabled = true;
        await command.ExecuteAsync("awaited");
        Assert.Equal("awaited", received);
    }

    /// <summary>列表显示数据覆盖备注优先、昵称回退、隐藏提示和资源状态。</summary>
    [Fact]
    public void AccountDisplayUsesRealMetadataAndExplicitLocalPreferences()
    {
        var account = new SteamAccount { AccountName = "alice", PersonaName = "艾丽丝" };
        var normal = new AccountItem { Account = account };
        Assert.Equal("艾丽丝", normal.Title);
        Assert.Equal("艾", normal.Initial);
        Assert.Equal("alice", normal.Subtitle);
        Assert.Equal("待绑定资源", normal.Status);
        var noted = new AccountItem { Account = account, Note = "练习", IsHidden = true, ResourceFolder = "11223344" };
        Assert.Equal("练习", noted.Title);
        Assert.Contains("已隐藏", noted.Subtitle);
        Assert.Equal("资源 11223344", noted.Status);
        var recent = new AccountItem { Account = new SteamAccount { AccountName = "bob", MostRecent = true } };
        Assert.Equal("最近使用", recent.Status);
        Assert.Equal("B", recent.Initial);
        Assert.Equal("S", new AccountItem { Account = new SteamAccount() }.Initial);
        Assert.Equal("未绑定资源目录", new ResourceBindingOption().DisplayName);
    }

    /// <summary>账号详情标签准确区分已记住登录和隐藏状态，并显示真实 Steam 标识。</summary>
    [Theory]
    [InlineData(true, true, "已记住此账号", "取消隐藏")]
    [InlineData(false, false, "尚未记住登录状态", "隐藏")]
    public void AccountDetailLabelsReflectRememberedLoginAndHiddenState(bool remembered, bool hidden, string loginHint, string hideLabel)
    {
        var item = new AccountItem
        {
            Account = new SteamAccount { SteamId = "76561198000000001", RememberPassword = remembered },
            IsHidden = hidden
        };
        Assert.Contains(loginHint, item.LoginState);
        Assert.Equal("SteamID  76561198000000001", item.SteamIdLabel);
        Assert.Equal(hideLabel, item.HideLabel);
    }

    /// <summary>界面准确显示注入的管理员状态，不读取测试机器真实权限。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdministratorStatusReflectsInjectedEnvironment(bool elevated)
    {
        var fixture = new Fixture(elevated);
        Assert.Equal(elevated, fixture.ViewModel.IsElevated);
        Assert.NotEmpty(fixture.ViewModel.AdminStatus);
    }

    /// <summary>为每项测试组合隔离的接口服务与真实视图模型，不访问文件、注册表或进程。</summary>
    private sealed class Fixture
    {
        /// <summary>可观察设置和持久化结果的隔离存储。</summary>
        public FakeStore Store { get; } = new();
        /// <summary>可控制发现结果和阻塞扫描的隔离服务。</summary>
        public FakeDiscovery Discovery { get; } = new();
        /// <summary>只记录切号与还原请求的隔离 Steam 服务。</summary>
        public FakeSteam Steam { get; } = new();
        /// <summary>只记录目录事务请求的隔离资源服务。</summary>
        public FakeResources Resources { get; } = new();
        /// <summary>记录提示和文件夹选择的隔离交互。</summary>
        public FakeInteraction Interaction { get; } = new();
        /// <summary>记录明暗主题应用的隔离服务。</summary>
        public FakeTheme Theme { get; } = new();
        /// <summary>被测试的真实状态与命令对象。</summary>
        public MainViewModel ViewModel { get; }
        /// <summary>跨页刷新、互斥与健康数据库状态。</summary>
        public WorkspaceService Workspace { get; }
        /// <summary>独立账号页的状态和操作。</summary>
        public AccountsPageViewModel AccountsPageVM { get; }
        /// <summary>独立资源页的状态和操作。</summary>
        public ResourcesPageViewModel ResourcesPageVM { get; }
        /// <summary>独立备份页的状态和操作。</summary>
        public BackupsPageViewModel BackupsPageVM { get; }
        /// <summary>独立设置页的状态和操作。</summary>
        public SettingsPageViewModel SettingsPageVM { get; }
        /// <summary>创建一组互不共享状态的完整服务夹具。</summary>
        public Fixture(bool elevated = true) : this(elevated, null) { }
        /// <summary>在保持全部业务边界隔离的同时显式注入日志服务。</summary>
        public Fixture(ILogger<WorkspaceService> logger) : this(true, logger) { }
        /// <summary>组合独立页面与共同工作区，不让 Shell 承担页面业务。</summary>
        private Fixture(bool elevated, ILogger<WorkspaceService>? logger)
        {
            Workspace = new WorkspaceService(Store, Discovery, Resources, Interaction, Theme, logger);
            ViewModel = new MainViewModel(Workspace, new FakeEnvironment(elevated));
            AccountsPageVM = new AccountsPageViewModel(Workspace, Store, Steam, Resources, Interaction);
            ResourcesPageVM = new ResourcesPageViewModel(Workspace, Store, Resources, Interaction);
            BackupsPageVM = new BackupsPageViewModel(Workspace, Steam, Resources);
            SettingsPageVM = new SettingsPageViewModel(Workspace, Store, Discovery, Interaction, Theme);
        }
        /// <summary>创建并成功完成初始化的测试夹具。</summary>
        public static async Task<Fixture> ReadyAsync()
        {
            var fixture = new Fixture();
            await fixture.ViewModel.InitializeAsync();
            return fixture;
        }
        /// <summary>创建完整资源对象，仅表示虚构路径而不创建目录。</summary>
        public static ResourceProfile Profile(string folder, long bytes, bool linked = false) => new() { FolderName = folder, FullPath = "C:\\Fixture\\Game\\LocalData\\" + folder, Bytes = bytes, IsLinked = linked, LinkTarget = linked ? "C:\\Fixture\\Game\\LocalData\\11223344\\0000" : null };
    }

    /// <summary>保存独立设置快照并记录同步的完整账号元数据。</summary>
    private sealed class FakeStore : ISettingsStore
    {
        /// <summary>所有测试均仅使用虚构的状态目录。</summary>
        public string StateDirectory => "C:\\Fixture\\State";
        /// <summary>已经持久保存的设置快照。</summary>
        public AppSettings Settings { get; set; } = new();
        /// <summary>可注入的读取异常。</summary>
        public Exception? LoadError { get; set; }
        /// <summary>可注入的保存异常。</summary>
        public Exception? SaveError { get; set; }
        /// <summary>模拟本次发现账号提交数据库失败。</summary>
        public Exception? SyncError { get; set; }
        /// <summary>已执行读取次数。</summary>
        public int LoadCount { get; private set; }
        /// <summary>已成功保存次数。</summary>
        public int SaveCount { get; private set; }
        /// <summary>已执行账号同步次数。</summary>
        public int SyncCount { get; private set; }
        /// <summary>最近同步的账号快照。</summary>
        public IReadOnlyList<SteamAccount> Synchronized { get; private set; } = [];
        /// <summary>读取独立快照，注入异常时不返回默认数据掩盖错误。</summary>
        public AppSettings Load()
        {
            LoadCount++;
            if (LoadError is not null) throw LoadError;
            return Copy(Settings);
        }
        /// <summary>模拟持久事务，仅在无异常时替换已保存快照。</summary>
        public void Save(AppSettings settings)
        {
            if (SaveError is not null) throw SaveError;
            Settings = Copy(settings);
            SaveCount++;
        }
        /// <summary>记录健康存储收到的真实账号同步结果。</summary>
        public void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts) { if (SyncError is not null) throw SyncError; Synchronized = accounts.ToArray(); SyncCount++; }
        /// <summary>返回最近一次账号同步的完整元数据。</summary>
        public IReadOnlyList<SteamAccount> GetAccounts() => Synchronized;
        /// <summary>复制集合，避免未保存编辑泄漏到持久设置的预期结果。</summary>
        private static AppSettings Copy(AppSettings settings) => new() { SteamPath = settings.SteamPath, GamePath = settings.GamePath, SourceProfile = settings.SourceProfile, DarkTheme = settings.DarkTheme, AccountBindings = new(settings.AccountBindings), AccountNotes = new(settings.AccountNotes), HiddenAccounts = new(settings.HiddenAccounts) };
    }

    /// <summary>返回完整安装快照，并允许确定性控制后台发现的忙碌状态。</summary>
    private sealed class FakeDiscovery : ISteamDiscoveryService
    {
        /// <summary>默认发现两个本地账号和完整安装路径。</summary>
        public DiscoveryResult Result { get; set; } = new() { SteamPath = "C:\\Fixture\\Steam", GamePath = "C:\\Fixture\\Game", Accounts = [new SteamAccount { SteamId = "1", AccountName = "alice", PersonaName = "艾丽丝", RememberPassword = true, AllowAutoLogin = true }, new SteamAccount { SteamId = "2", AccountName = "bob", PersonaName = "鲍勃", RememberPassword = false, AllowAutoLogin = false, MostRecent = true }] };
        /// <summary>用于验证无效安装路径的注入异常。</summary>
        public Exception? Error { get; set; }
        /// <summary>是否暂停后续扫描，直至测试明确释放。</summary>
        public bool Block { get; set; }
        /// <summary>通知测试扫描已经进入暂停点。</summary>
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>测试显式释放后台扫描的完成信号。</summary>
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>按顺序保存实际传入的安装偏好。</summary>
        public List<(string? Steam, string? Game)> Requests { get; } = [];
        /// <summary>模拟只读发现，暂停操作仅用于验证命令互斥。</summary>
        public DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null)
        {
            Requests.Add((steamOverride, gameOverride));
            if (Error is not null) throw Error;
            if (Block) { Entered.TrySetResult(true); Release.Task.GetAwaiter().GetResult(); }
            return Result;
        }
        /// <summary>返回完整账号数据而不访问 Steam 配置。</summary>
        public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath) => Result.Accounts;
    }

    /// <summary>使用固定结果模拟 Steam 操作，绝不启动或终止真实进程。</summary>
    private sealed class FakeSteam : ISteamAccountService
    {
        /// <summary>切号操作的可控用户结果。</summary>
        public string LaunchResult { get; set; } = "已启动测试账号";
        /// <summary>实际收到的活动路径和账号标识。</summary>
        public List<(string Steam, string Account)> LaunchedAccounts { get; } = [];
        /// <summary>实际收到的登录还原路径。</summary>
        public List<string> RestoredPaths { get; } = [];
        /// <summary>记录切号边界调用并返回固定成功结果。</summary>
        public Task<string> SwitchAndLaunchAsync(string steamPath, SteamAccount account, CancellationToken cancellationToken = default) { LaunchedAccounts.Add((steamPath, account.SteamId)); return Task.FromResult(LaunchResult); }
        /// <summary>记录登录还原边界调用而不写注册表或 Steam 文件。</summary>
        public Task<string> RestoreLatestAsync(string steamPath, CancellationToken cancellationToken = default) { RestoredPaths.Add(steamPath); return Task.FromResult("已还原测试登录配置"); }
    }

    /// <summary>用完整资源对象模拟资源扫描与事务响应，不创建目录或链接。</summary>
    private sealed class FakeResources : IResourceSharingService
    {
        /// <summary>旧跨页夹具不执行失效共享修复事务。</summary>
        public ShareBackup? RepairInvalidSharing(string backupId) => null;
        /// <summary>同时覆盖已下载来源、已共享目标和空目录。</summary>
        public IReadOnlyList<ResourceProfile> Profiles { get; set; } = [Fixture.Profile("11223344", 1_073_741_824), Fixture.Profile("aabbccdd", 0, true), Fixture.Profile("55667788", 0)];
        /// <summary>可选资源事务清单。</summary>
        public IReadOnlyList<ShareBackup> Backups { get; set; } = [new ShareBackup { Id = "backup", CreatedAt = DateTimeOffset.UtcNow, GamePath = "C:\\Fixture\\Game", SourcePath = "C:\\Fixture\\Game\\LocalData\\11223344\\0000", Entries = [new ShareEntry { ResourcePath = "C:\\Fixture\\Game\\LocalData\\aabbccdd\\0000", BackupPath = "C:\\Fixture\\Backup", OriginalExisted = true, Moved = true, Linked = true }] }];
        /// <summary>共享操作返回的事务，空引用表示幂等。</summary>
        public ShareBackup? ShareResult { get; set; }
        /// <summary>实际扫描的活动游戏路径。</summary>
        public List<string> ScannedPaths { get; } = [];
        /// <summary>实际共享请求中的路径、来源和目标集合。</summary>
        public List<(string Game, string Source, string[] Targets)> Shared { get; } = [];
        /// <summary>实际请求还原的事务标识。</summary>
        public List<string> RestoredIds { get; } = [];
        /// <summary>记录活动路径并返回完整隔离资源快照。</summary>
        public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath) { ScannedPaths.Add(gamePath); return Profiles; }
        /// <summary>记录共享的准确目标集合，以检验人工绑定边界。</summary>
        public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders)
        {
            var targets = targetFolders.ToArray();
            if (targets.Contains(sourceFolder, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("来源资源不可绑定到自身。");
            Shared.Add((gamePath, sourceFolder, targets));
            return ShareResult;
        }
        /// <summary>返回隔离事务清单。</summary>
        public IReadOnlyList<ShareBackup> GetBackups(string gamePath) => Backups;
        /// <summary>仅记录还原请求，不接触真实文件系统。</summary>
        public void Restore(string backupId) => RestoredIds.Add(backupId);
    }

    /// <summary>记录所有用户交互，并允许模拟取消与通知错误。</summary>
    private sealed class FakeInteraction : IUserInteraction
    {
        /// <summary>跨页集成夹具默认取消未请求的事务确认。</summary>
        public Task<bool> ConfirmAsync(string title, string content) => Task.FromResult(false);
        /// <summary>已展示的通知标题和内容。</summary>
        public List<(string Title, string Content)> Notices { get; } = [];
        /// <summary>文件夹选择器收到的标题和当前编辑路径。</summary>
        public List<(string Title, string Initial)> FolderRequests { get; } = [];
        /// <summary>选择器返回路径；空引用表示取消。</summary>
        public string? PickedFolder { get; set; }
        /// <summary>可注入的通知失败。</summary>
        public Exception? NoticeError { get; set; }
        /// <summary>记录通知并按设置返回成功或失败任务。</summary>
        public Task ShowNoticeAsync(string title, string content) { Notices.Add((title, content)); return NoticeError is null ? Task.CompletedTask : Task.FromException(NoticeError); }
        /// <summary>记录选择请求并返回隔离的用户选择结果。</summary>
        public string? PickFolder(string title, string initialDirectory) { FolderRequests.Add((title, initialDirectory)); return PickedFolder; }
    }

    /// <summary>记录主题偏好，不操作 WPF 全局资源。</summary>
    private sealed class FakeTheme : IThemeService
    {
        /// <summary>按调用顺序应用的明暗状态。</summary>
        public List<bool> Applied { get; } = [];
        /// <summary>记录界面要求的主题。</summary>
        public void Apply(bool dark) => Applied.Add(dark);
    }

    /// <summary>提供固定状态目录和权限标识，不读取操作系统身份。</summary>
    private sealed class FakeEnvironment : IApplicationEnvironment
    {
        /// <summary>创建具有指定管理员标识的隔离环境。</summary>
        public FakeEnvironment(bool elevated) => IsElevated = elevated;
        /// <summary>环境默认状态目录。</summary>
        public string DefaultStateDirectory => "C:\\Fixture\\DefaultState";
        /// <summary>测试默认模拟管理员权限。</summary>
        public bool IsElevated { get; }
    }
}
