using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Abstractions;

namespace MasterDuelSwitcher.App.Services;

/// <summary>由官方 NavigationView 页面协议从应用 DI 容器解析独立页面。</summary>
public sealed class PageService : INavigationViewPageProvider
{
    /// <summary>页面和对应视图模型所在的应用容器。</summary>
    private readonly IServiceProvider _services;

    /// <summary>注入应用容器，保持页面生命周期与应用会话一致。</summary>
    public PageService(IServiceProvider services) => _services = services;

    /// <summary>解析已注册页面，未注册类型由标准 DI 返回明确错误。</summary>
    public object GetPage(Type pageType) => _services.GetRequiredService(pageType);
}
