using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MasterDuelSwitcher.App.Services;

/// <summary>应用官方 WPF UI 明暗主题和 Windows 系统强调色。</summary>
public sealed class FluentThemeService : IThemeService
{
    /// <summary>切换官方明暗主题并跟随 Windows 系统强调色。</summary>
    public void Apply(bool dark)
    {
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, true);
    }
}
