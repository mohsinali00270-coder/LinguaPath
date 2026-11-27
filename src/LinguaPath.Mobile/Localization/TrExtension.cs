using LinguaPath.Mobile.Core.Localization;

namespace LinguaPath.Mobile.Localization;

/// <summary>
/// XAML: Text="{l:Tr SignIn}". Reads the string once when the page is built; changing the language rebuilds the shell
/// (see MauiLanguageService), so there is no live binding to keep up to date.
/// </summary>
[ContentProperty(nameof(Key))]
[AcceptEmptyServiceProvider]
public sealed class TrExtension : IMarkupExtension<string>
{
    public string Key { get; set; } = string.Empty;

    public string ProvideValue(IServiceProvider serviceProvider) => Localizer.T(Key);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
