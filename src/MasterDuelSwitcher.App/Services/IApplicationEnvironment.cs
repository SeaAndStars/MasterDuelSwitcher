using System.IO;
using System.Security.Principal;

namespace MasterDuelSwitcher.App.Services;

/// <summary>提供真实进程权限和独立于 EXE 的默认系统数据目录。</summary>
public interface IApplicationEnvironment
{
    /// <summary>默认的本机持久化状态目录。</summary>
    string DefaultStateDirectory { get; }
    /// <summary>当前进程令牌是否具有管理员权限。</summary>
    bool IsElevated { get; }
}

/// <summary>从 Windows 当前用户配置和进程令牌读取实际运行环境。</summary>
public sealed class WindowsApplicationEnvironment : IApplicationEnvironment
{
    /// <summary>系统 LocalApplicationData 下的固定工具目录。</summary>
    public string DefaultStateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MasterDuelSwitcher");
    /// <summary>读取当前 Windows 身份的真实管理员角色。</summary>
    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
