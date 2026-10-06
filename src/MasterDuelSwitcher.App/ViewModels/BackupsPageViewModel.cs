using System.Collections.ObjectModel;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>管理备份页独立的事务选择和还原命令，共用工作区操作状态。</summary>
public sealed class BackupsPageViewModel : ObservableViewModel
{
    /// <summary>执行受核心事务保护的 Steam 登录配置还原。</summary>
    private readonly ISteamAccountService _steam;
    /// <summary>执行受核心路径保护的资源事务还原。</summary>
    private readonly IResourceSharingService _resources;
    /// <summary>用户当前选择的真实资源事务。</summary>
    private ShareBackup? _selectedBackup;

    /// <summary>所有页面共用的实际发现快照、异步互斥和操作反馈。</summary>
    public WorkspaceService Workspace { get; }
    /// <summary>当前游戏安装的真实资源备份记录。</summary>
    public ObservableCollection<ShareBackup> Backups { get; } = [];
    /// <summary>资源备份列表是否为空。</summary>
    public bool NoBackups => Backups.Count == 0;
    /// <summary>当前选中的资源备份，刷新快照后清空。</summary>
    public ShareBackup? SelectedBackup { get => _selectedBackup; set => Set(ref _selectedBackup, value); }
    /// <summary>还原所选资源事务的异步命令。</summary>
    public AsyncCommand RestoreResourcesCommand { get; }
    /// <summary>还原最近 Steam 登录配置的异步命令。</summary>
    public AsyncCommand RestoreSteamCommand { get; }

    /// <summary>注入共享工作区及核心还原边界，构造时不访问真实账号或文件。</summary>
    public BackupsPageViewModel(WorkspaceService workspace, ISteamAccountService steam, IResourceSharingService resources)
    {
        Workspace = workspace;
        _steam = steam;
        _resources = resources;
        RestoreResourcesCommand = Workspace.CreateCommand(nameof(RestoreResourcesCommand), _ => Workspace.RunOperationAsync("正在还原独立资源目录…", RestoreResourcesAsync));
        RestoreSteamCommand = Workspace.CreateCommand(nameof(RestoreSteamCommand), _ => Workspace.RunOperationAsync("正在还原 Steam 登录配置…", RestoreSteamAsync));
        Workspace.DataChanged += OnDataChanged;
        RefreshBackups();
    }

    /// <summary>共同快照刷新后替换备份页集合和选择状态。</summary>
    private void OnDataChanged(object? sender, EventArgs args) => RefreshBackups();

    /// <summary>复制最近扫描的历史事务，清空旧选择并更新空状态提示。</summary>
    private void RefreshBackups()
    {
        Replace(Backups, Workspace.Backups);
        SelectedBackup = null;
        Notify(nameof(NoBackups));
    }

    /// <summary>校验所选记录并在后台执行资源还原，完成后重新扫描真实状态。</summary>
    private async Task RestoreResourcesAsync()
    {
        var backup = SelectedBackup ?? throw new InvalidOperationException("请先选择一条资源共享备份。");
        if (backup.Restored)
        {
            Workspace.AddLog("这条备份已经完成还原。");
            return;
        }
        await Task.Run(() => _resources.Restore(backup.Id));
        await Workspace.RefreshDataAsync();
        Workspace.AddLog("选中的资源备份已还原。");
    }

    /// <summary>验证检测到的 Steam 路径，还原最近登录配置并刷新共同快照。</summary>
    private async Task RestoreSteamAsync()
    {
        Workspace.EnsureSteamPath();
        var result = await _steam.RestoreLatestAsync(Workspace.ActiveSteamPath);
        await Workspace.RefreshDataAsync();
        Workspace.AddLog(result);
    }
}
