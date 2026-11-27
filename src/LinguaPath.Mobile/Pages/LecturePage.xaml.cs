using System.ComponentModel;
using LinguaPath.Mobile.Core.ViewModels;
using LinguaPath.Shared;

namespace LinguaPath.Mobile.Pages;

public partial class LecturePage : BasePage
{
    private readonly LectureViewModel _vm;
    private IDispatcherTimer? _timer;

    public LecturePage(LectureViewModel viewModel) : base(viewModel)
    {
        _vm = viewModel;
        InitializeComponent();
        _vm.PropertyChanged += OnViewModelChanged;
        Reader.Navigating += OnReaderNavigating;
        Reader.Navigated += OnReaderNavigated;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _timer?.Stop();
        base.OnDisappearing();
    }

    private void OnTick(object? sender, EventArgs e) => _vm.Tick();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LectureViewModel.Html) && _vm.Html is { } html)
            Dispatcher.Dispatch(() => Reader.Source = new HtmlWebViewSource { Html = html });
    }

    /// <summary>Word taps arrive as linguapath://word?w=...; real links open in the browser.</summary>
    private async void OnReaderNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri)) return;

        if (uri.Scheme == LectureHtml.WordScheme)
        {
            e.Cancel = true;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var word = query["w"];
            if (!string.IsNullOrWhiteSpace(word)) await _vm.LookupWordAsync(word);
            return;
        }

        if (uri.Scheme is "http" or "https" or "mailto")
        {
            e.Cancel = true;
            await Launcher.Default.OpenAsync(uri);
        }
    }

    /// <summary>Size the WebView to its content so the whole page scrolls as one.</summary>
    private async void OnReaderNavigated(object? sender, WebNavigatedEventArgs e)
    {
        try
        {
            await Task.Delay(150);
            var result = await Reader.EvaluateJavaScriptAsync("document.body.scrollHeight");
            if (double.TryParse(result, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0)
                Reader.HeightRequest = h + 24;
        }
        catch
        {
            // Keep the default height.
        }
    }
}

public sealed class DownloadTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? "✓ Offline copy" : "⬇ Download";

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
