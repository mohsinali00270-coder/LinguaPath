using System.Globalization;
using LinguaPath.Mobile.Core.Localization;
using LinguaPath.Mobile.Core.ViewModels;

namespace LinguaPath.Mobile.Pages;

/// <summary>Binds the ViewModel, forwards Shell query parameters and calls OnAppearingAsync.</summary>
public abstract class BasePage : ContentPage, IQueryAttributable
{
    protected BasePage(BaseViewModel viewModel)
    {
        ViewModel = viewModel;
        BindingContext = viewModel;
    }

    protected BaseViewModel ViewModel { get; }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyParameters(query);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await ViewModel.OnAppearingAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnAppearing failed on {GetType().Name}: {ex}");
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        try
        {
            await ViewModel.OnDisappearingAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnDisappearing failed on {GetType().Name}: {ex}");
        }
    }
}

public sealed class InvertedBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
}

public sealed class HasTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !string.IsNullOrWhiteSpace(value?.ToString());
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Roadmap lecture/module state → short label (FR-L30), in the app language.</summary>
public sealed class RoadmapStateTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LinguaPath.Shared.RoadmapItemState.Completed => Localizer.T("Ui_State_Done"),
        LinguaPath.Shared.RoadmapItemState.InProgress => Localizer.T("Ui_State_InProgress"),
        LinguaPath.Shared.RoadmapItemState.Available => Localizer.T("Ui_State_Next"),
        _ => Localizer.T("Ui_State_Locked")
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Roadmap state → accent colour (readable as text in both themes).</summary>
public sealed class RoadmapStateColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        return value switch
        {
            LinguaPath.Shared.RoadmapItemState.Completed => Color.FromArgb(dark ? "#2DD4BF" : "#0F766E"),
            LinguaPath.Shared.RoadmapItemState.InProgress => Color.FromArgb(dark ? "#FBBF24" : "#B45309"),
            LinguaPath.Shared.RoadmapItemState.Available => Color.FromArgb(dark ? "#C4B5FD" : "#6D28D9"),
            _ => Color.FromArgb(dark ? "#6E7A93" : "#8A93A6")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Roadmap state → soft background tint for the state pill / timeline node.</summary>
public sealed class RoadmapStateTintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        return value switch
        {
            LinguaPath.Shared.RoadmapItemState.Completed => Color.FromArgb(dark ? "#0E3B3B" : "#E3F8F5"),
            LinguaPath.Shared.RoadmapItemState.InProgress => Color.FromArgb(dark ? "#3D2C0B" : "#FEF3C7"),
            LinguaPath.Shared.RoadmapItemState.Available => Color.FromArgb(dark ? "#2A1D52" : "#F1EBFE"),
            _ => Color.FromArgb(dark ? "#172340" : "#F4F5FA")
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Roadmap state → icon geometry from Resources/Styles/Icons.xaml (check, play, lock…).</summary>
public sealed class RoadmapStateIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            LinguaPath.Shared.RoadmapItemState.Completed => "IconCheck",
            LinguaPath.Shared.RoadmapItemState.InProgress => "IconClock",
            LinguaPath.Shared.RoadmapItemState.Available => "IconPlay",
            _ => "IconLock"
        };
        return Application.Current?.Resources.TryGetValue(key, out var geometry) == true ? geometry : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true when a roadmap item is locked (dims the row).</summary>
public sealed class RoadmapStateLockedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not (LinguaPath.Shared.RoadmapItemState.Completed or LinguaPath.Shared.RoadmapItemState.InProgress or LinguaPath.Shared.RoadmapItemState.Available);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>MyRating + star number (ConverterParameter) → filled or empty star.</summary>
public sealed class RatingStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int rating && int.TryParse(parameter?.ToString(), out var star) && rating >= star ? "★" : "☆";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → right-to-left text (Arabic, Urdu, Hebrew…).</summary>
public sealed class RtlConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FlowDirection.RightToLeft : FlowDirection.MatchParent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Pronunciation band ("good", "ok", "poor") or dictation DiffKind → colour (FR-L92, FR-L93).</summary>
public sealed class ScoreColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "good" or LinguaPath.Shared.DiffKind.Correct => Color.FromArgb("#059669"),
        "ok" or LinguaPath.Shared.DiffKind.Wrong => Color.FromArgb("#D97706"),
        "poor" or LinguaPath.Shared.DiffKind.Missing => Color.FromArgb("#DC2626"),
        _ => Color.FromArgb("#9CA3AF")
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0–100 percentage (int, double or decimal) → 0–1 for ProgressRing / ProgressBar.</summary>
public sealed class PercentToFractionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return value is null ? 0.0 : Math.Clamp(System.Convert.ToDouble(value, CultureInfo.InvariantCulture) / 100.0, 0, 1);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return 0.0;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
