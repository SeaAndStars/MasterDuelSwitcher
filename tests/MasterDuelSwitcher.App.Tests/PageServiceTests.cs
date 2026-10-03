using MasterDuelSwitcher.App.Services;
using MasterDuelSwitcher.App.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MasterDuelSwitcher.App.Tests;

/// <summary>验证官方导航页面提供器对遗漏页面注册的明确失败行为。</summary>
public sealed class PageServiceTests
{
    /// <summary>未注册页面由标准 DI 报告错误，而不是静默返回空页面。</summary>
    [Fact]
    public void MissingPageRegistrationReportsTheRequestedPageType()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var pageService = new PageService(services);
        var exception = Assert.Throws<InvalidOperationException>(() => pageService.GetPage(typeof(AccountsPage)));
        Assert.Contains(nameof(AccountsPage), exception.Message);
    }
}
