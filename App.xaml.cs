using System;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
namespace ZENTRIX
{
 sealed partial class App : Application
 {
  public App() { InitializeComponent(); Suspending += OnSuspending; Resuming += OnResuming; }
  protected override void OnLaunched(LaunchActivatedEventArgs e)
  {
   Frame rootFrame = Window.Current.Content as Frame;
   if (rootFrame == null) { rootFrame = new Frame(); rootFrame.NavigationFailed += OnNavigationFailed; Window.Current.Content = rootFrame; }
   if (rootFrame.Content == null) rootFrame.Navigate(typeof(MainPage), e.Arguments);
   Window.Current.Activate();
  }
  private void OnNavigationFailed(object sender, NavigationFailedEventArgs e) { throw new InvalidOperationException("Failed to load page: " + e.SourcePageType.FullName); }
  private void OnResuming(object sender, object e)
  {
   Frame rootFrame = Window.Current.Content as Frame;
   MainPage page = rootFrame == null ? null : rootFrame.Content as MainPage;
   if (page != null) page.RecoverAfterReactivation();
  }
  private void OnSuspending(object sender, SuspendingEventArgs e) { }
 }
}
