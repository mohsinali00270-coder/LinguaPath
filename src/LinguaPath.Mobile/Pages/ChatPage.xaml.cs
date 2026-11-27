using LinguaPath.Mobile.Core.ViewModels;

namespace LinguaPath.Mobile.Pages;

public partial class ChatPage : BasePage
{
    public ChatPage(ChatViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        // Keep the newest message in view.
        viewModel.MessageAdded += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (viewModel.Messages.Count > 0) List.ScrollTo(viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        });
    }
}
