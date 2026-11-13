using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace ZENTRIX
{
 public sealed partial class MainPage : Page
 {
  private readonly ObservableCollection<FileEntry> _items = new ObservableCollection<FileEntry>();
  private readonly Stack<ExplorerLocation> _history = new Stack<ExplorerLocation>();
  private StorageFolder _currentFolder;
  private bool _isShowingRemovableRoot;
  private bool _isPageActive;
  private int _pageLifecycleVersion;
  private int _explorerRequestVersion;
  private Control _lastFocusedControl;

  public MainPage() { InitializeComponent(); FilesListView.ItemsSource = _items; }

  protected override async void OnNavigatedTo(NavigationEventArgs e)
  {
   base.OnNavigatedTo(e);
   _isPageActive = true;
   _pageLifecycleVersion++;
   if (_currentFolder == null && !_isShowingRemovableRoot) await LoadAppStorageAsync();
   else await ReloadCurrentLocationAsync();
   if (_lastFocusedControl == null && FilesNavButton != null) FilesNavButton.Focus(FocusState.Programmatic);
  }

  protected override void OnNavigatedFrom(NavigationEventArgs e)
  {
   _isPageActive = false;
   _pageLifecycleVersion++;
   _explorerRequestVersion++;
   base.OnNavigatedFrom(e);
  }

  public async void RecoverAfterReactivation()
  {
   if (!_isPageActive) return;
   _pageLifecycleVersion++;
   _explorerRequestVersion++;
   await ReloadCurrentLocationAsync();
   await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
   {
    if (_isPageActive && _lastFocusedControl != null && _lastFocusedControl.IsLoaded)
     _lastFocusedControl.Focus(FocusState.Programmatic);
    else if (_isPageActive && FilesNavButton != null)
     FilesNavButton.Focus(FocusState.Programmatic);
   });
  }

  private async Task ReloadCurrentLocationAsync()
  {
   if (_isShowingRemovableRoot) await LoadRemovableStorageAsync(true);
   else if (_currentFolder != null) await LoadFolderAsync(_currentFolder, false);
   else await LoadAppStorageAsync();
  }

  private async Task LoadAppStorageAsync()
  {
   if (!_isPageActive) return;
   _history.Clear();
   _isShowingRemovableRoot = false;
   await LoadFolderAsync(ApplicationData.Current.LocalFolder, false);
  }

  private async Task LoadRemovableStorageAsync(bool preserveHistory)
  {
   if (!_isPageActive) return;
   int pageVersion = _pageLifecycleVersion;
   int requestVersion = ++_explorerRequestVersion;
   if (!preserveHistory) _history.Clear();
   _currentFolder = null;
   _isShowingRemovableRoot = true;
   ShowFilesWorkspace("USB devices", "Removable storage");
   _items.Clear();
   StatusText.Text = "Checking removable devices...";
   try
   {
    IReadOnlyList<StorageFolder> devices = await KnownFolders.RemovableDevices.GetFoldersAsync();
    if (!IsCurrentRequest(pageVersion, requestVersion)) return;
    foreach (StorageFolder device in devices)
     _items.Add(new FileEntry { Name = device.DisplayName, Details = "Removable device", IconGlyph = "\uE8B7", Folder = device });
    StatusText.Text = _items.Count == 0 ? "No removable storage detected. App storage is still available." : _items.Count + " removable device(s) found.";
   }
   catch (UnauthorizedAccessException ex) { ReportStorageError(ex); }
   catch (IOException ex) { ReportStorageError(ex); }
   catch (COMException ex) { ReportStorageError(ex); }
   catch (Exception ex) { ReportStorageError(ex); }
  }

  private async Task LoadFolderAsync(StorageFolder folder, bool pushHistory)
  {
   if (!_isPageActive || folder == null) return;
   int pageVersion = _pageLifecycleVersion;
   int requestVersion = ++_explorerRequestVersion;
   if (pushHistory) _history.Push(new ExplorerLocation { Folder = _currentFolder, IsRemovableRoot = _isShowingRemovableRoot });
   _currentFolder = folder;
   _isShowingRemovableRoot = false;
   ShowFilesWorkspace("Files", folder.DisplayName);
   _items.Clear();
   StatusText.Text = "Loading folder...";
   try
   {
    IReadOnlyList<StorageFolder> folders = await folder.GetFoldersAsync();
    if (!IsCurrentRequest(pageVersion, requestVersion)) return;
    foreach (StorageFolder child in folders)
     _items.Add(new FileEntry { Name = child.Name, Details = "Folder", IconGlyph = "\uE8B7", Folder = child });
    IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();
    if (!IsCurrentRequest(pageVersion, requestVersion)) return;
    foreach (StorageFile file in files)
     _items.Add(new FileEntry { Name = file.Name, Details = String.IsNullOrEmpty(file.FileType) ? "File" : file.FileType.ToUpperInvariant(), IconGlyph = "\uE8A5", File = file });
    StatusText.Text = _items.Count == 0 ? "This folder is empty." : _items.Count + " item(s)";
   }
   catch (UnauthorizedAccessException ex) { ReportStorageError(ex); }
   catch (IOException ex) { ReportStorageError(ex); }
   catch (COMException ex) { ReportStorageError(ex); }
   catch (Exception ex) { ReportStorageError(ex); }
  }

  private bool IsCurrentRequest(int pageVersion, int requestVersion)
  {
   return _isPageActive && pageVersion == _pageLifecycleVersion && requestVersion == _explorerRequestVersion;
  }

  private void ReportStorageError(Exception exception)
  {
   Debug.WriteLine("ZENTRIX storage operation failed: " + exception);
   if (_isPageActive) StatusText.Text = "Storage could not be read. App-local storage remains available.";
  }

  private void ShowFilesWorkspace(string title, string subtitle)
  {
   SectionTitle.Text = title; SectionSubtitle.Text = subtitle;
   FilesWorkspace.Visibility = Visibility.Visible; PlaceholderPanel.Visibility = Visibility.Collapsed;
  }

  private void ShowPlaceholder(string title, string description)
  {
   SectionTitle.Text = title; SectionSubtitle.Text = "ZENTRIX";
   FilesWorkspace.Visibility = Visibility.Collapsed; PlaceholderPanel.Visibility = Visibility.Visible;
   PlaceholderTitle.Text = title; PlaceholderText.Text = description;
  }

  private async void OnFilesNavClick(object sender, RoutedEventArgs e) { await ReloadCurrentLocationAsync(); }
  private void OnHomeNavClick(object sender, RoutedEventArgs e) { ShowPlaceholder("Home", "Your console overview and shortcuts will appear here."); }
  private void OnMusicNavClick(object sender, RoutedEventArgs e) { ShowPlaceholder("Music", "The ZENTRIX audio library and playback experience will appear here."); }
  private void OnPhoneLinkNavClick(object sender, RoutedEventArgs e) { ShowPlaceholder("Phone Link", "Secure device pairing and synchronization will appear here."); }
  private async void OnAppStorageClick(object sender, RoutedEventArgs e) { await LoadAppStorageAsync(); }
  private async void OnUsbClick(object sender, RoutedEventArgs e) { await LoadRemovableStorageAsync(false); }

  private async void OnBackClick(object sender, RoutedEventArgs e)
  {
   if (_history.Count > 0)
   {
    ExplorerLocation previous = _history.Pop();
    if (previous.IsRemovableRoot) await LoadRemovableStorageAsync(true);
    else if (previous.Folder != null) await LoadFolderAsync(previous.Folder, false);
    else await LoadAppStorageAsync();
   }
   else if (_isShowingRemovableRoot || _currentFolder != null) await LoadAppStorageAsync();
  }

  private async void OnFileItemClick(object sender, ItemClickEventArgs e)
  {
   FileEntry entry = e.ClickedItem as FileEntry;
   if (entry == null) return;
   if (entry.Folder != null) { await LoadFolderAsync(entry.Folder, true); return; }
   if (entry.File == null) return;
   try
   {
    bool launched = await Launcher.LaunchFileAsync(entry.File);
    StatusText.Text = launched ? "File opened." : "No application can open this file.";
   }
   catch (UnauthorizedAccessException ex) { ReportStorageError(ex); }
   catch (IOException ex) { ReportStorageError(ex); }
   catch (COMException ex) { ReportStorageError(ex); }
   catch (Exception ex) { ReportStorageError(ex); }
  }

  private void OnContentRootGotFocus(object sender, RoutedEventArgs e)
  {
   Control focused = e.OriginalSource as Control;
   if (focused != null && focused.IsTabStop) _lastFocusedControl = focused;
  }

  private sealed class ExplorerLocation
  {
   public StorageFolder Folder { get; set; }
   public bool IsRemovableRoot { get; set; }
  }
 }
}
