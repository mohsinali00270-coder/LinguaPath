using LinguaPath.Mobile.Core.ViewModels;
using LinguaPath.Mobile.Services;

namespace LinguaPath.Mobile.Pages;

public partial class ProfilePage : BasePage
{
    public ProfilePage(ProfileViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        ShowTheme(ThemePreference.Current);
    }

    // Appearance is a device setting, so it lives here rather than in the ViewModel.
    private void OnThemeClicked(object? sender, EventArgs e)
    {
        var theme = sender == ThemeLightButton ? AppTheme.Light
            : sender == ThemeDarkButton ? AppTheme.Dark
            : AppTheme.Unspecified;
        ThemePreference.Set(theme);
        ShowTheme(theme);
    }

    private void ShowTheme(AppTheme theme)
    {
        var selected = (Style)Application.Current!.Resources["SegmentSelected"];
        var normal = (Style)Application.Current!.Resources["Segment"];
        ThemeSystemButton.Style = theme == AppTheme.Unspecified ? selected : normal;
        ThemeLightButton.Style = theme == AppTheme.Light ? selected : normal;
        ThemeDarkButton.Style = theme == AppTheme.Dark ? selected : normal;
    }
}
