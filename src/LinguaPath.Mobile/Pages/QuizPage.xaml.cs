using LinguaPath.Mobile.Core.ViewModels;

namespace LinguaPath.Mobile.Pages;

public partial class QuizPage : BasePage
{
    private readonly QuizViewModel _vm;
    private IDispatcherTimer? _timer;
    private Window? _window;

    public QuizPage(QuizViewModel viewModel) : base(viewModel)
    {
        _vm = viewModel;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();

        // Integrity log (FR-L65) and resume with the server's time left (FR-L62).
        _window = Window;
        if (_window is not null)
        {
            _window.Stopped -= OnStopped;
            _window.Stopped += OnStopped;
            _window.Resumed -= OnResumed;
            _window.Resumed += OnResumed;
        }
    }

    protected override void OnDisappearing()
    {
        _timer?.Stop();
        if (_window is not null)
        {
            _window.Stopped -= OnStopped;
            _window.Resumed -= OnResumed;
        }
        base.OnDisappearing();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        try
        {
            await _vm.TickAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Quiz tick failed: {ex}");
        }
    }

    private async void OnStopped(object? sender, EventArgs e)
    {
        try
        {
            await _vm.OnBackgroundedAsync();
        }
        catch
        {
            // Logging only.
        }
    }

    private async void OnResumed(object? sender, EventArgs e)
    {
        try
        {
            await _vm.OnAppearingAsync();
        }
        catch
        {
            // The next tick or answer will catch up.
        }
    }
}
