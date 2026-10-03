using System.Collections.ObjectModel;
using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>管理资源页自己的来源选择、共享校验与资源快照。</summary>
public sealed class ResourcesPageViewModel : ObservableViewModel
{
    /// <summary>保存用户显式来源偏好的设置边界。</summary>
    private readonly ISettingsStore _store;
    /// <summary>执行实际共享与失效事务修复的资源边界。</summary>
    private readonly IResourceSharingService _resources;
    /// <summary>失效修复前的官方 Fluent 明确确认边界。</summary>
    private readonly IUserInteraction _interaction;
    /// <summary>当前选择的独立来源。</summary>
    private ResourceProfile? _selectedSource;
    /// <summary>当前选择的待检查旧事务。</summary>
    private ShareBackup? _selectedRepairBackup;
    /// <summary>当前资源快照的容量摘要。</summary>
    private string _summary = "尚未检测到资源目录";
    /// <summary>所有页面共享的安装、忙碌与日志状态。</summary>
    public WorkspaceService Workspace { get; }
    /// <summary>资源页展示的真实账号资源快照。</summary>
    public ObservableCollection<ResourceProfile> Profiles { get; } = [];
    /// <summary>包含已下载资源且尚未共享的来源候选。</summary>
    public ObservableCollection<ResourceProfile> SourceProfiles { get; } = [];
    /// <summary>用户显式选择的资源来源。</summary>
    public ResourceProfile? SelectedSource { get => _selectedSource; set => Set(ref _selectedSource, value); }
    /// <summary>当前快照的目录数、共享数与独立资源容量。</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    /// <summary>是否需要显示资源目录空状态。</summary>
    public bool NoProfiles => Profiles.Count == 0;
    /// <summary>保存用户选定的来源账号。</summary>
    public AsyncCommand SaveSourceCommand { get; }
    /// <summary>保留目标原资源并启用共享。</summary>
    public AsyncCommand EnableSharingCommand { get; }
    /// <summary>尚未还原且需要用户核对现场的共享记录。</summary>
    public ObservableCollection<ShareBackup> RepairBackups { get; } = [];
    /// <summary>用户选定的失效共享记录。</summary>
    public ShareBackup? SelectedRepairBackup { get => _selectedRepairBackup; set => Set(ref _selectedRepairBackup, value); }
    /// <summary>是否没有待检查的共享记录。</summary>
    public bool NoRepairBackups => RepairBackups.Count == 0;
    /// <summary>明确确认后归档失效事务并保留当前目录重新共享。</summary>
    public AsyncCommand RepairSharingCommand { get; }

    /// <summary>注入共享状态与独立业务服务，构造阶段保持只读。</summary>
    public ResourcesPageViewModel(WorkspaceService workspace, ISettingsStore store, IResourceSharingService resources, IUserInteraction interaction)
    {
        Workspace = workspace;
        _store = store;
        _resources = resources;
        _interaction = interaction;
        SaveSourceCommand = workspace.CreateCommand(nameof(SaveSourceCommand), _ => workspace.RunOperationAsync("正在保存资源来源…", SaveSourceAsync));
        EnableSharingCommand = workspace.CreateCommand(nameof(EnableSharingCommand), _ => workspace.RunOperationAsync("正在备份并关联共享资源…", EnableSharingAsync));
        RepairSharingCommand = workspace.CreateCommand(nameof(RepairSharingCommand), _ => workspace.RunOperationAsync("正在检查并修复失效共享…", RepairSharingAsync));
        workspace.DataChanged += OnDataChanged;
        RefreshSnapshot();
    }

    /// <summary>收到公共工作区的新快照后同步本页资源和历史选择。</summary>
    private void OnDataChanged(object? sender, EventArgs args) => RefreshSnapshot();

    /// <summary>替换本页集合并只恢复明确保存的来源和仍存在的旧事务选择。</summary>
    private void RefreshSnapshot()
    {
        var selectedRepairId = SelectedRepairBackup?.Id;
        Replace(Profiles, Workspace.Profiles);
        Replace(SourceProfiles, Workspace.Profiles.Where(profile => !profile.IsLinked && profile.Bytes > 0));
        SelectedSource = SourceProfiles.FirstOrDefault(profile => string.Equals(profile.FolderName, Workspace.Settings.SourceProfile, StringComparison.OrdinalIgnoreCase));
        Summary = NoProfiles ? "尚未检测到资源目录" : $"{Profiles.Count} 个目录 · {Profiles.Count(profile => profile.IsLinked)} 个已共享 · 独立资源 {FormatBytes(Profiles.Where(profile => !profile.IsLinked).Sum(profile => profile.Bytes))}";
        Replace(RepairBackups, Workspace.Backups.Where(backup => !backup.Restored));
        SelectedRepairBackup = RepairBackups.FirstOrDefault(backup => string.Equals(backup.Id, selectedRepairId, StringComparison.Ordinal));
        Notify(nameof(NoProfiles), nameof(NoRepairBackups));
    }

    /// <summary>验证工作区当前真实快照，拒绝过时选择、链接或未下载的来源。</summary>
    private ResourceProfile RequireSelectedSource()
    {
        Workspace.EnsureStorageReady();
        if (string.IsNullOrEmpty(Workspace.ActiveGamePath)) throw new InvalidOperationException("请先检测 Master Duel 安装目录。");
        var selected = SelectedSource ?? throw new InvalidOperationException("请先选择已完成更新的独立资源来源。");
        var source = Workspace.Profiles.FirstOrDefault(profile => string.Equals(profile.FolderName, selected.FolderName, StringComparison.OrdinalIgnoreCase));
        if (source is null || source.IsLinked || source.Bytes <= 0)
            throw new InvalidOperationException("资源来源已改变或尚未完成下载。请刷新并选择有资源的独立目录。");
        return source;
    }

    /// <summary>保存并记录已验证来源，不覆盖其他用户偏好。</summary>
    private void SaveSource(ResourceProfile source)
    {
        Workspace.Settings.SourceProfile = source.FolderName;
        _store.Save(Workspace.Settings);
        Workspace.AddLog($"资源来源已保存：{source.FolderName}");
    }

    /// <summary>保存按钮重新验证用户显式选择并持久化来源。</summary>
    private Task SaveSourceAsync()
    {
        SaveSource(RequireSelectedSource());
        return Task.CompletedTask;
    }

    /// <summary>将其余真实账号目录作为目标，保存来源后运行可恢复共享事务。</summary>
    private async Task EnableSharingAsync()
    {
        var source = RequireSelectedSource();
        var targets = Workspace.Profiles.Where(profile => !string.Equals(profile.FolderName, source.FolderName, StringComparison.OrdinalIgnoreCase)).Select(profile => profile.FolderName).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("当前只有来源目录。请先使用其他账号启动游戏，生成对应目录后刷新。");
        SaveSource(source);
        var backup = await Task.Run(() => _resources.EnableSharing(Workspace.ActiveGamePath, source.FolderName, targets));
        await Workspace.RefreshDataAsync();
        Workspace.AddLog(backup is null ? "资源共享已是最新状态。" : $"资源共享已启用；已保留 {backup.Entries.Count} 个目录的还原记录。");
    }

    /// <summary>明确确认旧备份丢失事实后，请求核心保留当前目录并重新共享。</summary>
    private async Task RepairSharingAsync()
    {
        Workspace.EnsureStorageReady();
        var selected = SelectedRepairBackup ?? throw new InvalidOperationException("请选择一条需要修复的共享记录。");
        var backup = Workspace.Backups.FirstOrDefault(item => string.Equals(item.Id, selected.Id, StringComparison.Ordinal));
        if (backup is null) throw new InvalidOperationException("共享记录已改变，请刷新后重新选择。");
        if (!await _interaction.ConfirmAsync("修复失效共享", "仅在旧备份已被手动删除、共享链接已失效时继续。\n\n当前目录会完整保留为新备份，旧记录原样归档为失效历史，随后重新建立共享。旧备份中的原资源不会被标记为已还原。\n\n请先确认来源账号已完成更新并退出游戏。")) return;
        var repaired = await Task.Run(() => _resources.RepairInvalidSharing(backup.Id));
        await Workspace.RefreshDataAsync();
        Workspace.AddLog(repaired is null ? "失效记录已归档；共享已是最新状态。" : "失效共享已修复；旧记录已归档，当前目录已保留为新备份。");
    }

    /// <summary>以吉字节或兆字节显示当前独立资源容量。</summary>
    private static string FormatBytes(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:F2} GB" : $"{bytes / 1048576d:F1} MB";
}
