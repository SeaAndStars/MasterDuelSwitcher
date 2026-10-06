using System.IO;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.ViewModels;
using MasterDuelSwitcher.App.Views.Pages;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>通过独立工作区快照验证账号页业务，不接触真实 Steam 或目录事务。</summary>
public sealed class AccountsPageViewModelTests
{
    /// <summary>空路径与非路径值保持空图像，让账号首字正常显示。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(123)]
    [InlineData("")]
    [InlineData(" \t")]
    public void AvatarConverterReturnsNullForAbsentPath(object? value)
        => Assert.Null(new DeferredAvatarImageConverter().Convert(value, typeof(BitmapImage), null, CultureInfo.InvariantCulture));

    /// <summary>绑定阶段仅创建延迟图像并保留文件释放选项，尚未读取或冻结像素。</summary>
    [Fact]
    public void AvatarConverterDefersFileDecodeUntilImageControlUsesIt()
    {
        const string path = "C:\\Fixture\\not-created-avatar.png";
        var image = Assert.IsType<BitmapImage>(new DeferredAvatarImageConverter().Convert(path, typeof(BitmapImage), null, CultureInfo.InvariantCulture));
        Assert.Equal(BitmapCreateOptions.DelayCreation, image.CreateOptions);
        Assert.Equal(BitmapCacheOption.OnLoad, image.CacheOption);
        Assert.Equal(path, image.UriSource.LocalPath);
        Assert.False(image.IsFrozen);
    }

    /// <summary>头像转换保持单向绑定，不把界面图像写回路径模型。</summary>
    [Fact]
    public void AvatarConverterDoesNotWriteImageBackToModel()
        => Assert.Same(Binding.DoNothing, new DeferredAvatarImageConverter().ConvertBack(null, typeof(string), null, CultureInfo.InvariantCulture));

    /// <summary>账号保存失败后星标保存保持原持久键值，同时保留未保存的备注与资源选择草稿。</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task FailedAccountSaveDoesNotLeakDraftThroughLaterStarSave(bool hasSavedValues, bool clearBinding)
    {
        var fixture = new Fixture();
        if (hasSavedValues)
        {
            fixture.Store.Settings.AccountNotes["1"] = "原备注";
            fixture.Store.Settings.AccountBindings["1"] = "aabbccdd";
        }
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        fixture.ViewModel.Note = "未保存草稿";
        var draftFolder = clearBinding ? "" : "8899aabb";
        fixture.ViewModel.SelectedBinding = fixture.ViewModel.BindingOptions.Single(option => option.FolderName == draftFolder);
        fixture.Store.SaveError = new IOException("账号保存失败");
        await fixture.ViewModel.SaveAccountCommand.ExecuteAsync();
        Assert.Contains("账号保存失败", Assert.Single(fixture.Interaction.Notices).Content);
        fixture.Store.SaveError = null;
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync(fixture.ViewModel.Accounts.Single(item => item.Account.SteamId == "2"));
        Assert.Contains("2", fixture.Store.Settings.StarredAccounts);
        Assert.Equal(hasSavedValues, fixture.Store.Settings.AccountNotes.ContainsKey("1"));
        Assert.Equal(hasSavedValues ? "原备注" : null, fixture.Store.Settings.AccountNotes.GetValueOrDefault("1"));
        Assert.Equal(hasSavedValues, fixture.Store.Settings.AccountBindings.ContainsKey("1"));
        Assert.Equal(hasSavedValues ? "aabbccdd" : null, fixture.Store.Settings.AccountBindings.GetValueOrDefault("1"));
        Assert.Equal("1", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        Assert.Equal("未保存草稿", fixture.ViewModel.Note);
        Assert.Equal(draftFolder, fixture.ViewModel.SelectedBinding?.FolderName);
    }

    /// <summary>选中和清空账号必须通知真实绑定属性名，使右侧昵称、标识和按钮文字跟随更新。</summary>
    [Fact]
    public async Task SelectionNotifiesActualAccountBindingProperty()
    {
        var fixture = await Fixture.ReadyAsync();
        var names = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => names.Add(args.PropertyName);
        fixture.Select("1");
        Assert.Contains(nameof(AccountsPageViewModel.SelectedAccount), names);
        Assert.DoesNotContain("SetSelection", names);
        names.Clear();
        fixture.ViewModel.SelectedAccount = null;
        Assert.Contains(nameof(AccountsPageViewModel.SelectedAccount), names);
        Assert.False(fixture.ViewModel.HasSelectedAccount);
    }

    /// <summary>头像路径仅在实际改变时通知图像和首字回退状态。</summary>
    [Fact]
    public void AvatarPathNotifiesImageAndFallbackBindings()
    {
        var item = new AccountItem { Account = new SteamAccount { AccountName = "alice" } };
        var names = new List<string?>();
        item.PropertyChanged += (_, args) => names.Add(args.PropertyName);
        Assert.Null(item.AvatarPath);
        Assert.False(item.HasAvatar);
        item.AvatarPath = "C:\\Fixture\\avatar.png";
        Assert.True(item.HasAvatar);
        Assert.Equal(new[] { nameof(AccountItem.AvatarPath), nameof(AccountItem.HasAvatar) }, names);
        names.Clear();
        item.AvatarPath = item.AvatarPath;
        Assert.Empty(names);
        item.AvatarPath = null;
        Assert.False(item.HasAvatar);
        Assert.Equal("A", item.Initial);
    }

    /// <summary>头像请求延迟不会阻塞初始化或账号命令，完成后更新当前同标识行与编辑器。</summary>
    [Fact]
    public async Task AvatarLoadingIsObservableWithoutBlockingWorkspace()
    {
        var fixture = new Fixture();
        fixture.Avatars.Pause = true;
        await fixture.Workspace.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Workspace.IsReady);
        Assert.False(fixture.ViewModel.AvatarLoadingTask.IsCompleted);
        Assert.Equal(2, fixture.Avatars.Requests.Count);
        fixture.Select("1");
        fixture.ViewModel.Note = "后台加载时的编辑";
        fixture.ViewModel.SearchText = "bob";
        fixture.Avatars.Complete("1", "C:\\Fixture\\avatar-1.png");
        fixture.Avatars.Complete("2", null);
        await fixture.ViewModel.AvatarLoadingTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("C:\\Fixture\\avatar-1.png", fixture.ViewModel.SelectedAccount?.AvatarPath);
        Assert.Equal("后台加载时的编辑", fixture.ViewModel.Note);
        Assert.False(Assert.Single(fixture.ViewModel.Accounts).HasAvatar);
        fixture.ViewModel.SearchText = "";
        Assert.Equal("C:\\Fixture\\avatar-1.png", fixture.ViewModel.Accounts.Single(item => item.Account.SteamId == "1").AvatarPath);
    }

    /// <summary>每个账号只请求一次头像，筛选、星标排序与刷新复用同一已缓存路径。</summary>
    [Fact]
    public async Task AvatarRequestsAndPathsAreReusedAcrossListChanges()
    {
        var fixture = new Fixture();
        var path = "C:\\Fixture\\avatar-1.png";
        fixture.Avatars.Results["1"] = path;
        await fixture.Workspace.InitializeAsync();
        await fixture.ViewModel.AvatarLoadingTask;
        fixture.Select("1");
        Assert.Same(path, fixture.ViewModel.SelectedAccount?.AvatarPath);
        fixture.ViewModel.SearchText = "alice";
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync();
        fixture.ViewModel.SearchText = "";
        await fixture.Workspace.RefreshCommand.ExecuteAsync();
        await fixture.ViewModel.AvatarLoadingTask;
        Assert.Equal(new[] { "1", "2" }, fixture.Avatars.Requests.Select(request => request.Id).Order(StringComparer.Ordinal));
        Assert.Same(path, fixture.ViewModel.SelectedAccount?.AvatarPath);
        Assert.Equal("C:\\Fixture\\Steam", fixture.Avatars.Requests[0].Steam);
    }

    /// <summary>图像完整解码失败后清空所有同标识图像，并缓存回退结果避免重新显示坏图片。</summary>
    [Fact]
    public async Task RejectedAvatarFallsBackAndStaysRejectedDuringRebuild()
    {
        var fixture = new Fixture();
        fixture.Avatars.Results["1"] = "C:\\Fixture\\broken.png";
        await fixture.Workspace.InitializeAsync();
        await fixture.ViewModel.AvatarLoadingTask;
        fixture.Select("1");
        Assert.True(fixture.ViewModel.SelectedAccount?.HasAvatar);
        fixture.ViewModel.ReportAvatarFailure("1");
        Assert.Null(fixture.ViewModel.SelectedAccount?.AvatarPath);
        Assert.False(fixture.ViewModel.SelectedAccount?.HasAvatar);
        fixture.ViewModel.SearchText = "alice";
        Assert.Null(Assert.Single(fixture.ViewModel.Accounts).AvatarPath);
        Assert.Equal(2, fixture.Avatars.Requests.Count);
        fixture.ViewModel.ReportAvatarFailure("account-removed-before-image-failed");
    }

    /// <summary>关闭页面取消全部后台头像并移除工作区订阅，重复关闭保持幂等。</summary>
    [Fact]
    public async Task DisposeCancelsAvatarsAndStopsWorkspaceSubscription()
    {
        var fixture = new Fixture();
        fixture.Avatars.Pause = true;
        await fixture.Workspace.InitializeAsync();
        Assert.Equal(2, fixture.Avatars.Requests.Count);
        fixture.ViewModel.Dispose();
        fixture.ViewModel.Dispose();
        await fixture.ViewModel.AvatarLoadingTask.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Discovery.Result = new DiscoveryResult();
        await fixture.Workspace.RefreshCommand.ExecuteAsync();
        Assert.Equal(2, fixture.ViewModel.Accounts.Count);
        fixture.ViewModel.SearchText = "after-close";
        Assert.Equal(2, fixture.Avatars.Requests.Count);
    }

    /// <summary>正常完成但迟于关闭的头像任务只结束观察，不再写入已关闭页面。</summary>
    [Fact]
    public async Task CompletionAfterDisposeDoesNotChangeAvatarState()
    {
        var fixture = new Fixture();
        fixture.Avatars.Pause = true;
        fixture.Avatars.HonorCancellation = false;
        await fixture.Workspace.InitializeAsync();
        Assert.Equal(2, fixture.Avatars.Requests.Count);
        fixture.ViewModel.Dispose();
        fixture.Avatars.Complete("1", "C:\\Fixture\\late.png");
        fixture.Avatars.Complete("2", null);
        await fixture.ViewModel.AvatarLoadingTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(fixture.ViewModel.Accounts, item => Assert.Null(item.AvatarPath));
    }

    /// <summary>仅修改资源绑定或明确清空绑定的草稿在筛选后仍保留，用户可清空可见选择。</summary>
    [Fact]
    public async Task BindingOnlyDraftAndVisibleDeselectionArePreserved()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.AccountBindings["1"] = "aabbccdd";
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        fixture.ViewModel.SelectedBinding = null;
        fixture.ViewModel.SearchText = "alice";
        Assert.Equal("", fixture.ViewModel.SelectedBinding?.FolderName);
        fixture.ViewModel.SelectedAccount = null;
        Assert.Null(fixture.ViewModel.SelectedAccount);
        fixture.Select("1");
        Assert.Equal("", fixture.ViewModel.SelectedBinding?.FolderName);
        Assert.Equal("aabbccdd", fixture.Store.Settings.AccountBindings["1"]);
    }

    /// <summary>用户可直接点击账号行星标而无需先选择账号，排序不自动选择其他账号。</summary>
    [Fact]
    public async Task RowStarDoesNotRequireOrCreateSelection()
    {
        var fixture = await Fixture.ReadyAsync();
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync(fixture.ViewModel.Accounts.Single(item => item.Account.SteamId == "1"));
        Assert.Contains("1", fixture.Store.Settings.StarredAccounts);
        Assert.Null(fixture.ViewModel.SelectedAccount);
        Assert.Equal("1", fixture.ViewModel.Accounts[0].Account.SteamId);
    }

    /// <summary>即时搜索覆盖昵称、登录名、标识、备注、大小写、有序子序列和多词交集。</summary>
    [Theory]
    [InlineData("ALI", "1")]
    [InlineData("ace", "1")]
    [InlineData("艾丝", "1")]
    [InlineData("1", "1")]
    [InlineData("冠军", "1")]
    [InlineData("冠色", "1")]
    [InlineData("ALI 青色", "1")]
    [InlineData("  ALI\t冠军\n", "1")]
    [InlineData("ob", "2")]
    [InlineData("cia", "")]
    [InlineData("ALL", "")]
    [InlineData("ALI nohit", "")]
    [InlineData("不存在", "")]
    [InlineData("", "2,1")]
    [InlineData(" \t\r\n", "2,1")]
    public async Task SearchMatchesAllWordsAcrossAccountFields(string query, string expectedIds)
    {
        var fixture = new Fixture();
        fixture.Store.Settings.AccountNotes["1"] = "冠军 青色";
        await fixture.Workspace.InitializeAsync();
        fixture.ViewModel.SearchText = query;
        Assert.Equal(expectedIds, string.Join(',', fixture.ViewModel.Accounts.Select(item => item.Account.SteamId)));
        Assert.Equal(expectedIds.Length == 0, fixture.ViewModel.NoAccounts);
    }

    /// <summary>搜索没有结果时展示搜索提示，空白搜索仍展示正常登录引导，且关键词不写日志。</summary>
    [Fact]
    public async Task SearchEmptyStateAndNotificationsDoNotLogQuery()
    {
        var fixture = await Fixture.ReadyAsync();
        var names = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => names.Add(args.PropertyName);
        Assert.Equal("", fixture.ViewModel.SearchText);
        var logCount = fixture.Workspace.Logs.Count;
        fixture.ViewModel.SearchText = "private_search_keyword";
        Assert.Equal("没有匹配的账号", fixture.ViewModel.EmptyTitle);
        Assert.Equal("尝试其他关键词。", fixture.ViewModel.EmptyDescription);
        Assert.Contains(nameof(AccountsPageViewModel.EmptyTitle), names);
        Assert.Contains(nameof(AccountsPageViewModel.EmptyDescription), names);
        names.Clear();
        fixture.ViewModel.SearchText = "private_search_keyword";
        Assert.Empty(names);
        Assert.Equal(logCount, fixture.Workspace.Logs.Count);
        Assert.DoesNotContain(fixture.Workspace.Logs, log => log.Contains("private_search_keyword"));
        fixture.ViewModel.SearchText = " ";
        Assert.Equal("还没有可显示的账号", fixture.ViewModel.EmptyTitle);
        Assert.Contains("在 Steam 正常登录", fixture.ViewModel.EmptyDescription);
    }

    /// <summary>星标、官方最近账号、时间、昵称和账号标识组成确定性优先级。</summary>
    [Fact]
    public async Task ListOrdersStarsThenOfficialRecentThenTimestampAndStableIdentity()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.StarredAccounts.UnionWith(["1", "2", "3"]);
        fixture.Discovery.Result = new DiscoveryResult
        {
            Accounts = [
                new SteamAccount { SteamId = "8", AccountName = "last", LastLoginTimestamp = 1 },
                new SteamAccount { SteamId = "7", AccountName = "alpha", LastLoginTimestamp = 99 },
                new SteamAccount { SteamId = "5", AccountName = "beta", LastLoginTimestamp = 99 },
                new SteamAccount { SteamId = "3", AccountName = "Alpha", LastLoginTimestamp = 300 },
                new SteamAccount { SteamId = "4", AccountName = "official", MostRecent = true, LastLoginTimestamp = 1000 },
                new SteamAccount { SteamId = "1", AccountName = "Zulu", LastLoginTimestamp = 100 },
                new SteamAccount { SteamId = "6", AccountName = "ALPHA", LastLoginTimestamp = 99 },
                new SteamAccount { SteamId = "2", AccountName = "starofficial", MostRecent = true, LastLoginTimestamp = 1 }]
        };
        await fixture.Workspace.InitializeAsync();
        Assert.Equal(new[] { "2", "3", "1", "4", "6", "7", "5", "8" }, fixture.ViewModel.Accounts.Select(item => item.Account.SteamId));
        Assert.Equal(new[] { true, true, true, false, false, false, false, false }, fixture.ViewModel.Accounts.Select(item => item.IsStarred));
    }

    /// <summary>所选账号被搜索排除时保留选择和未保存编辑，界面清空选择回写也不丢失草稿。</summary>
    [Fact]
    public async Task SearchPreservesSelectionAndDraftWhileTemporarilyFilteredOut()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        fixture.ViewModel.Note = "未保存的备注";
        fixture.ViewModel.SelectedBinding = fixture.ViewModel.BindingOptions.Single(option => option.FolderName == "8899aabb");
        fixture.ViewModel.SearchText = "bob";
        Assert.Equal("2", Assert.Single(fixture.ViewModel.Accounts).Account.SteamId);
        Assert.Equal("1", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        fixture.ViewModel.SelectedAccount = null;
        Assert.Equal("1", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        fixture.ViewModel.SearchText = "";
        Assert.Equal("1", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        Assert.Equal("未保存的备注", fixture.ViewModel.Note);
        Assert.Equal("8899aabb", fixture.ViewModel.SelectedBinding?.FolderName);
        Assert.Equal(0, fixture.Store.SaveCount);
    }

    /// <summary>排序、隐藏筛选以及工作区刷新不覆盖仍存在账号的未保存备注和资源绑定。</summary>
    [Fact]
    public async Task StarSortVisibilityAndRefreshPreserveCurrentDraft()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        fixture.ViewModel.Note = "未保存";
        fixture.ViewModel.SelectedBinding = fixture.ViewModel.BindingOptions.Single(option => option.FolderName == "8899aabb");
        fixture.ViewModel.ShowHidden = true;
        Assert.Equal("未保存", fixture.ViewModel.Note);
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync(fixture.ViewModel.Accounts.Single(item => item.Account.SteamId == "2"));
        Assert.Equal("1", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        Assert.Equal("未保存", fixture.ViewModel.Note);
        Assert.Equal("8899aabb", fixture.ViewModel.SelectedBinding?.FolderName);
        Assert.False(fixture.Store.Settings.AccountNotes.ContainsKey("1"));
        await fixture.Workspace.RefreshCommand.ExecuteAsync();
        Assert.Equal("未保存", fixture.ViewModel.Note);
        Assert.Equal("8899aabb", fixture.ViewModel.SelectedBinding?.FolderName);
        fixture.ViewModel.SearchText = "ALi";
        Assert.Equal("未保存", fixture.ViewModel.Note);
    }

    /// <summary>离开账号后返回可以继续编辑草稿，成功保存后后续刷新读取新的持久偏好。</summary>
    [Fact]
    public async Task AccountDraftsStaySeparateAndSuccessfulSaveReleasesDraft()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        fixture.ViewModel.Note = "草稿一";
        fixture.ViewModel.SelectedBinding = fixture.ViewModel.BindingOptions.Single(option => option.FolderName == "8899aabb");
        fixture.Select("2");
        Assert.Equal("", fixture.ViewModel.Note);
        Assert.Equal("", fixture.ViewModel.SelectedBinding?.FolderName);
        fixture.Select("1");
        Assert.Equal("草稿一", fixture.ViewModel.Note);
        Assert.Equal("8899aabb", fixture.ViewModel.SelectedBinding?.FolderName);
        await fixture.ViewModel.SaveAccountCommand.ExecuteAsync();
        fixture.Store.Settings.AccountNotes["1"] = "后来保存的备注";
        await fixture.Workspace.RefreshCommand.ExecuteAsync();
        Assert.Equal("后来保存的备注", fixture.ViewModel.Note);
    }

    /// <summary>星标支持所选账号和其他账号行参数，保存后立即置顶并保持当前选择。</summary>
    [Fact]
    public async Task ToggleStarAcceptsSelectedAccountAndRowParameter()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync();
        Assert.Contains("1", fixture.Store.Settings.StarredAccounts);
        Assert.Equal("1", fixture.ViewModel.Accounts[0].Account.SteamId);
        Assert.True(fixture.ViewModel.SelectedAccount?.IsStarred);
        Assert.Equal("取消星标", fixture.ViewModel.SelectedAccount?.StarLabel);
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync(fixture.ViewModel.Accounts.Single(item => item.Account.SteamId == "2"));
        Assert.Contains("2", fixture.Store.Settings.StarredAccounts);
        Assert.Equal("2", fixture.ViewModel.Accounts[0].Account.SteamId);
        Assert.Equal("1", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync();
        Assert.DoesNotContain("1", fixture.Store.Settings.StarredAccounts);
        Assert.Equal("添加星标", fixture.ViewModel.SelectedAccount?.StarLabel);
    }

    /// <summary>星标保存失败恢复原偏好，同一参数重试继续执行原来的星标意图。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStarSaveRestoresOriginalStateBeforeRetry(bool initiallyStarred)
    {
        var fixture = new Fixture();
        if (initiallyStarred) fixture.Store.Settings.StarredAccounts.Add("1");
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        fixture.Store.SaveError = new IOException("星标保存失败");
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync();
        Assert.Equal(initiallyStarred, fixture.Workspace.Settings.StarredAccounts.Contains("1"));
        Assert.Equal(initiallyStarred, fixture.ViewModel.SelectedAccount?.IsStarred);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Contains("星标保存失败", Assert.Single(fixture.Interaction.Notices).Content);
        fixture.Store.SaveError = null;
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync();
        Assert.Equal(!initiallyStarred, fixture.Store.Settings.StarredAccounts.Contains("1"));
        Assert.Equal(!initiallyStarred, fixture.ViewModel.SelectedAccount?.IsStarred);
    }

    /// <summary>星标要求健康数据库和存在的真实账号，拒绝无选择及过期行参数。</summary>
    [Theory]
    [InlineData("selection")]
    [InlineData("storage")]
    [InlineData("stale")]
    public async Task InvalidStarRequestsDoNotWritePreferences(string scenario)
    {
        var fixture = new Fixture();
        if (scenario == "storage") await fixture.Workspace.RefreshDataAsync();
        else await fixture.Workspace.InitializeAsync();
        if (scenario == "storage") fixture.Select("1");
        var parameter = scenario == "stale" ? new AccountItem { Account = new SteamAccount { SteamId = "missing" } } : null;
        await fixture.ViewModel.ToggleStarCommand.ExecuteAsync(parameter);
        Assert.Single(fixture.Interaction.Notices);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Workspace.Settings.StarredAccounts);
    }

    /// <summary>页面在初始化前保持空列表，并在工作区完成发现后接收完整账号。</summary>
    [Fact]
    public async Task ConstructorSubscribesToWorkspaceWithoutReadingStorage()
    {
        var fixture = new Fixture();
        Assert.Same(fixture.Workspace, fixture.ViewModel.Workspace);
        Assert.Equal(0, fixture.Store.LoadCount);
        Assert.True(fixture.ViewModel.NoAccounts);
        Assert.False(fixture.ViewModel.HasSelectedAccount);
        Assert.False(fixture.ViewModel.ShowHidden);
        Assert.Equal("", fixture.ViewModel.Note);
        Assert.Null(fixture.ViewModel.SelectedBinding);
        await fixture.Workspace.InitializeAsync();
        Assert.Equal(new[] { "2", "1" }, fixture.ViewModel.Accounts.Select(account => account.Account.SteamId));
        Assert.Equal("本机账号 · 2/2", fixture.ViewModel.AccountCountText);
        Assert.False(fixture.ViewModel.NoAccounts);
    }

    /// <summary>导航首次创建页面时直接使用已存在的工作区快照。</summary>
    [Fact]
    public async Task LatePageConstructionUsesCurrentSnapshot()
    {
        var fixture = await Fixture.ReadyAsync();
        var page = new AccountsPageViewModel(fixture.Workspace, fixture.Store, fixture.Steam, fixture.Resources, fixture.Interaction);
        Assert.Equal(2, page.Accounts.Count);
        Assert.Equal(1, fixture.Store.LoadCount);
        Assert.Equal("本机账号 · 2/2", page.AccountCountText);
    }

    /// <summary>刷新采用新持久偏好和资源列表，同时保留当前账号选择。</summary>
    [Fact]
    public async Task RefreshPreservesSelectionAndUsesNewSettingsSnapshot()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        var original = fixture.ViewModel.SelectedAccount;
        fixture.Store.Settings.AccountNotes["1"] = "刷新后的备注";
        fixture.Store.Settings.AccountBindings["1"] = "aabbccdd";
        fixture.Resources.Profiles = [Fixture.Profile("aabbccdd", 24)];
        await fixture.Workspace.RefreshCommand.ExecuteAsync();
        Assert.NotSame(original, fixture.ViewModel.SelectedAccount);
        Assert.Equal("1", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        Assert.Equal("刷新后的备注", fixture.ViewModel.Note);
        Assert.Equal("aabbccdd", fixture.ViewModel.SelectedBinding?.FolderName);
        Assert.Equal(2, fixture.ViewModel.BindingOptions.Count);
        Assert.True(fixture.ViewModel.HasSelectedAccount);
    }

    /// <summary>账号不再被真实发现时清空编辑区，并保留明确的未绑定选项。</summary>
    [Fact]
    public async Task RemovedAccountClearsEditorAndSelection()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        fixture.ViewModel.Note = "尚未保存";
        fixture.Discovery.Result = new DiscoveryResult { SteamPath = "C:\\Fixture\\Steam" };
        await fixture.Workspace.RefreshCommand.ExecuteAsync();
        Assert.Null(fixture.ViewModel.SelectedAccount);
        Assert.False(fixture.ViewModel.HasSelectedAccount);
        Assert.True(fixture.ViewModel.NoAccounts);
        Assert.Equal("", fixture.ViewModel.Note);
        Assert.Equal("", Assert.Single(fixture.ViewModel.BindingOptions).FolderName);
        Assert.Same(fixture.ViewModel.BindingOptions[0], fixture.ViewModel.SelectedBinding);
    }

    /// <summary>同值赋值保持安静，编辑和选择变更通知真实绑定属性。</summary>
    [Fact]
    public async Task PropertiesNotifyOnlyChangesAndSelectionDerivedState()
    {
        var fixture = await Fixture.ReadyAsync();
        var names = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => names.Add(args.PropertyName);
        fixture.ViewModel.ShowHidden = false;
        fixture.ViewModel.SelectedAccount = null;
        fixture.ViewModel.Note = "";
        fixture.ViewModel.SelectedBinding = null;
        Assert.Empty(names);
        fixture.Select("1");
        Assert.Contains(nameof(AccountsPageViewModel.HasSelectedAccount), names);
        names.Clear();
        fixture.ViewModel.SelectedAccount = fixture.ViewModel.SelectedAccount;
        fixture.ViewModel.Note = "编辑";
        fixture.ViewModel.Note = "编辑";
        Assert.Equal(new[] { nameof(AccountsPageViewModel.Note) }, names);
    }

    /// <summary>隐藏筛选保留可见选择，显示隐藏账号后可正常选择和取消隐藏。</summary>
    [Fact]
    public async Task HiddenFilterAndToggleKeepOnlyVisibleSelection()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.HiddenAccounts.Add("1");
        await fixture.Workspace.InitializeAsync();
        Assert.Equal("2", Assert.Single(fixture.ViewModel.Accounts).Account.SteamId);
        Assert.Equal("本机账号 · 1/2", fixture.ViewModel.AccountCountText);
        fixture.Select("2");
        fixture.ViewModel.ShowHidden = true;
        Assert.Equal("2", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        fixture.Select("1");
        Assert.True(fixture.ViewModel.SelectedAccount?.IsHidden);
        await fixture.ViewModel.HideAccountCommand.ExecuteAsync();
        Assert.DoesNotContain("1", fixture.Store.Settings.HiddenAccounts);
        Assert.False(fixture.ViewModel.SelectedAccount?.IsHidden);
        await fixture.ViewModel.HideAccountCommand.ExecuteAsync();
        Assert.Contains("1", fixture.Store.Settings.HiddenAccounts);
        fixture.ViewModel.ShowHidden = false;
        Assert.Null(fixture.ViewModel.SelectedAccount);
        fixture.ViewModel.ShowHidden = true;
        Assert.Equal(2, fixture.ViewModel.Accounts.Count);
        Assert.Empty(fixture.Steam.Launched);
    }

    /// <summary>默认隐藏操作立即从列表移除账号，且不修改 Steam 元数据。</summary>
    [Fact]
    public async Task HidingSelectedAccountRemovesItUntilShown()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        await fixture.ViewModel.HideAccountCommand.ExecuteAsync();
        Assert.Null(fixture.ViewModel.SelectedAccount);
        Assert.Single(fixture.ViewModel.Accounts);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal(2, fixture.Workspace.DetectedAccounts.Count);
        Assert.Contains("Steam 账号记录保持原样", fixture.Workspace.Status);
    }

    /// <summary>隐藏和取消隐藏保存失败后恢复原偏好，同一按钮重试仍执行原请求。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedVisibilitySaveRestoresPreferenceAndRetryKeepsOriginalIntent(bool initiallyHidden)
    {
        var fixture = new Fixture();
        if (initiallyHidden) fixture.Store.Settings.HiddenAccounts.Add("1");
        await fixture.Workspace.InitializeAsync();
        fixture.ViewModel.ShowHidden = true;
        fixture.Select("1");
        fixture.Store.SaveError = new IOException("首次保存失败");

        await fixture.ViewModel.HideAccountCommand.ExecuteAsync();

        Assert.Equal(initiallyHidden, fixture.Workspace.Settings.HiddenAccounts.Contains("1"));
        Assert.Equal(initiallyHidden, fixture.ViewModel.SelectedAccount?.IsHidden);
        Assert.Equal(initiallyHidden, fixture.Store.Settings.HiddenAccounts.Contains("1"));
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Contains("首次保存失败", Assert.Single(fixture.Interaction.Notices).Content);
        fixture.Store.SaveError = null;

        await fixture.ViewModel.HideAccountCommand.ExecuteAsync();

        Assert.Equal(!initiallyHidden, fixture.Store.Settings.HiddenAccounts.Contains("1"));
        Assert.Equal(!initiallyHidden, fixture.Workspace.Settings.HiddenAccounts.Contains("1"));
        Assert.Equal(!initiallyHidden, fixture.ViewModel.SelectedAccount?.IsHidden);
        Assert.Equal(1, fixture.Store.SaveCount);
    }

    /// <summary>失效人工绑定保存时继续保留，只有明确取消绑定才删除。</summary>
    [Fact]
    public async Task MissingBindingIsPreservedUntilExplicitUnbind()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.AccountBindings["1"] = "deadbeef";
        fixture.Store.Settings.AccountNotes["1"] = "旧备注";
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        Assert.Equal("旧备注", fixture.ViewModel.Note);
        Assert.Contains("未检测到", fixture.ViewModel.SelectedBinding?.DisplayName);
        Assert.Equal("deadbeef", fixture.ViewModel.SelectedBinding?.FolderName);
        fixture.ViewModel.Note = "  新备注  ";
        await fixture.ViewModel.SaveAccountCommand.ExecuteAsync();
        Assert.Equal("新备注", fixture.Store.Settings.AccountNotes["1"]);
        Assert.Equal("deadbeef", fixture.Store.Settings.AccountBindings["1"]);
        Assert.Equal("新备注", fixture.ViewModel.SelectedAccount?.Title);
        fixture.ViewModel.SelectedBinding = fixture.ViewModel.BindingOptions[0];
        await fixture.ViewModel.SaveAccountCommand.ExecuteAsync();
        Assert.False(fixture.Store.Settings.AccountBindings.ContainsKey("1"));
        Assert.Equal("账号信息已保存。", fixture.Workspace.Status);
    }

    /// <summary>明确绑定存在的资源被持久保存，空选择等同于取消绑定。</summary>
    [Fact]
    public async Task SelectedExistingBindingAndNullSelectionAreSaved()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("2");
        fixture.ViewModel.SelectedBinding = fixture.ViewModel.BindingOptions.Single(option => option.FolderName == "aabbccdd");
        await fixture.ViewModel.SaveAccountCommand.ExecuteAsync();
        Assert.Equal("aabbccdd", fixture.Store.Settings.AccountBindings["2"]);
        fixture.ViewModel.SelectedBinding = null;
        await fixture.ViewModel.SaveAccountCommand.ExecuteAsync();
        Assert.False(fixture.Store.Settings.AccountBindings.ContainsKey("2"));
    }

    /// <summary>添加账号命令给出 Steam 正常登录步骤而不写本地账号记录。</summary>
    [Fact]
    public async Task AddAccountDisplaysSteamInstructionsOnly()
    {
        var fixture = new Fixture();
        await fixture.ViewModel.AddAccountCommand.ExecuteAsync();
        var notice = Assert.Single(fixture.Interaction.Notices);
        Assert.Equal("添加 Steam 账号", notice.Title);
        Assert.Contains("更改账号", notice.Content);
        Assert.Contains("刷新", notice.Content);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Steam.Launched);
    }

    /// <summary>账号写入和切换均要求选择账号，失败后工作区恢复可操作状态。</summary>
    [Theory]
    [InlineData("Save")]
    [InlineData("Hide")]
    [InlineData("Switch")]
    public async Task MissingSelectionReportsErrorWithoutMutation(string operation)
    {
        var fixture = await Fixture.ReadyAsync();
        await fixture.Command(operation).ExecuteAsync();
        Assert.Contains("请先选择一个 Steam 账号", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Resources.Shared);
        Assert.Empty(fixture.Steam.Launched);
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>数据库未健康读取时账号写入被工作区保护，即使发现快照含真实账号。</summary>
    [Theory]
    [InlineData("Save")]
    [InlineData("Hide")]
    [InlineData("Switch")]
    public async Task UnhealthyStorageBlocksAllAccountWrites(string operation)
    {
        var fixture = new Fixture();
        await fixture.Workspace.RefreshDataAsync();
        fixture.Select("1");
        await fixture.Command(operation).ExecuteAsync();
        Assert.Contains("accounts.db", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Steam.Launched);
    }

    /// <summary>保存失败保留原持久备注，并阻止切换或资源关联继续执行。</summary>
    [Fact]
    public async Task SaveFailureDoesNotStartSteam()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        fixture.ViewModel.Note = "尚未保存";
        fixture.Store.SaveError = new IOException("数据库暂不可写");
        await fixture.ViewModel.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.False(fixture.Store.Settings.AccountNotes.ContainsKey("1"));
        Assert.Contains("数据库暂不可写", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Empty(fixture.Resources.Shared);
        Assert.Empty(fixture.Steam.Launched);
    }

    /// <summary>不同来源和目标执行一次准确的资源关联，随后启动并刷新工作区。</summary>
    [Fact]
    public async Task DifferentSourceSharesOnlySelectedTargetThenLaunchesAndRefreshes()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = "11223344";
        fixture.Store.Settings.AccountBindings["1"] = "aabbccdd";
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        await fixture.ViewModel.SwitchAndLaunchCommand.ExecuteAsync();
        var shared = Assert.Single(fixture.Resources.Shared);
        Assert.Equal("C:\\Fixture\\Game", shared.Game);
        Assert.Equal("11223344", shared.Source);
        Assert.Equal(new[] { "aabbccdd" }, shared.Targets);
        Assert.Equal(("C:\\Fixture\\Steam", "1"), Assert.Single(fixture.Steam.Launched));
        Assert.Equal(2, fixture.Discovery.CallCount);
        Assert.Equal("1", fixture.ViewModel.SelectedAccount?.Account.SteamId);
        Assert.Contains(fixture.Workspace.Logs, log => log.Contains("已检查并关联当前账号资源"));
        Assert.Equal("已通过 Steam 启动测试账号", fixture.Workspace.Status);
    }

    /// <summary>账号绑定来源自身时跳过资源事务，包括目录名大小写差异。</summary>
    [Theory]
    [InlineData("11223344", "11223344")]
    [InlineData("AABBCCDD", "aabbccdd")]
    public async Task SourceEqualTargetSkipsSharingAndLaunches(string source, string target)
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = source;
        fixture.Store.Settings.AccountBindings["1"] = target;
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        await fixture.ViewModel.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Empty(fixture.Resources.Shared);
        Assert.Single(fixture.Steam.Launched);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.DoesNotContain(fixture.Workspace.Logs, log => log.Contains("本账号未绑定资源目录"));
    }

    /// <summary>没有来源或没有账号绑定时直接启动，只有来源已选且账号未绑定才显示说明。</summary>
    [Theory]
    [InlineData("", "", false)]
    [InlineData("", "aabbccdd", false)]
    [InlineData("11223344", "", true)]
    public async Task DirectLaunchUsesBindingAndSourcePreferences(string source, string target, bool expectedNotice)
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = source;
        fixture.Store.Settings.AccountBindings["1"] = target;
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        await fixture.ViewModel.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Empty(fixture.Resources.Shared);
        Assert.Single(fixture.Steam.Launched);
        Assert.Equal(expectedNotice, fixture.Workspace.Logs.Any(log => log.Contains("本账号未绑定资源目录")));
    }

    /// <summary>失效绑定在保存后仍被保留，同时阻止资源事务和 Steam 启动。</summary>
    [Fact]
    public async Task MissingBoundTargetRejectsLaunchAndKeepsSavedBinding()
    {
        var fixture = new Fixture();
        fixture.Store.Settings.AccountBindings["1"] = "deadbeef";
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        await fixture.ViewModel.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Equal("deadbeef", fixture.Store.Settings.AccountBindings["1"]);
        Assert.Contains("绑定的资源目录已不存在", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Empty(fixture.Resources.Shared);
        Assert.Empty(fixture.Steam.Launched);
    }

    /// <summary>来源缺失、已共享或尚未下载资源均阻止关联和启动。</summary>
    [Theory]
    [InlineData("deadbeef")]
    [InlineData("aabbccdd")]
    [InlineData("55667788")]
    public async Task InvalidSourceRejectsSharingAndLaunch(string source)
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = source;
        fixture.Store.Settings.AccountBindings["1"] = "8899aabb";
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        await fixture.ViewModel.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Contains("资源来源已改变或尚未完成下载", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Empty(fixture.Resources.Shared);
        Assert.Empty(fixture.Steam.Launched);
    }

    /// <summary>Steam 安装未发现时在任何本地保存和资源关联前停止。</summary>
    [Fact]
    public async Task MissingSteamPathRejectsBeforeSave()
    {
        var fixture = new Fixture();
        fixture.Discovery.Result = new DiscoveryResult { Accounts = fixture.Discovery.Result.Accounts };
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        await fixture.ViewModel.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Contains("Steam 安装目录", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Steam.Launched);
    }

    /// <summary>资源关联异常阻止启动，Steam 异常阻止成功刷新，两者均报告真实错误。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OperationFailureStopsFollowingStages(bool sharingFails)
    {
        var fixture = new Fixture();
        fixture.Store.Settings.SourceProfile = "11223344";
        fixture.Store.Settings.AccountBindings["1"] = "8899aabb";
        if (sharingFails) fixture.Resources.ShareError = new IOException("资源边界错误");
        else fixture.Steam.Error = new InvalidOperationException("Steam 边界错误");
        await fixture.Workspace.InitializeAsync();
        fixture.Select("1");
        await fixture.ViewModel.SwitchAndLaunchCommand.ExecuteAsync();
        Assert.Contains(sharingFails ? "资源边界错误" : "Steam 边界错误", Assert.Single(fixture.Interaction.Notices).Content);
        Assert.Equal(sharingFails ? 0 : 1, fixture.Steam.Launched.Count);
        Assert.Equal(1, fixture.Discovery.CallCount);
        Assert.True(fixture.Workspace.IsReady);
    }

    /// <summary>工作区忙碌时所有账号命令禁用，恢复后重新接受操作。</summary>
    [Fact]
    public async Task WorkspaceBusyStateDisablesEveryAccountCommand()
    {
        var fixture = await Fixture.ReadyAsync();
        fixture.Select("1");
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = fixture.Workspace.RunOperationAsync("隔离测试操作", () => release.Task);
        foreach (var command in new[] { fixture.ViewModel.SaveAccountCommand, fixture.ViewModel.HideAccountCommand, fixture.ViewModel.AddAccountCommand, fixture.ViewModel.SwitchAndLaunchCommand })
        {
            Assert.False(command.CanExecute(null));
            await command.ExecuteAsync();
        }
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Empty(fixture.Interaction.Notices);
        Assert.Empty(fixture.Steam.Launched);
        release.SetResult(true);
        await operation;
        Assert.True(fixture.ViewModel.SaveAccountCommand.CanExecute(null));
    }

    /// <summary>提供互不共享状态的真实工作区和账号页。</summary>
    private sealed class Fixture
    {
        /// <summary>隔离的持久偏好快照。</summary>
        public FakeStore Store { get; } = new();
        /// <summary>隔离的安装发现快照。</summary>
        public FakeDiscovery Discovery { get; } = new();
        /// <summary>记录 Steam 边界请求的服务。</summary>
        public FakeSteam Steam { get; } = new();
        /// <summary>记录资源边界请求的服务。</summary>
        public FakeResources Resources { get; } = new();
        /// <summary>记录用户可见错误的交互。</summary>
        public FakeInteraction Interaction { get; } = new();
        /// <summary>可控制完成时机而不访问网络的头像边界。</summary>
        public FakeAvatar Avatars { get; } = new();
        /// <summary>实际运行共享逻辑的工作区。</summary>
        public WorkspaceService Workspace { get; }
        /// <summary>实际运行账号业务的独立页面模型。</summary>
        public AccountsPageViewModel ViewModel { get; }
        /// <summary>创建完全隔离的服务和真实视图模型。</summary>
        public Fixture()
        {
            Workspace = new WorkspaceService(Store, Discovery, Resources, Interaction, new FakeTheme());
            ViewModel = new AccountsPageViewModel(Workspace, Store, Steam, Resources, Interaction, Avatars);
        }
        /// <summary>完成工作区首次发现后返回夹具。</summary>
        public static async Task<Fixture> ReadyAsync()
        {
            var fixture = new Fixture();
            await fixture.Workspace.InitializeAsync();
            return fixture;
        }
        /// <summary>通过当前可见账号列表选择指定账号。</summary>
        public void Select(string id) => ViewModel.SelectedAccount = ViewModel.Accounts.Single(account => account.Account.SteamId == id);
        /// <summary>按测试场景返回账号命令。</summary>
        public AsyncCommand Command(string operation) => operation switch { "Save" => ViewModel.SaveAccountCommand, "Hide" => ViewModel.HideAccountCommand, _ => ViewModel.SwitchAndLaunchCommand };
        /// <summary>构造仅包含虚构路径的资源快照。</summary>
        public static ResourceProfile Profile(string folder, long bytes, bool linked = false) => new() { FolderName = folder, FullPath = "C:\\Fixture\\Game\\LocalData\\" + folder, Bytes = bytes, IsLinked = linked };
    }

    /// <summary>复制偏好以区分编辑状态与成功保存的状态。</summary>
    private sealed class FakeStore : ISettingsStore
    {
        /// <summary>仅供显示的虚构状态目录。</summary>
        public string StateDirectory => "C:\\Fixture\\State";
        /// <summary>持久保存的独立设置。</summary>
        public AppSettings Settings { get; set; } = new();
        /// <summary>可注入的保存错误。</summary>
        public Exception? SaveError { get; set; }
        /// <summary>健康读取次数。</summary>
        public int LoadCount { get; private set; }
        /// <summary>成功保存次数。</summary>
        public int SaveCount { get; private set; }
        /// <summary>返回独立副本并记录读取。</summary>
        public AppSettings Load() { LoadCount++; return Copy(Settings); }
        /// <summary>只在保存成功时替换持久副本。</summary>
        public void Save(AppSettings settings) { if (SaveError is not null) throw SaveError; Settings = Copy(settings); SaveCount++; }
        /// <summary>账号同步不操作磁盘。</summary>
        public void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts) { }
        /// <summary>测试不从缓存推断可切换账号。</summary>
        public IReadOnlyList<SteamAccount> GetAccounts() => [];
        /// <summary>复制人工字段集合，避免未保存编辑污染断言。</summary>
        private static AppSettings Copy(AppSettings settings) => new() { SteamPath = settings.SteamPath, GamePath = settings.GamePath, SourceProfile = settings.SourceProfile, DarkTheme = settings.DarkTheme, AccountBindings = new(settings.AccountBindings), AccountNotes = new(settings.AccountNotes), HiddenAccounts = new(settings.HiddenAccounts), StarredAccounts = new(settings.StarredAccounts) };
    }

    /// <summary>以确定性快照模拟安装和真实账号发现。</summary>
    private sealed class FakeDiscovery : ISteamDiscoveryService
    {
        /// <summary>包含两个账号的默认安装快照。</summary>
        public DiscoveryResult Result { get; set; } = new() { SteamPath = "C:\\Fixture\\Steam", GamePath = "C:\\Fixture\\Game", Accounts = [new SteamAccount { SteamId = "1", AccountName = "alice", PersonaName = "艾丽丝", RememberPassword = true }, new SteamAccount { SteamId = "2", AccountName = "bob", MostRecent = true }] };
        /// <summary>已执行的发现次数。</summary>
        public int CallCount { get; private set; }
        /// <summary>返回当前快照并记录刷新次数。</summary>
        public DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null) { CallCount++; return Result; }
        /// <summary>只返回隔离账号，不读取真实文件。</summary>
        public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath) => Result.Accounts;
    }

    /// <summary>只记录启动请求，不操作任何进程。</summary>
    private sealed class FakeSteam : ISteamAccountService
    {
        /// <summary>接收到的 Steam 路径和账号。</summary>
        public List<(string Steam, string Account)> Launched { get; } = [];
        /// <summary>可注入的 Steam 业务错误。</summary>
        public Exception? Error { get; set; }
        /// <summary>记录请求并返回确定性成功结果或错误。</summary>
        public Task<string> SwitchAndLaunchAsync(string steamPath, SteamAccount account, CancellationToken cancellationToken = default) { Launched.Add((steamPath, account.SteamId)); return Error is null ? Task.FromResult("已通过 Steam 启动测试账号") : Task.FromException<string>(Error); }
        /// <summary>账号页没有登录还原命令，测试边界保持空操作。</summary>
        public Task<string> RestoreLatestAsync(string steamPath, CancellationToken cancellationToken = default) => Task.FromResult("");
    }

    /// <summary>以确定性资源快照记录共享请求。</summary>
    private sealed class FakeResources : IResourceSharingService
    {
        /// <summary>分别表示有效来源、已共享目录、空目录和目标。</summary>
        public IReadOnlyList<ResourceProfile> Profiles { get; set; } = [Fixture.Profile("11223344", 1024), Fixture.Profile("aabbccdd", 0, true), Fixture.Profile("55667788", 0), Fixture.Profile("8899aabb", 64)];
        /// <summary>共享请求的准确路径、来源和目标。</summary>
        public List<(string Game, string Source, string[] Targets)> Shared { get; } = [];
        /// <summary>可注入的资源事务错误。</summary>
        public Exception? ShareError { get; set; }
        /// <summary>读取当前隔离资源快照。</summary>
        public IReadOnlyList<ResourceProfile> ScanProfiles(string gamePath) => Profiles;
        /// <summary>记录一次事务请求并按设置返回或抛错。</summary>
        public ShareBackup? EnableSharing(string gamePath, string sourceFolder, IEnumerable<string> targetFolders) { Shared.Add((gamePath, sourceFolder, targetFolders.ToArray())); if (ShareError is not null) throw ShareError; return null; }
        /// <summary>测试不包含资源还原事务。</summary>
        public IReadOnlyList<ShareBackup> GetBackups(string gamePath) => [];
        /// <summary>账号页没有资源还原命令。</summary>
        public void Restore(string backupId) { }
        /// <summary>账号页没有失效共享修复命令，保持隔离空结果。</summary>
        public ShareBackup? RepairInvalidSharing(string backupId) => null;
    }

    /// <summary>记录所有实际显示的提示。</summary>
    private sealed class FakeInteraction : IUserInteraction
    {
        /// <summary>按显示顺序保存标题和内容。</summary>
        public List<(string Title, string Content)> Notices { get; } = [];
        /// <summary>记录提示，不创建窗口。</summary>
        public Task ShowNoticeAsync(string title, string content) { Notices.Add((title, content)); return Task.CompletedTask; }
        /// <summary>账号页不请求资源修复确认，隔离边界返回取消。</summary>
        public Task<bool> ConfirmAsync(string title, string content) => Task.FromResult(false);
        /// <summary>账号页不提供安装路径选择器。</summary>
        public string? PickFolder(string title, string initialDirectory) => null;
    }

    /// <summary>隔离工作区初始化时的外观应用。</summary>
    private sealed class FakeTheme : IThemeService
    {
        /// <summary>不访问 WPF 全局主题资源。</summary>
        public void Apply(bool dark) { }
    }

    /// <summary>使用完成信号与虚构路径隔离头像请求、缓存和关闭取消。</summary>
    private sealed class FakeAvatar : IAccountAvatarService
    {
        /// <summary>头像边界收到的安装路径和账号标识。</summary>
        public List<(string Steam, string Id)> Requests { get; } = [];
        /// <summary>每个账号对应的固定头像结果。</summary>
        public Dictionary<string, string?> Results { get; } = [];
        /// <summary>按账号控制后台完成的信号。</summary>
        private readonly Dictionary<string, TaskCompletionSource<string?>> _pending = [];
        /// <summary>是否暂停本次请求供测试显式完成。</summary>
        public bool Pause { get; set; }
        /// <summary>是否配合关闭取消，或模拟迟到的正常响应。</summary>
        public bool HonorCancellation { get; set; } = true;
        /// <summary>记录一次请求，并返回固定结果或可显式释放的异步任务。</summary>
        public Task<string?> GetAvatarPathAsync(string steamPath, string steamId, CancellationToken cancellationToken = default)
        {
            Requests.Add((steamPath, steamId));
            if (!Pause) return Task.FromResult(Results.GetValueOrDefault(steamId));
            var signal = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(steamId, signal);
            return HonorCancellation ? signal.Task.WaitAsync(cancellationToken) : signal.Task;
        }
        /// <summary>完成指定账号的头像请求。</summary>
        public void Complete(string id, string? path) => _pending[id].SetResult(path);
    }
}
