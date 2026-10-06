using Wpf.Ui.Markup;

namespace MasterDuelSwitcher.App.Views;

/// <summary>Views 层暴露的官方 Fluent 主题资源，保持官方主题源位于应用顶层。</summary>
public sealed class FluentThemeResources : ThemesDictionary
{
    /// <summary>使用官方主题字典的默认浅色初始化。</summary>
    public FluentThemeResources() { }
}

/// <summary>Views 层暴露的官方 Fluent 控件资源。</summary>
public sealed class FluentControlResources : ControlsDictionary
{
    /// <summary>使用官方控件模板初始化。</summary>
    public FluentControlResources() { }
}
