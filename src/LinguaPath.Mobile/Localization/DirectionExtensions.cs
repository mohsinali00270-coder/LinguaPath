using LinguaPath.Mobile.Core.Localization;

namespace LinguaPath.Mobile.Localization;

/// <summary>
/// XAML: ScaleX="{l:Mirror}" on a directional icon (chevron, arrow). Returns -1 in right-to-left languages (Urdu)
/// so "forward" icons point the way the text reads. Like {l:Tr}, it is read when the page is built; changing the
/// language rebuilds the shell.
/// </summary>
[AcceptEmptyServiceProvider]
public sealed class MirrorExtension : IMarkupExtension<double>
{
    public double ProvideValue(IServiceProvider serviceProvider) => Localizer.IsRightToLeft ? -1 : 1;

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
