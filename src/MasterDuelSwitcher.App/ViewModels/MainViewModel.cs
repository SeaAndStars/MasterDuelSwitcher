using MasterDuelSwitcher.App.Services;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>只管理窗口导航标题与运行环境的 Shell，各页面独立管理自己的业务状态。</summary>
public sealed class MainViewModel : ObservableViewModel
{
    /// <summary>当前进程的真实系统环境。</summary>
    private readonly IApplicationEnvironment _environment;
    /// <summary>当前官方导航页面的标识。</summary>
    private string _currentPage = "Accounts";

    /// <summary>页面共同使用的状态、互斥与刷新协调服务。</summary>
    public WorkspaceService Workspace { get; }
    /// <summary>运行遮罩直接使用的免费开包模型，旧的纯状态调用可保持未注入。</summary>
    public FreePacksPageViewModel? FreePacks { get; }
    /// <summary>当前页面标识，供窗口标题和简短说明使用。</summary>
    public string CurrentPage { get => _currentPage; set { if (Set(ref _currentPage, value)) Notify(nameof(Title), nameof(Subtitle)); } }
    /// <summary>当前页面标题。</summary>
    public string Title => CurrentPage switch { "Resources" => "资源共享", "Backups" => "备份还原", "Settings" => "设置", "FreePacks" => "免费开包", _ => "账号切换" };
    /// <summary>当前页面的简短操作说明。</summary>
    public string Subtitle => CurrentPage switch { "Resources" => "一次更新，多个账号共用已下载的游戏资源。", "Backups" => "检查操作记录，按需恢复独立资源与登录配置。", "Settings" => "检查安装位置，调整本机的工具偏好。", "FreePacks" => "在当前账号中领取免费卡包，并继续下一包。", _ => "选择本机账号，继续下一场决斗。" };
    /// <summary>当前进程是否实际具有管理员权限。</summary>
    public bool IsElevated => _environment.IsElevated;
    /// <summary>供窗口底部和自动化验证读取的真实权限状态。</summary>
    public string AdminStatus => IsElevated ? "管理员权限" : "普通权限";
    /// <summary>窗口标题区使用的共享刷新命令。</summary>
    public AsyncCommand RefreshCommand => Workspace.RefreshCommand;

    /// <summary>注入共享工作区与系统环境，构造时不访问 Steam 或数据库。</summary>
    public MainViewModel(WorkspaceService workspace, IApplicationEnvironment environment, FreePacksPageViewModel? freePacks = null)
    {
        Workspace = workspace;
        _environment = environment;
        FreePacks = freePacks;
    }

    /// <summary>由桌面会话启动共同的只读发现与设置读取。</summary>
    public Task InitializeAsync() => Workspace.InitializeAsync();
}
