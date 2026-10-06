using System.IO;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>验证独立设置页的路径编辑、校验、主题保存及共享工作区刷新。</summary>
public sealed class SettingsPageViewModelTests
{
    /// <summary>构造页面模型仅绑定共享工作区，不主动读取数据库或检测安装。</summary>
    [Fact]
    public void ConstructionDoesNotReadOrDiscover()
    {
        var fixture = new Fixture();
        Assert.Same(fixture.Workspace, fixture.ViewModel.Workspace);
        Assert.Equal(0, fixture.Store.LoadCount);
        Assert.Empty(fixture.Discovery.Requests);
        Assert.Empty(fixture.ViewModel.SteamPath);
        Assert.Empty(fixture.ViewModel.GamePath);
        Assert.False(fixture.ViewModel.DarkTheme);
    }

    /// <summary>工作区完成真实发现后应更新独立编辑字段及主题偏好。</summary>
    [Fact]
    public async Task WorkspaceRefreshUpdatesDetectedPathsAndTheme()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.DarkTheme = true;
        await fixture.Workspace.InitializeAsync();
        Assert.Equal("C:\\DetectedSteam", fixture.ViewModel.SteamPath);
        Assert.Equal("C:\\DetectedGame", fixture.ViewModel.GamePath);
        Assert.True(fixture.ViewModel.DarkTheme);
        fixture.ViewModel.SteamPath = "未保存编辑";
        fixture.ViewModel.GamePath = "未保存编辑";
        await fixture.Workspace.RefreshDataAsync();
        Assert.Equal("C:\\DetectedSteam", fixture.ViewModel.SteamPath);
        Assert.Equal("C:\\DetectedGame", fixture.ViewModel.GamePath);
    }

    /// <summary>发现结果缺失时保留已保存的手动路径，不将编辑字段错误清空。</summary>
    [Fact]
    public async Task MissingDiscoveryPreservesSavedOverrides()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SteamPath = "保存的Steam";
        fixture.Store.Settings.GamePath = "保存的游戏";
        fixture.Discovery.Result = new DiscoveryResult();
        await fixture.Workspace.InitializeAsync();
        Assert.Equal("保存的Steam", fixture.ViewModel.SteamPath);
        Assert.Equal("保存的游戏", fixture.ViewModel.GamePath);
    }

    /// <summary>选择目录应使用当前编辑值作为初始路径，并只更新对应字段。</summary>
    [Theory]
    [InlineData("Steam", "Steam编辑值", "游戏编辑值", "选中的Steam", "游戏编辑值")]
    [InlineData("Game", "Steam编辑值", "游戏编辑值", "Steam编辑值", "选中的Steam")]
    public async Task BrowseUpdatesOnlyRequestedPath(string target, string steam, string game, string expectedSteam, string expectedGame)
    {
        var fixture = new Fixture();
        fixture.ViewModel.SteamPath = steam;
        fixture.ViewModel.GamePath = game;
        fixture.Interaction.SelectedFolder = "选中的Steam";
        await fixture.ViewModel.BrowseFolderCommand.ExecuteAsync(target);
        Assert.Equal(expectedSteam, fixture.ViewModel.SteamPath);
        Assert.Equal(expectedGame, fixture.ViewModel.GamePath);
        Assert.Equal(target == "Steam" ? steam : game, Assert.Single(fixture.Interaction.FolderRequests).InitialDirectory);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    /// <summary>取消目录选择应保留编辑内容且不触发存储写入。</summary>
    [Fact]
    public async Task CancelledBrowsePreservesEditors()
    {
        var fixture = new Fixture();
        fixture.ViewModel.SteamPath = "原编辑值";
        await fixture.ViewModel.BrowseFolderCommand.ExecuteAsync("Steam");
        Assert.Equal("原编辑值", fixture.ViewModel.SteamPath);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    /// <summary>保存前修剪路径并完整校验，空白路径使用自动发现。</summary>
    [Theory]
    [InlineData("  D:\\Steam  ", "  E:\\Game  ", "D:\\Steam", "E:\\Game")]
    [InlineData("  ", "  ", "", "")]
    public async Task SaveValidatesTrimmedPathsBeforePersistence(string steam, string game, string expectedSteam, string expectedGame)
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.ViewModel.SteamPath = steam;
        fixture.ViewModel.GamePath = game;
        var priorCount = fixture.Discovery.Requests.Count;
        await fixture.ViewModel.SaveSettingsCommand.ExecuteAsync();
        Assert.Equal(expectedSteam, fixture.Store.Settings.SteamPath);
        Assert.Equal(expectedGame, fixture.Store.Settings.GamePath);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal(priorCount + 2, fixture.Discovery.Requests.Count);
        Assert.Equal(expectedSteam.Length == 0 ? null : expectedSteam, fixture.Discovery.Requests[priorCount].Steam);
        Assert.Equal(expectedGame.Length == 0 ? null : expectedGame, fixture.Discovery.Requests[priorCount].Game);
        Assert.Contains("安装路径已保存", fixture.Workspace.Status);
    }

    /// <summary>路径校验失败时应保留原偏好并通过工作区显示错误。</summary>
    [Fact]
    public async Task FailedValidationDoesNotOverwritePreferences()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.ViewModel.SteamPath = "错误路径";
        fixture.Discovery.Error = new InvalidOperationException("缺少 Steam.exe");
        await fixture.ViewModel.SaveSettingsCommand.ExecuteAsync();
        Assert.Empty(fixture.Store.Settings.SteamPath);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Contains("缺少 Steam.exe", Assert.Single(fixture.Interaction.Notices));
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>重新自动检测清除两项覆盖偏好并刷新实际安装路径。</summary>
    [Fact]
    public async Task AutoDetectClearsOverridesAndRefreshesEditors()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SteamPath = "手动Steam";
        fixture.Store.Settings.GamePath = "手动游戏";
        await fixture.Workspace.InitializeAsync();
        await fixture.ViewModel.AutoDetectCommand.ExecuteAsync();
        Assert.Empty(fixture.Store.Settings.SteamPath);
        Assert.Empty(fixture.Store.Settings.GamePath);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Null(fixture.Discovery.Requests[^1].Steam);
        Assert.Null(fixture.Discovery.Requests[^1].Game);
        Assert.Equal("C:\\DetectedSteam", fixture.ViewModel.SteamPath);
        Assert.Equal("C:\\DetectedGame", fixture.ViewModel.GamePath);
    }

    /// <summary>明暗主题只有持久化成功后才应用到主题服务。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThemeCommandPersistsAndAppliesRequestedTheme(bool dark)
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.ViewModel.DarkTheme = dark;
        await fixture.ViewModel.ToggleThemeCommand.ExecuteAsync();
        Assert.Equal(dark, fixture.Store.Settings.DarkTheme);
        Assert.Equal(dark, fixture.Theme.Applied[^1]);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    /// <summary>损坏数据库读取失败后，全部设置写入命令应保持无保存副作用。</summary>
    [Fact]
    public async Task StorageFailurePreventsAllSettingsWrites()
    {
        var fixture = new Fixture();
        fixture.Store.LoadError = new InvalidDataException("数据库损坏");
        await fixture.Workspace.InitializeAsync();
        foreach (var command in new[] { fixture.ViewModel.SaveSettingsCommand, fixture.ViewModel.AutoDetectCommand, fixture.ViewModel.ToggleThemeCommand }) await command.ExecuteAsync();
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.False(fixture.Workspace.StorageReady);
    }

    /// <summary>组合真实设置页与共享工作区，仅替换外部存储、发现及界面边界。</summary>
    private sealed class Fixture
    {
        /// <summary>当前测试的偏好存储边界。</summary>
        public MemoryStore Store { get; } = new();
        /// <summary>记录安装校验请求的发现边界。</summary>
        public Discovery Discovery { get; } = new();
        /// <summary>记录通知及目录选择的交互边界。</summary>
        public Interaction Interaction { get; } = new();
        /// <summary>记录实际应用明暗状态的主题边界。</summary>
        public Theme Theme { get; } = new();
        /// <summary>真实共享工作区。</summary>
        public WorkspaceService Workspace { get; }
        /// <summary>当前被测的独立设置页模型。</summary>
        public SettingsPageViewModel ViewModel { get; }
        /// <summary>创建不接触真实 Steam 和游戏的独立测试对象。</summary>
        public Fixture()
        {
            Workspace = new WorkspaceService(Store, Discovery, new Resources(), Interaction, Theme);
            ViewModel = new SettingsPageViewModel(Workspace, Store, Discovery, Interaction, Theme);
        }
        /// <summary>创建已成功读取偏好与发现安装的工作区夹具。</summary>
        public static async Task<Fixture> ReadyAsync()
        {
            var fixture = new Fixture();
            await fixture.Workspace.InitializeAsync();
            return fixture;
        }
    }

    /// <summary>设置页测试使用的内存边界，不替代 SQLite 集成测试。</summary>
    private sealed class MemoryStore : ISettingsStore
    {
        /// <summary>当前真实传递给工作区的偏好对象。</summary>
        public AppSettings Settings { get; } = new();
        /// <summary>读取次数，用于核实页面构造没有磁盘副作用。</summary>
        public int LoadCount { get; private set; }
        /// <summary>成功保存次数。</summary>
        public int SaveCount { get; private set; }
        /// <summary>模拟损坏数据库的读取异常。</summary>
        public Exception? LoadError { get; set; }
        /// <summary>供页面展示的隔离状态目录。</summary>
        public string StateDirectory => "C:\\Fixture\\Data";
        /// <summary>读取偏好或报告指定异常。</summary>
        public AppSettings Load() { LoadCount++; if (LoadError is not null) throw LoadError; return Settings; }
        /// <summary>记录真实模型已完成的持久化请求。</summary>
        public void Save(AppSettings settings) => SaveCount++;
        /// <summary>此页测试不改变账号元数据。</summary>
        public void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts) { }
        /// <summary>此页测试不读取账号缓存。</summary>
        public IReadOnlyList<SteamAccount> GetAccounts() => [];
    }

    /// <summary>记录真实视图模型发出的安装校验与刷新请求。</summary>
    private sealed class Discovery : ISteamDiscoveryService
    {
        /// <summary>按实际调用顺序记录的两个路径覆盖值。</summary>
        public List<(string? Steam, string? Game)> Requests { get; } = [];
        /// <summary>本次安装发现结果。</summary>
        public DiscoveryResult Result { get; set; } = new() { SteamPath = "C:\\DetectedSteam", GamePath = "C:\\DetectedGame" };
        /// <summary>本次安装校验失败原因。</summary>
        public Exception? Error { get; set; }
        /// <summary>记录覆盖值并返回快照或指定校验错误。</summary>
        public DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null) { Requests.Add((steamOverride, gameOverride)); if (Error is not null) throw Error; return Result; }
        /// <summary>设置页测试无需 Steam 账号。</summary>
        public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath) => [];
    }

    /// <summary>为共享工作区提供无资源操作的边界。</summary>
    private sealed class Resources : IResourceSharingService
    {
        /// <summary>设置页没有需要展示的账号资源。</summary>
        public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath) => [];
        /// <summary>设置页没有需要展示的恢复清单。</summary>
        public IReadOnlyList<ShareBackup> GetBackups(string gamePath) => [];
        /// <summary>意外资源变更立即报告，防止设置页夹带资源操作。</summary>
        public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders) => throw new InvalidOperationException("设置页不应变更资源。");
        /// <summary>意外资源恢复立即报告。</summary>
        public void Restore(string backupId) => throw new InvalidOperationException("设置页不应恢复资源。");
        /// <summary>意外资源修复立即报告，确保设置页保持自身职责。</summary>
        public ShareBackup? RepairInvalidSharing(string backupId) => throw new InvalidOperationException("设置页不应修复资源。");
    }

    /// <summary>记录设置页通知和用户选择目录的输入输出。</summary>
    private sealed class Interaction : IUserInteraction
    {
        /// <summary>用户选择的目录，空值表示取消。</summary>
        public string? SelectedFolder { get; set; }
        /// <summary>收到的错误通知正文。</summary>
        public List<string> Notices { get; } = [];
        /// <summary>实际打开目录选择器的标题和初始路径。</summary>
        public List<(string Title, string InitialDirectory)> FolderRequests { get; } = [];
        /// <summary>设置页不执行资源修复确认，测试边界保持取消结果。</summary>
        public Task<bool> ConfirmAsync(string title, string content) => Task.FromResult(false);
        /// <summary>记录界面通知而不打开真实弹窗。</summary>
        public Task ShowNoticeAsync(string title, string content) { Notices.Add(content); return Task.CompletedTask; }
        /// <summary>记录选择器输入并返回本次用户选择。</summary>
        public string? PickFolder(string title, string initialDirectory) { FolderRequests.Add((title, initialDirectory)); return SelectedFolder; }
    }

    /// <summary>记录主题实际应用顺序。</summary>
    private sealed class Theme : IThemeService
    {
        /// <summary>实际应用的明暗状态列表。</summary>
        public List<bool> Applied { get; } = [];
        /// <summary>记录主题变化而不访问真实 WPF 应用。</summary>
        public void Apply(bool dark) => Applied.Add(dark);
    }
}
