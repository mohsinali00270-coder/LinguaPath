using LinguaPath.Mobile.Core.ViewModels;

namespace LinguaPath.Mobile.Pages;

public partial class ConversationPage : BasePage
{
    public ConversationPage(ConversationViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        // Keep the newest message in view.
        viewModel.MessageAdded += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (viewModel.Messages.Count > 0) Chat.ScrollTo(viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: true);
        });
    }
}
