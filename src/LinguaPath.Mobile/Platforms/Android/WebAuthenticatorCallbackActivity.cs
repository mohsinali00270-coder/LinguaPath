using Android.App;
using Android.Content;
using Android.Content.PM;

namespace LinguaPath.Mobile;

/// <summary>Receives linguapath://auth#... after Google sign-in in the browser.</summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = AppConfig.CallbackScheme)]
public class WebAuthenticatorCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
}
