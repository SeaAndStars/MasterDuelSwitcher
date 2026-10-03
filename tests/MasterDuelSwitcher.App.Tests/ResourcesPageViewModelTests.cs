using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.App.Views.Pages;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Markup;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>验证资源页独立状态、显式来源校验和实际共享服务调用。</summary>
public sealed class ResourcesPageViewModelTests
{
    /// <summary>构造页面只建立本页状态，不提前读取数据库或扫描资源。</summary>
    [Fact]
    public void ConstructionIsDeferredAndExposesOwnEmptyState()
    {
        var fixture = new Fixture();
        Assert.Same(fixture.Workspace, fixture.ViewModel.Workspace);
        Assert.Empty(fixture.ViewModel.Profiles);
        Assert.Empty(fixture.ViewModel.SourceProfiles);
        Assert.Null(fixture.ViewModel.SelectedSource);
        Assert.Equal("尚未检测到资源目录", fixture.ViewModel.Summary);
        Assert.True(fixture.ViewModel.NoProfiles);
        Assert.True(fixture.ViewModel.NoRepairBackups);
        Assert.Null(fixture.ViewModel.SelectedRepairBackup);
        Assert.Equal(0, fixture.Store.LoadCalls);
        Assert.Equal(0, fixture.Resources.ScanCalls);
    }

    /// <summary>快照刷新筛选独立且有资源的来源，按保存的账号标识选中并计算独立容量。</summary>
    [Fact]
    public async Task RefreshOwnsProfilesSourcesSelectionAndGigabyteSummary()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = "1234abcd";
        fixture.Resources.Profiles = [Profile("1234ABCD", 2L * 1073741824), Profile("5678EF90", 4L * 1073741824, true), Profile("AAAABBBB", 0), Profile("CCCCDDDD", 268435456)];
        await fixture.Workspace.InitializeAsync();
        Assert.Equal(4, fixture.ViewModel.Profiles.Count);
        Assert.Equal(new[] { "1234ABCD", "CCCCDDDD" }, fixture.ViewModel.SourceProfiles.Select(profile => profile.FolderName));
        Assert.Same(fixture.ViewModel.Profiles[0], fixture.ViewModel.SelectedSource);
        Assert.Equal("4 个目录 · 1 个已共享 · 独立资源 2.25 GB", fixture.ViewModel.Summary);
        Assert.False(fixture.ViewModel.NoProfiles);
    }

    /// <summary>小于一吉字节的独立资源采用兆字节显示，已共享目录不贡献独立容量。</summary>
    [Fact]
    public async Task SmallResourceSummaryUsesMegabytes()
    {
        var fixture = new Fixture();
        fixture.Resources.Profiles = [Profile("1234ABCD", 5242880), Profile("5678EF90", 100, true)];
        await fixture.Workspace.InitializeAsync();
        Assert.Equal("2 个目录 · 1 个已共享 · 独立资源 5.0 MB", fixture.ViewModel.Summary);
    }

    /// <summary>刷新清空过时的快照、来源和选择并恢复真实空状态。</summary>
    [Fact]
    public async Task EmptyRefreshClearsPageStateAndNotifiesBindings()
    {
        var fixture = await Fixture.Ready();
        var notifications = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        fixture.Resources.Profiles = [];
        await fixture.Workspace.RefreshDataAsync();
        Assert.Empty(fixture.ViewModel.Profiles);
        Assert.Empty(fixture.ViewModel.SourceProfiles);
        Assert.Null(fixture.ViewModel.SelectedSource);
        Assert.True(fixture.ViewModel.NoProfiles);
        Assert.Equal("尚未检测到资源目录", fixture.ViewModel.Summary);
        Assert.Contains(nameof(ResourcesPageViewModel.NoProfiles), notifications);
        Assert.Contains(nameof(ResourcesPageViewModel.Summary), notifications);
    }

    /// <summary>保存的来源消失、共享或未下载时保持未选中，避免自动猜测其他目录。</summary>
    [Theory]
    [InlineData("DEADBEEF")]
    [InlineData("5678EF90")]
    [InlineData("AAAABBBB")]
    public async Task InvalidSavedSourceDoesNotSelectAnotherAccount(string saved)
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = saved;
        fixture.Resources.Profiles = [Profile("1234ABCD", 10), Profile("5678EF90", 10, true), Profile("AAAABBBB", 0)];
        await fixture.Workspace.InitializeAsync();
        Assert.Single(fixture.ViewModel.SourceProfiles);
        Assert.Null(fixture.ViewModel.SelectedSource);
    }

    /// <summary>同一选择不重复通知，刷新恢复已保存来源而不保留未保存选择。</summary>
    [Fact]
    public async Task SelectionNotifiesOnlyOnChangeAndRefreshUsesSavedPreference()
    {
        var fixture = await Fixture.Ready();
        var notifications = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        fixture.ViewModel.SelectedSource = fixture.ViewModel.SelectedSource;
        Assert.Empty(notifications);
        fixture.ViewModel.SelectedSource = fixture.ViewModel.Profiles[1];
        Assert.Equal(new[] { nameof(ResourcesPageViewModel.SelectedSource) }, notifications);
        await fixture.Workspace.RefreshDataAsync();
        Assert.Equal("1234ABCD", fixture.ViewModel.SelectedSource!.FolderName);
    }

    /// <summary>保存来源只提交所选目录并保留其他用户偏好。</summary>
    [Fact]
    public async Task SaveSourcePersistsSelectionAndKeepsOtherSettings()
    {
        var fixture = await Fixture.Ready();
        fixture.Workspace.Settings.AccountNotes["account"] = "备注";
        fixture.ViewModel.SelectedSource = fixture.ViewModel.Profiles[1];
        await fixture.ViewModel.SaveSourceCommand.ExecuteAsync();
        var saved = Assert.Single(fixture.Store.Saved);
        Assert.Equal("5678EF90", saved.SourceProfile);
        Assert.Equal("备注", saved.AccountNotes["account"]);
        Assert.Equal("5678EF90", fixture.Workspace.Settings.SourceProfile);
        Assert.Contains("资源来源已保存：5678EF90", fixture.Workspace.Status);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.Equal(0, fixture.Resources.EnableCalls);
    }

    /// <summary>数据库未成功加载时，页面保存和共享均不写入状态。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnhealthyStorageBlocksMutations(bool enable)
    {
        var fixture = new Fixture();
        await (enable ? fixture.ViewModel.EnableSharingCommand : fixture.ViewModel.SaveSourceCommand).ExecuteAsync();
        Assert.Contains("accounts.db", Assert.Single(fixture.Interaction.Notices));
        Assert.Empty(fixture.Store.Saved);
        Assert.Equal(0, fixture.Resources.EnableCalls);
    }

    /// <summary>没有有效游戏路径时明确提示检测游戏，不调用共享服务。</summary>
    [Fact]
    public async Task MissingGamePathBlocksSourceSaving()
    {
        var fixture = new Fixture();
        fixture.Discovery.GamePath = "";
        await fixture.Workspace.InitializeAsync();
        fixture.ViewModel.SelectedSource = Profile("1234ABCD", 10);
        await fixture.ViewModel.SaveSourceCommand.ExecuteAsync();
        Assert.Contains("Master Duel 安装目录", Assert.Single(fixture.Interaction.Notices));
        Assert.Empty(fixture.Store.Saved);
    }

    /// <summary>未选择来源时保存和共享均展示明确提示，不猜测第一个来源。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSelectionBlocksMutations(bool enable)
    {
        var fixture = await Fixture.Ready();
        fixture.ViewModel.SelectedSource = null;
        await (enable ? fixture.ViewModel.EnableSharingCommand : fixture.ViewModel.SaveSourceCommand).ExecuteAsync();
        Assert.Contains("请先选择", Assert.Single(fixture.Interaction.Notices));
        Assert.Empty(fixture.Store.Saved);
        Assert.Equal(0, fixture.Resources.EnableCalls);
    }

    /// <summary>旧选择已经消失、变为链接或清空资源时，重新按最新快照校验来源。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StaleSourceIsValidatedAgainstLatestSnapshot(int change)
    {
        var fixture = await Fixture.Ready();
        var stale = fixture.ViewModel.SelectedSource;
        fixture.Resources.Profiles = change switch
        {
            0 => [Profile("5678EF90", 10)],
            1 => [Profile("1234ABCD", 10, true), Profile("5678EF90", 10)],
            _ => [Profile("1234ABCD", 0), Profile("5678EF90", 10)]
        };
        await fixture.Workspace.RefreshDataAsync();
        fixture.ViewModel.SelectedSource = stale;
        await fixture.ViewModel.EnableSharingCommand.ExecuteAsync();
        Assert.Contains("资源来源已改变", Assert.Single(fixture.Interaction.Notices));
        Assert.Empty(fixture.Store.Saved);
        Assert.Equal(0, fixture.Resources.EnableCalls);
    }

    /// <summary>只有来源目录时不保存共享设置，也不向后端传入空目标集合。</summary>
    [Fact]
    public async Task SourceWithoutOtherAccountDoesNotEnableSharing()
    {
        var fixture = new Fixture();
        fixture.Resources.Profiles = [Profile("1234ABCD", 10)];
        fixture.Store.Settings.SourceProfile = "1234ABCD";
        await fixture.Workspace.InitializeAsync();
        await fixture.ViewModel.EnableSharingCommand.ExecuteAsync();
        Assert.Contains("当前只有来源目录", Assert.Single(fixture.Interaction.Notices));
        Assert.Empty(fixture.Store.Saved);
        Assert.Equal(0, fixture.Resources.EnableCalls);
    }

    /// <summary>共享使用实际检测路径及本页显式选择，完成后刷新快照并显示还原记录数量。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnableSharingPersistsSourceCallsBackendAndRefreshes(bool noOp)
    {
        var fixture = await Fixture.Ready();
        fixture.Resources.Result = noOp ? null : new ShareBackup { Entries = [new ShareEntry()] };
        fixture.Resources.AfterEnable = () => fixture.Resources.Profiles = [Profile("1234ABCD", 10), Profile("5678EF90", 0, true)];
        await fixture.ViewModel.EnableSharingCommand.ExecuteAsync();
        Assert.Equal(1, fixture.Resources.EnableCalls);
        Assert.Equal(fixture.Discovery.GamePath, fixture.Resources.CalledGame);
        Assert.Equal("1234ABCD", fixture.Resources.CalledSource);
        Assert.Equal(new[] { "5678EF90" }, fixture.Resources.CalledTargets);
        Assert.Equal("1234ABCD", Assert.Single(fixture.Store.Saved).SourceProfile);
        Assert.True(fixture.ViewModel.Profiles[1].IsLinked);
        Assert.Single(fixture.ViewModel.SourceProfiles);
        Assert.Contains(noOp ? "已是最新状态" : "已保留 1 个目录", fixture.Workspace.Status);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.False(fixture.Workspace.IsBusy);
    }

    /// <summary>后端共享错误由公共工作区显示，页面不伪报完成并解除全部页面忙碌状态。</summary>
    [Fact]
    public async Task BackendFailureIsReportedAndSharedBusyStateIsReleased()
    {
        var fixture = await Fixture.Ready();
        fixture.Resources.EnableError = new IOException("事务冲突现场已保留");
        await fixture.ViewModel.EnableSharingCommand.ExecuteAsync();
        Assert.Contains("事务冲突现场已保留", Assert.Single(fixture.Interaction.Notices));
        Assert.Equal(1, fixture.Resources.ScanCalls);
        Assert.False(fixture.Workspace.IsBusy);
        Assert.True(fixture.ViewModel.SaveSourceCommand.CanExecute(null));
    }

    /// <summary>任意页面已进入操作时，资源保存和共享命令都不执行。</summary>
    [Fact]
    public async Task SharedBusyStateDisablesBothPageCommands()
    {
        var fixture = await Fixture.Ready();
        await fixture.Workspace.RunOperationAsync("其他页面操作", async () =>
        {
            Assert.False(fixture.ViewModel.SaveSourceCommand.CanExecute(null));
            Assert.False(fixture.ViewModel.EnableSharingCommand.CanExecute(null));
            Assert.False(fixture.ViewModel.RepairSharingCommand.CanExecute(null));
            await fixture.ViewModel.SaveSourceCommand.ExecuteAsync();
            await fixture.ViewModel.EnableSharingCommand.ExecuteAsync();
            await fixture.ViewModel.RepairSharingCommand.ExecuteAsync();
        });
        Assert.Empty(fixture.Store.Saved);
        Assert.Equal(0, fixture.Resources.EnableCalls);
    }

    /// <summary>待检查记录只包含未还原事务，刷新保留相同标识的选择并清除已消失选择。</summary>
    [Fact]
    public async Task RepairSnapshotFiltersRestoredRecordsAndTracksSelectionById()
    {
        var fixture = await Fixture.Ready();
        var pending = Backup("old", false);
        fixture.Resources.Backups = [pending, Backup("restored", true)];
        await fixture.Workspace.RefreshDataAsync();
        Assert.Same(pending, Assert.Single(fixture.ViewModel.RepairBackups));
        Assert.False(fixture.ViewModel.NoRepairBackups);
        fixture.ViewModel.SelectedRepairBackup = pending;
        var refreshed = Backup("old", false);
        fixture.Resources.Backups = [refreshed];
        await fixture.Workspace.RefreshDataAsync();
        Assert.Same(refreshed, fixture.ViewModel.SelectedRepairBackup);
        fixture.Resources.Backups = [];
        await fixture.Workspace.RefreshDataAsync();
        Assert.Null(fixture.ViewModel.SelectedRepairBackup);
        Assert.True(fixture.ViewModel.NoRepairBackups);
    }

    /// <summary>没有成功读取数据库时，修复命令在确认窗口之前停止。</summary>
    [Fact]
    public async Task RepairRequiresHealthyStorageBeforeConfirmation()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.RepairSharingCommand.ExecuteAsync();
        Assert.Contains("accounts.db", Assert.Single(fixture.Interaction.Notices));
        Assert.Empty(fixture.Interaction.Confirmations);
        Assert.Equal(0, fixture.Resources.RepairCalls);
    }

    /// <summary>修复必须显式选择当前快照中的事务，不接受已过期的旧页面对象。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairRequiresCurrentExplicitSelection(bool stale)
    {
        var fixture = await Fixture.Ready();
        fixture.ViewModel.SelectedRepairBackup = stale ? Backup("missing", false) : null;
        await fixture.ViewModel.RepairSharingCommand.ExecuteAsync();
        Assert.Contains(stale ? "记录已改变" : "请选择", Assert.Single(fixture.Interaction.Notices));
        Assert.Empty(fixture.Interaction.Confirmations);
        Assert.Equal(0, fixture.Resources.RepairCalls);
    }

    /// <summary>取消失效修复保留原记录和当前目录，确认内容清楚说明丢失备份及归档事实。</summary>
    [Fact]
    public async Task DeclinedRepairDoesNotInvokeBackendOrRefresh()
    {
        var fixture = await RepairFixture();
        var scans = fixture.Resources.ScanCalls;
        await fixture.ViewModel.RepairSharingCommand.ExecuteAsync();
        var confirmation = Assert.Single(fixture.Interaction.Confirmations);
        Assert.Contains("旧备份", confirmation);
        Assert.Contains("当前目录", confirmation);
        Assert.Contains("归档", confirmation);
        Assert.Equal(0, fixture.Resources.RepairCalls);
        Assert.Equal(scans, fixture.Resources.ScanCalls);
        Assert.Empty(fixture.Store.Saved);
        Assert.Empty(fixture.Interaction.Notices);
    }

    /// <summary>确认修复后只提交明确选定的旧事务，刷新移除已归档记录并显示新备份结果。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedRepairInvokesBackendAndRefreshes(bool noOp)
    {
        var fixture = await RepairFixture();
        fixture.Interaction.Confirmed = true;
        fixture.Resources.RepairResult = noOp ? null : new ShareBackup { Entries = [new ShareEntry()] };
        fixture.Resources.AfterRepair = () => fixture.Resources.Backups = [];
        await fixture.ViewModel.RepairSharingCommand.ExecuteAsync();
        Assert.Equal("old", fixture.Resources.RepairedId);
        Assert.Equal(1, fixture.Resources.RepairCalls);
        Assert.Empty(fixture.ViewModel.RepairBackups);
        Assert.Null(fixture.ViewModel.SelectedRepairBackup);
        Assert.Contains(noOp ? "共享已是最新状态" : "当前目录已保留为新备份", fixture.Workspace.Status);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.Empty(fixture.Store.Saved);
    }

    /// <summary>修复后端或确认窗口发生错误时释放忙碌状态，保留页面历史快照供检查。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairOrConfirmationErrorKeepsSnapshotAndReleasesBusy(bool confirmationError)
    {
        var fixture = await RepairFixture();
        fixture.Interaction.Confirmed = true;
        if (confirmationError) fixture.Interaction.ConfirmError = new IOException("确认窗口错误");
        else fixture.Resources.RepairError = new IOException("失效事务现场已保留");
        await fixture.ViewModel.RepairSharingCommand.ExecuteAsync();
        Assert.Contains(confirmationError ? "确认窗口错误" : "失效事务现场已保留", Assert.Single(fixture.Interaction.Notices));
        Assert.Single(fixture.ViewModel.RepairBackups);
        Assert.Equal(confirmationError ? 0 : 1, fixture.Resources.RepairCalls);
        Assert.False(fixture.Workspace.IsBusy);
    }

    /// <summary>在统一 WPF 应用的 Dispatcher 生命周期内验证资源页真实控件和双向绑定。</summary>
    internal static async Task VerifyRealPageBindingsAsync()
    {
        Application.Current.Dispatcher.VerifyAccess();
        var fixture = await RepairFixture();
        var page = new ResourcesPage(fixture.ViewModel);
        page.Resources.MergedDictionaries.Add(new ThemesDictionary());
        page.Resources.MergedDictionaries.Add(new ControlsDictionary());
        page.Measure(new Size(1000, 1000));
        page.Arrange(new Rect(0, 0, 1000, 1000));
        page.UpdateLayout();
        Assert.Same(fixture.ViewModel, page.ViewModel);
        Assert.Same(fixture.ViewModel, page.DataContext);
        Assert.Same(fixture.ViewModel, Assert.IsAssignableFrom<INavigableView<ResourcesPageViewModel>>(page).ViewModel);
        Assert.Equal("Segoe UI Variable, Microsoft YaHei UI, Segoe UI", page.FontFamily.Source);
        Assert.Equal(14, page.FontSize);
        var sources = Assert.IsType<ComboBox>(page.FindName("SourceProfile"));
        Assert.Same(fixture.ViewModel.SourceProfiles, sources.ItemsSource);
        sources.SelectedItem = fixture.ViewModel.SourceProfiles[1];
        Assert.Same(sources.SelectedItem, fixture.ViewModel.SelectedSource);
        fixture.ViewModel.SelectedSource = fixture.ViewModel.SourceProfiles[0];
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Assert.Same(fixture.ViewModel.SelectedSource, sources.SelectedItem);
        var repair = Assert.IsType<ComboBox>(page.FindName("RepairBackup"));
        Assert.Same(fixture.ViewModel.RepairBackups, repair.ItemsSource);
        fixture.ViewModel.SelectedRepairBackup = null;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Assert.Null(repair.SelectedItem);
        repair.SelectedItem = fixture.ViewModel.RepairBackups[0];
        Assert.Same(repair.SelectedItem, fixture.ViewModel.SelectedRepairBackup);
        var profiles = Assert.IsType<Wpf.Ui.Controls.ListView>(page.FindName("ProfileList"));
        Assert.Same(fixture.ViewModel.Profiles, profiles.ItemsSource);
        Assert.Equal(2, profiles.Items.Count);
        Assert.False(Assert.IsType<Wpf.Ui.Controls.InfoBar>(page.FindName("ResourcesEmpty")).IsOpen);
        var save = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("SaveSourceButton"));
        var enable = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("EnableSharingButton"));
        var repairButton = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("RepairSharingButton"));
        Assert.Same(fixture.ViewModel.SaveSourceCommand, save.Command);
        Assert.Same(fixture.ViewModel.EnableSharingCommand, enable.Command);
        Assert.Same(fixture.ViewModel.RepairSharingCommand, repairButton.Command);
        Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(save.Icon);
        Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(enable.Icon);
        Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(repairButton.Icon);
        Assert.True(page.ActualWidth > 0);
    }

    /// <summary>构造只包含一个明确失效历史记录的资源页。</summary>
    private static async Task<Fixture> RepairFixture()
    {
        var fixture = await Fixture.Ready();
        fixture.Resources.Backups = [Backup("old", false)];
        await fixture.Workspace.RefreshDataAsync();
        fixture.ViewModel.SelectedRepairBackup = Assert.Single(fixture.ViewModel.RepairBackups);
        return fixture;
    }

    /// <summary>生成可辨认标识的内存历史记录。</summary>
    private static ShareBackup Backup(string id, bool restored) => new() { Id = id, Restored = restored, Entries = [new ShareEntry { OriginalExisted = true }] };

    /// <summary>生成明确账号标识的资源扫描测试快照。</summary>
    private static ResourceProfile Profile(string folder, long bytes, bool linked = false) => new() { FolderName = folder, FullPath = Path.Combine("D:\\fixture-game", "LocalData", folder), Bytes = bytes, IsLinked = linked };

    /// <summary>组合独立资源页和真实共享工作区，不触及用户安装或数据库。</summary>
    private sealed class Fixture
    {
        /// <summary>内存设置边界。</summary>
        internal FakeStore Store { get; } = new();
        /// <summary>实际安装路径的可控发现边界。</summary>
        internal FakeDiscovery Discovery { get; } = new();
        /// <summary>扫描和共享调用记录边界。</summary>
        internal FakeResources Resources { get; } = new();
        /// <summary>可观察用户通知。</summary>
        internal FakeInteraction Interaction { get; } = new();
        /// <summary>真实全局互斥和数据快照管理。</summary>
        internal WorkspaceService Workspace { get; }
        /// <summary>独立被测资源页。</summary>
        internal ResourcesPageViewModel ViewModel { get; }
        /// <summary>连接资源页面的真实命令与工作区。</summary>
        internal Fixture()
        {
            Workspace = new WorkspaceService(Store, Discovery, Resources, Interaction, new FakeTheme());
            ViewModel = new ResourcesPageViewModel(Workspace, Store, Resources, Interaction);
        }
        /// <summary>返回已成功加载、拥有两个独立账号目录的资源页面。</summary>
        internal static async Task<Fixture> Ready()
        {
            var fixture = new Fixture();
            fixture.Store.Settings.SourceProfile = "1234ABCD";
            fixture.Resources.Profiles = [Profile("1234ABCD", 10), Profile("5678EF90", 20)];
            await fixture.Workspace.InitializeAsync();
            return fixture;
        }
    }

    /// <summary>仅保存内存设置副本的数据库替身。</summary>
    private sealed class FakeStore : ISettingsStore
    {
        /// <summary>固定测试状态根。</summary>
        public string StateDirectory => "D:\\fixture-state";
        /// <summary>模拟持久用户偏好。</summary>
        internal AppSettings Settings { get; set; } = new();
        /// <summary>读取调用次数。</summary>
        internal int LoadCalls { get; private set; }
        /// <summary>每次保存的独立设置快照。</summary>
        internal List<AppSettings> Saved { get; } = [];
        /// <summary>读取独立内存副本。</summary>
        public AppSettings Load() { LoadCalls++; return Clone(Settings); }
        /// <summary>记录完整保存内容。</summary>
        public void Save(AppSettings settings) { Settings = Clone(settings); Saved.Add(Clone(settings)); }
        /// <summary>本页夹具无需持久化发现的账号。</summary>
        public void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts) { }
        /// <summary>资源页不读取本地账号表。</summary>
        public IReadOnlyList<SteamAccount> GetAccounts() => [];
        /// <summary>复制可变设置以检验真实保存时机。</summary>
        private static AppSettings Clone(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
    }

    /// <summary>提供可控真实检测结果的发现替身。</summary>
    private sealed class FakeDiscovery : ISteamDiscoveryService
    {
        /// <summary>可控的已检测游戏路径。</summary>
        internal string GamePath { get; set; } = "D:\\fixture-game";
        /// <summary>返回实际检测路径，不把用户编辑偏好当作检测结果。</summary>
        public DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null) => new() { SteamPath = "D:\\fixture-steam", GamePath = GamePath };
        /// <summary>资源页面无需账号元数据。</summary>
        public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath) => [];
    }

    /// <summary>记录共享调用而不访问任何真实目录的资源替身。</summary>
    private sealed class FakeResources : IResourceSharingService
    {
        /// <summary>下一次扫描返回的快照。</summary>
        internal IReadOnlyList<ResourceProfile> Profiles { get; set; } = [];
        /// <summary>扫描次数。</summary>
        internal int ScanCalls { get; private set; }
        /// <summary>共享调用次数。</summary>
        internal int EnableCalls { get; private set; }
        /// <summary>接收到的检测路径。</summary>
        internal string? CalledGame { get; private set; }
        /// <summary>接收到的来源账号。</summary>
        internal string? CalledSource { get; private set; }
        /// <summary>接收到的完整目标账号列表。</summary>
        internal string[] CalledTargets { get; private set; } = [];
        /// <summary>共享成功后模拟刷新结果的钩子。</summary>
        internal Action? AfterEnable { get; set; }
        /// <summary>模拟共享服务错误。</summary>
        internal Exception? EnableError { get; set; }
        /// <summary>共享返回的事务或幂等空值。</summary>
        internal ShareBackup? Result { get; set; }
        /// <summary>资源历史快照。</summary>
        internal IReadOnlyList<ShareBackup> Backups { get; set; } = [];
        /// <summary>失效修复调用次数。</summary>
        internal int RepairCalls { get; private set; }
        /// <summary>收到的失效事务标识。</summary>
        internal string? RepairedId { get; private set; }
        /// <summary>修复接口的新事务返回值。</summary>
        internal ShareBackup? RepairResult { get; set; }
        /// <summary>修复后模拟归档更新的钩子。</summary>
        internal Action? AfterRepair { get; set; }
        /// <summary>修复过程中模拟的错误。</summary>
        internal Exception? RepairError { get; set; }
        /// <summary>返回只读快照并记录扫描。</summary>
        public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath) { ScanCalls++; return Profiles; }
        /// <summary>记录共享参数并执行可控完成或失败行为。</summary>
        public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders)
        {
            EnableCalls++;
            CalledGame = gamePath;
            CalledSource = sourceFolder;
            CalledTargets = targetFolders.ToArray();
            if (EnableError is not null) throw EnableError;
            AfterEnable?.Invoke();
            return Result;
        }
        /// <summary>该夹具没有资源还原事务。</summary>
        public IReadOnlyList<ShareBackup> GetBackups(string gamePath) => Backups;
        /// <summary>本页命令不应调用还原接口。</summary>
        public void Restore(string backupId) => throw new InvalidOperationException("资源页测试不应执行还原。");
        /// <summary>记录失效共享修复调用的占位测试边界。</summary>
        public ShareBackup? RepairInvalidSharing(string backupId)
        {
            RepairCalls++;
            RepairedId = backupId;
            if (RepairError is not null) throw RepairError;
            AfterRepair?.Invoke();
            return RepairResult;
        }
    }

    /// <summary>收集用户可见错误且不显示真实窗口。</summary>
    private sealed class FakeInteraction : IUserInteraction
    {
        /// <summary>通知内容列表。</summary>
        internal List<string> Notices { get; } = [];
        /// <summary>确认窗口中的用户可见说明。</summary>
        internal List<string> Confirmations { get; } = [];
        /// <summary>用户是否明确继续。</summary>
        internal bool Confirmed { get; set; }
        /// <summary>确认窗口的故障注入。</summary>
        internal Exception? ConfirmError { get; set; }
        /// <summary>记录实际通知正文。</summary>
        public Task ShowNoticeAsync(string title, string content) { Notices.Add(content); return Task.CompletedTask; }
        /// <summary>资源保存和普通共享测试不接受额外修复确认。</summary>
        public Task<bool> ConfirmAsync(string title, string content)
        {
            Confirmations.Add(content);
            if (ConfirmError is not null) throw ConfirmError;
            return Task.FromResult(Confirmed);
        }
        /// <summary>资源页没有路径选择行为。</summary>
        public string? PickFolder(string title, string initialDirectory) => null;
    }

    /// <summary>隔离真实 WPF 外观应用的主题替身。</summary>
    private sealed class FakeTheme : IThemeService
    {
        /// <summary>加载设置时不修改桌面主题。</summary>
        public void Apply(bool dark) { }
    }
}
