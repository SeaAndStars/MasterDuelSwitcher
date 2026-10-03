using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.App.Views.Pages;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Markup;
using Xunit;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>使用独立完整服务快照验证备份页行为，并在 STA 中实例化官方 Fluent 页面。</summary>
public sealed class BackupsPageViewModelTests
{
    /// <summary>构造时不读取存储，工作区首次刷新后页面获得真实快照。</summary>
    [Fact]
    public async Task ConstructorDefersStorageAndDataChangedPopulatesBackups()
    {
        var fixture = new Fixture();
        Assert.Same(fixture.Workspace, fixture.ViewModel.Workspace);
        Assert.Equal(0, fixture.Store.LoadCount);
        Assert.True(fixture.ViewModel.NoBackups);
        Assert.Null(fixture.ViewModel.SelectedBackup);
        Assert.True(fixture.ViewModel.RestoreResourcesCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.RestoreSteamCommand.CanExecute(null));
        await fixture.Workspace.InitializeAsync();
        Assert.Same(Assert.Single(fixture.Resources.Backups), Assert.Single(fixture.ViewModel.Backups));
        Assert.False(fixture.ViewModel.NoBackups);
        Assert.Equal(1, fixture.Store.LoadCount);
    }

    /// <summary>页面在工作区已有快照之后创建时，应立即复制备份而不是等待下一次刷新。</summary>
    [Fact]
    public async Task ConstructorCopiesAlreadyLoadedWorkspaceSnapshot()
    {
        var fixture = await Fixture.ReadyAsync();
        var viewModel = new BackupsPageViewModel(fixture.Workspace, fixture.Steam, fixture.Resources);
        Assert.Same(fixture.Resources.Backups[0], Assert.Single(viewModel.Backups));
        Assert.False(viewModel.NoBackups);
    }

    /// <summary>刷新替换页面集合并清空选中记录，属性同值赋值不产生重复通知。</summary>
    [Fact]
    public async Task RefreshClearsStaleSelectionAndNotifiesEmptyState()
    {
        var fixture = await Fixture.ReadyAsync();
        var names = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => names.Add(args.PropertyName);
        fixture.ViewModel.SelectedBackup = null;
        Assert.Empty(names);
        fixture.ViewModel.SelectedBackup = fixture.ViewModel.Backups[0];
        fixture.ViewModel.SelectedBackup = fixture.ViewModel.Backups[0];
        Assert.Equal(new[] { nameof(BackupsPageViewModel.SelectedBackup) }, names);
        fixture.Resources.Backups = [];
        await fixture.Workspace.RefreshDataAsync();
        Assert.Null(fixture.ViewModel.SelectedBackup);
        Assert.Empty(fixture.ViewModel.Backups);
        Assert.True(fixture.ViewModel.NoBackups);
        Assert.Contains(nameof(BackupsPageViewModel.NoBackups), names);
        Assert.Equal(2, names.Count(name => name == nameof(BackupsPageViewModel.SelectedBackup)));
    }

    /// <summary>未选择资源备份时显示明确提示，不调用核心还原或刷新。</summary>
    [Fact]
    public async Task MissingBackupSelectionShowsNoticeWithoutRestoring()
    {
        var fixture = await Fixture.ReadyAsync();
        var scans = fixture.Resources.ScanCount;
        await fixture.ViewModel.RestoreResourcesCommand.ExecuteAsync();
        Assert.Contains("请先选择一条资源共享备份", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Empty(fixture.Resources.RestoredIds);
        Assert.Equal(scans, fixture.Resources.ScanCount);
        Assert.False(fixture.Workspace.IsBusy);
    }

    /// <summary>已经还原的事务显示幂等结果，不重复执行核心操作或刷新。</summary>
    [Fact]
    public async Task AlreadyRestoredBackupShowsIdempotentResult()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Resources.Backups[0].Restored = true;
        fixture.ViewModel.SelectedBackup = fixture.ViewModel.Backups[0];
        var scans = fixture.Resources.ScanCount;
        await fixture.ViewModel.RestoreResourcesCommand.ExecuteAsync();
        Assert.Contains("已经完成还原", fixture.Workspace.Status);
        Assert.Empty(fixture.Resources.RestoredIds);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.Equal(scans, fixture.Resources.ScanCount);
    }

    /// <summary>资源还原成功后刷新工作区，清空选择并保留已还原事务记录。</summary>
    [Fact]
    public async Task ResourceRestoreRefreshesBackupStateAndClearsSelection()
    {
        var fixture = await Fixture.ReadyAsync();
        var selected = fixture.ViewModel.Backups[0];
        fixture.ViewModel.SelectedBackup = selected;
        await fixture.ViewModel.RestoreResourcesCommand.ExecuteAsync();
        Assert.Equal(selected.Id, Assert.Single(fixture.Resources.RestoredIds));
        Assert.True(Assert.Single(fixture.ViewModel.Backups).Restored);
        Assert.Null(fixture.ViewModel.SelectedBackup);
        Assert.Equal(2, fixture.Resources.ScanCount);
        Assert.Contains("选中的资源备份已还原", fixture.Workspace.Status);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.False(fixture.Workspace.IsBusy);
    }

    /// <summary>核心资源错误保留选中记录及未还原状态，并恢复所有命令的可执行状态。</summary>
    [Fact]
    public async Task ResourceRestoreFailurePreservesSelectionAndReleasesBusyState()
    {
        var fixture = await Fixture.ReadyAsync();
        var selected = fixture.ViewModel.Backups[0];
        fixture.ViewModel.SelectedBackup = selected;
        fixture.Resources.RestoreError = new IOException("fixture-resource-restore-failed");
        await fixture.ViewModel.RestoreResourcesCommand.ExecuteAsync();
        Assert.Same(selected, fixture.ViewModel.SelectedBackup);
        Assert.False(selected.Restored);
        Assert.Equal(1, fixture.Resources.ScanCount);
        Assert.Contains("fixture-resource-restore-failed", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.False(fixture.Workspace.IsBusy);
        Assert.True(fixture.ViewModel.RestoreResourcesCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.RestoreSteamCommand.CanExecute(null));
    }

    /// <summary>资源还原执行期间工作区统一禁用其他页面命令，完成后重新启用。</summary>
    [Fact]
    public async Task ResourceRestoreUsesSharedBusyGateForSteamAndRefresh()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.ViewModel.SelectedBackup = fixture.ViewModel.Backups[0];
        fixture.Resources.BlockRestore = true;
        var running = fixture.ViewModel.RestoreResourcesCommand.ExecuteAsync();
        await fixture.Resources.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(fixture.Workspace.IsBusy);
            Assert.False(fixture.ViewModel.RestoreSteamCommand.CanExecute(null));
            Assert.False(fixture.Workspace.RefreshCommand.CanExecute(null));
            await fixture.ViewModel.RestoreSteamCommand.ExecuteAsync();
            Assert.Empty(fixture.Steam.RestoredPaths);
        }
        finally { fixture.Resources.RestoreRelease.TrySetResult(true); await running; }
        Assert.False(fixture.Workspace.IsBusy);
        Assert.True(fixture.ViewModel.RestoreSteamCommand.CanExecute(null));
    }

    /// <summary>未发现 Steam 路径时应显示设置提示，不调用核心 Steam 还原。</summary>
    [Fact]
    public async Task SteamRestoreRequiresDetectedSteamPath()
    {
        var fixture = new Fixture();
        fixture.Discovery.Result = new DiscoveryResult { GamePath = "C:\\Fixture\\Game" };
        await fixture.Workspace.InitializeAsync();
        await fixture.ViewModel.RestoreSteamCommand.ExecuteAsync();
        Assert.Empty(fixture.Steam.RestoredPaths);
        Assert.Contains("选择 Steam 安装目录", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Equal(1, fixture.Resources.ScanCount);
        Assert.False(fixture.Workspace.IsBusy);
    }

    /// <summary>Steam 还原使用已检测路径，完成后刷新备份页并显示核心返回的结果。</summary>
    [Fact]
    public async Task SteamRestoreUsesActivePathAndRefreshesWorkspace()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.ViewModel.SelectedBackup = fixture.ViewModel.Backups[0];
        await fixture.ViewModel.RestoreSteamCommand.ExecuteAsync();
        Assert.Equal("C:\\Fixture\\Steam", Assert.Single(fixture.Steam.RestoredPaths));
        Assert.Equal("Steam 登录配置已还原", fixture.Workspace.Status);
        Assert.Equal(2, fixture.Resources.ScanCount);
        Assert.Null(fixture.ViewModel.SelectedBackup);
        Assert.Empty(fixture.Interaction.Notices);
    }

    /// <summary>通知适配器失败应向上层传播，同时工作区释放忙碌状态。</summary>
    [Fact]
    public async Task NoticeFailurePropagatesAfterBusyStateIsReleased()
    {
        var fixture = await Fixture.ReadyAsync();
        var expected = new InvalidOperationException("fixture-notice-failed");
        fixture.Interaction.NoticeError = expected;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ViewModel.RestoreResourcesCommand.ExecuteAsync());
        Assert.Same(expected, failure);
        Assert.False(fixture.Workspace.IsBusy);
        Assert.True(fixture.ViewModel.RestoreResourcesCommand.CanExecute(null));
    }

    /// <summary>在统一 WPF 应用的 Dispatcher 生命周期内验证备份页真实控件和双向绑定。</summary>
    internal static async Task VerifyRealPageBindingsAsync()
    {
        Application.Current.Dispatcher.VerifyAccess();
        var fixture = await Fixture.ReadyAsync();
        var page = new BackupsPage(fixture.ViewModel);
        page.Resources.MergedDictionaries.Add(new ThemesDictionary());
        page.Resources.MergedDictionaries.Add(new ControlsDictionary());
        page.Measure(new Size(1000, 760));
        page.Arrange(new Rect(0, 0, 1000, 760));
        page.UpdateLayout();
        Assert.Same(fixture.ViewModel, page.ViewModel);
        Assert.Same(fixture.ViewModel, page.DataContext);
        Assert.Same(fixture.ViewModel, Assert.IsAssignableFrom<INavigableView<BackupsPageViewModel>>(page).ViewModel);
        Assert.Equal("Segoe UI Variable, Microsoft YaHei UI, Segoe UI", page.FontFamily.Source);
        Assert.Equal(14, page.FontSize);
        var list = Assert.IsType<Wpf.Ui.Controls.ListView>(page.FindName("BackupList"));
        Assert.Same(fixture.ViewModel.Backups, list.ItemsSource);
        Assert.Single(list.Items.Cast<ShareBackup>());
        list.SelectedItem = fixture.ViewModel.Backups[0];
        Assert.Same(list.SelectedItem, fixture.ViewModel.SelectedBackup);
        fixture.ViewModel.SelectedBackup = null;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Assert.Null(list.SelectedItem);
        Assert.False(Assert.IsType<Wpf.Ui.Controls.InfoBar>(page.FindName("BackupsEmpty")).IsOpen);
        var resourceButton = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("RestoreResourcesButton"));
        var steamButton = Assert.IsType<Wpf.Ui.Controls.Button>(page.FindName("RestoreSteamButton"));
        Assert.Same(fixture.ViewModel.RestoreResourcesCommand, resourceButton.Command);
        Assert.Same(fixture.ViewModel.RestoreSteamCommand, steamButton.Command);
        Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(resourceButton.Icon);
        Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(steamButton.Icon);
        Assert.True(page.ActualWidth > 0);
    }

    /// <summary>每个测试独立拥有完整工作区及其全部服务快照。</summary>
    private sealed class Fixture
    {
        /// <summary>隔离的偏好与账号存储。</summary>
        internal FakeStore Store { get; } = new();
        /// <summary>隔离的安装与账号发现服务。</summary>
        internal FakeDiscovery Discovery { get; } = new();
        /// <summary>隔离的 Steam 还原服务。</summary>
        internal FakeSteam Steam { get; } = new();
        /// <summary>隔离的资源快照及还原服务。</summary>
        internal FakeResources Resources { get; } = new();
        /// <summary>隔离的用户通知边界。</summary>
        internal FakeInteraction Interaction { get; } = new();
        /// <summary>所有操作实际共享的工作区。</summary>
        internal WorkspaceService Workspace { get; }
        /// <summary>被测备份页视图模型。</summary>
        internal BackupsPageViewModel ViewModel { get; }
        /// <summary>构建互不共享的服务对象，不访问真实账号或文件。</summary>
        internal Fixture()
        {
            Workspace = new WorkspaceService(Store, Discovery, Resources, Interaction, new FakeTheme());
            ViewModel = new BackupsPageViewModel(Workspace, Steam, Resources);
        }
        /// <summary>显式完成本地设置读取及完整发现快照加载。</summary>
        internal static async Task<Fixture> ReadyAsync()
        {
            var fixture = new Fixture();
            await fixture.Workspace.InitializeAsync();
            return fixture;
        }
    }

    /// <summary>返回独立完整设置快照，并记录工作区的账号同步。</summary>
    private sealed class FakeStore : ISettingsStore
    {
        /// <summary>固定隔离的状态路径，不创建真实数据库。</summary>
        public string StateDirectory => "C:\\Fixture\\State";
        /// <summary>已经保存的完整偏好快照。</summary>
        internal AppSettings Settings { get; private set; } = new();
        /// <summary>已读取设置的次数。</summary>
        internal int LoadCount { get; private set; }
        /// <summary>最近同步的完整账号元数据快照。</summary>
        private IReadOnlyList<SteamAccount> _accounts = [];
        /// <summary>读取独立设置副本。</summary>
        public AppSettings Load() { LoadCount++; return Copy(Settings); }
        /// <summary>保存独立设置副本。</summary>
        public void Save(AppSettings settings) => Settings = Copy(settings);
        /// <summary>记录完整账号同步快照。</summary>
        public void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts) => _accounts = accounts.ToArray();
        /// <summary>返回最近保存的完整账号快照。</summary>
        public IReadOnlyList<SteamAccount> GetAccounts() => _accounts;
        /// <summary>复制所有偏好及集合，防止未保存修改泄漏。</summary>
        private static AppSettings Copy(AppSettings settings) => new()
        {
            SteamPath = settings.SteamPath, GamePath = settings.GamePath, SourceProfile = settings.SourceProfile,
            DarkTheme = settings.DarkTheme, AccountBindings = new(settings.AccountBindings),
            AccountNotes = new(settings.AccountNotes), HiddenAccounts = new(settings.HiddenAccounts)
        };
    }

    /// <summary>提供完整的隔离安装与账号发现快照。</summary>
    private sealed class FakeDiscovery : ISteamDiscoveryService
    {
        /// <summary>测试默认的完整安装与账号记录。</summary>
        internal DiscoveryResult Result { get; set; } = new()
        {
            SteamPath = "C:\\Fixture\\Steam", GamePath = "C:\\Fixture\\Game",
            Accounts = [new SteamAccount { SteamId = "1", AccountName = "fixture", PersonaName = "测试账号", RememberPassword = true, AllowAutoLogin = true, MostRecent = true }]
        };
        /// <summary>返回完整发现结果而不访问注册表或 Steam。</summary>
        public DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null) => Result;
        /// <summary>返回发现快照中完整账号集合。</summary>
        public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath) => Result.Accounts;
    }

    /// <summary>仅记录 Steam 登录还原请求，不启动或结束真实进程。</summary>
    private sealed class FakeSteam : ISteamAccountService
    {
        /// <summary>核心还原收到的活动 Steam 路径。</summary>
        internal List<string> RestoredPaths { get; } = [];
        /// <summary>备份页不应发起切号，意外调用直接使测试失败。</summary>
        public Task<string> SwitchAndLaunchAsync(string steamPath, SteamAccount account, CancellationToken cancellationToken = default) => throw new InvalidOperationException("备份页不应发起切号。");
        /// <summary>记录请求并返回完整可显示的用户结果。</summary>
        public Task<string> RestoreLatestAsync(string steamPath, CancellationToken cancellationToken = default)
        {
            RestoredPaths.Add(steamPath);
            return Task.FromResult("Steam 登录配置已还原");
        }
    }

    /// <summary>维护完整资源与事务快照，并提供可控的核心还原状态。</summary>
    private sealed class FakeResources : IResourceSharingService
    {
        /// <summary>完整的隔离资源目录快照。</summary>
        internal IReadOnlyList<ResourceProfile> Profiles { get; } = [new ResourceProfile { FolderName = "1234ABCD", FullPath = "C:\\Fixture\\Game\\LocalData\\1234ABCD", Bytes = 24, IsLinked = false }];
        /// <summary>完整的隔离资源事务快照。</summary>
        internal IReadOnlyList<ShareBackup> Backups { get; set; } = [new ShareBackup
        {
            Id = "0123456789abcdef0123456789abcdef", CreatedAt = DateTimeOffset.UtcNow,
            GamePath = "C:\\Fixture\\Game", SourcePath = "C:\\Fixture\\Game\\LocalData\\1234ABCD\\0000",
            Entries = [new ShareEntry { ResourcePath = "C:\\Fixture\\Game\\LocalData\\5678EF90\\0000", BackupPath = "C:\\Fixture\\Game\\LocalData\\5678EF90\\0000.mdbackup-0123456789abcdef0123456789abcdef", OriginalExisted = true, Moved = true, Linked = true }]
        }];
        /// <summary>实际收到的还原事务标识。</summary>
        internal List<string> RestoredIds { get; } = [];
        /// <summary>实际资源刷新次数。</summary>
        internal int ScanCount { get; private set; }
        /// <summary>核心还原应返回的注入错误。</summary>
        internal Exception? RestoreError { get; set; }
        /// <summary>是否暂时阻塞核心还原以验证共享忙碌状态。</summary>
        internal bool BlockRestore { get; set; }
        /// <summary>通知测试核心还原已进入。</summary>
        internal TaskCompletionSource<bool> RestoreEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>由测试显式释放核心还原。</summary>
        internal TaskCompletionSource<bool> RestoreRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>记录刷新并返回完整目录快照。</summary>
        public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath) { ScanCount++; return Profiles; }
        /// <summary>备份页不应启用共享，意外调用直接使测试失败。</summary>
        public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders) => throw new InvalidOperationException("备份页不应启用共享。");
        /// <summary>备份页不执行失效共享修复，意外调用直接使测试失败。</summary>
        public ShareBackup? RepairInvalidSharing(string backupId) => throw new InvalidOperationException("备份页不应发起共享修复。");
        /// <summary>返回完整事务快照，不读取真实清单。</summary>
        public IReadOnlyList<ShareBackup> GetBackups(string gamePath) => Backups;
        /// <summary>改变指定事务的完整恢复状态，不操作真实目录。</summary>
        public void Restore(string backupId)
        {
            RestoredIds.Add(backupId);
            if (RestoreError is not null) throw RestoreError;
            if (BlockRestore) { RestoreEntered.TrySetResult(true); RestoreRelease.Task.GetAwaiter().GetResult(); }
            var backup = Backups.Single(value => value.Id == backupId);
            foreach (var entry in backup.Entries) { entry.Restored = true; entry.Moved = false; entry.Linked = false; }
            backup.Restored = true;
        }
    }

    /// <summary>记录完整用户通知，并允许模拟通知适配器本身失败。</summary>
    private sealed class FakeInteraction : IUserInteraction
    {
        /// <summary>收到的通知标题和完整内容。</summary>
        internal List<(string Title, string Content)> Notices { get; } = [];
        /// <summary>通知显示应传播的注入异常。</summary>
        internal Exception? NoticeError { get; set; }
        /// <summary>记录通知并返回明确的成功或失败任务。</summary>
        public Task ShowNoticeAsync(string title, string content)
        {
            Notices.Add((title, content));
            return NoticeError is null ? Task.CompletedTask : Task.FromException(NoticeError);
        }
        /// <summary>备份页没有目录选择动作，返回取消且不打开对话框。</summary>
        public string? PickFolder(string title, string initialDirectory) => null;
        /// <summary>备份页没有修复确认动作，返回明确的否定结果。</summary>
        public Task<bool> ConfirmAsync(string title, string content) => Task.FromResult(false);
    }

    /// <summary>记录完整主题偏好，不触碰其他测试的 WPF 全局资源。</summary>
    private sealed class FakeTheme : IThemeService
    {
        /// <summary>工作区加载时应用的明暗偏好。</summary>
        private readonly List<bool> _applied = [];
        /// <summary>只记录偏好，不应用实际系统主题。</summary>
        public void Apply(bool dark) => _applied.Add(dark);
    }
}
