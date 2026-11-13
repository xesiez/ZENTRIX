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
  private readonly ExplorerStorageService _storageService = new ExplorerStorageService();
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
   Window.Current.CoreWindow.KeyDown += OnCoreWindowKeyDown;
   _pageLifecycleVersion++;
   StorageFile activatedFile = e.Parameter as StorageFile;
   if (activatedFile != null)
    await LoadActivatedFileLocationAsync(activatedFile);
   else if (_currentFolder == null && !_isShowingRemovableRoot)
    await LoadAppStorageAsync();
   else
    await ReloadCurrentLocationAsync();
   if (_lastFocusedControl == null && FilesListView != null) FilesListView.Focus(FocusState.Programmatic);
  }

  protected override void OnNavigatedFrom(NavigationEventArgs e)
  {
   Window.Current.CoreWindow.KeyDown -= OnCoreWindowKeyDown;
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
    else if (_isPageActive && FilesListView != null)
     FilesListView.Focus(FocusState.Programmatic);
   });
  }

  private async Task LoadActivatedFileLocationAsync(StorageFile file)
  {
   try
   {
    StorageFolder parent = await file.GetParentAsync();
    if (parent != null)
    {
     await LoadFolderAsync(parent, false);
     if (_isPageActive) StatusText.Text = "File location opened: " + file.Name;
     return;
    }
   }
   catch (UnauthorizedAccessException ex) { Debug.WriteLine("ZENTRIX file activation access denied: " + ex); }
   catch (IOException ex) { Debug.WriteLine("ZENTRIX file activation I/O failure: " + ex); }
   catch (COMException ex) { Debug.WriteLine("ZENTRIX file activation COM failure: " + ex); }
   catch (Exception ex) { Debug.WriteLine("ZENTRIX file activation failed: " + ex); }

   await LoadAppStorageAsync();
   if (_isPageActive) StatusText.Text = "The selected file location is unavailable. App-local storage is available.";
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
   await LoadFolderAsync(_storageService.GetAppLocalRoot(), false);
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
    StorageEnumerationResult<StorageFolder> result = await _storageService.GetRemovableDevicesAsync();
    if (!IsCurrentRequest(pageVersion, requestVersion)) return;
    if (!result.Succeeded)
    {
     ReportStorageError(result.Error, pageVersion, requestVersion);
     return;
    }
    foreach (StorageFolder device in result.Items)
     _items.Add(new FileEntry { Name = device.DisplayName, Details = "Removable device", IconGlyph = "\uE8B7", Folder = device });
    StatusText.Text = _items.Count == 0 ? "No removable storage detected. App storage is still available." : _items.Count + " removable device(s) found.";
   }
   catch (UnauthorizedAccessException ex) { ReportStorageError(ex, pageVersion, requestVersion); }
   catch (IOException ex) { ReportStorageError(ex, pageVersion, requestVersion); }
   catch (COMException ex) { ReportStorageError(ex, pageVersion, requestVersion); }
   catch (Exception ex) { ReportStorageError(ex, pageVersion, requestVersion); }
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
    // Enumerate folders and files independently so a partial storage failure
    // does not hide the entries that remain accessible.
    StorageEnumerationResult<StorageFolder> folders = await _storageService.EnumerateFoldersAsync(folder);
    if (!IsCurrentRequest(pageVersion, requestVersion)) return;
    if (folders.Succeeded)
    {
     foreach (StorageFolder child in folders.Items)
      _items.Add(new FileEntry { Name = child.Name, Details = "Folder", IconGlyph = "\uE8B7", Folder = child });
    }

    StorageEnumerationResult<StorageFile> files = await _storageService.EnumerateFilesAsync(folder);
    if (!IsCurrentRequest(pageVersion, requestVersion)) return;
    if (files.Succeeded)
    {
     foreach (StorageFile file in files.Items)
      _items.Add(new FileEntry { Name = file.Name, Details = String.IsNullOrEmpty(file.FileType) ? "File" : file.FileType.ToUpperInvariant(), IconGlyph = "\uE8A5", File = file });
    }

    if (!folders.Succeeded && !files.Succeeded)
     StatusText.Text = "This location could not be read. App-local storage remains available.";
    else if (!folders.Succeeded || !files.Succeeded)
     StatusText.Text = _items.Count + " item(s). Some entries could not be read.";
    else
     StatusText.Text = _items.Count == 0 ? "This folder is empty." : _items.Count + " item(s)";
   }
   catch (UnauthorizedAccessException ex) { ReportStorageError(ex, pageVersion, requestVersion); }
   catch (IOException ex) { ReportStorageError(ex, pageVersion, requestVersion); }
   catch (COMException ex) { ReportStorageError(ex, pageVersion, requestVersion); }
   catch (Exception ex) { ReportStorageError(ex, pageVersion, requestVersion); }
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

  private void ReportStorageError(Exception exception, int pageVersion, int requestVersion)
  {
   Debug.WriteLine("ZENTRIX storage operation failed: " + exception);
   if (IsCurrentRequest(pageVersion, requestVersion))
    StatusText.Text = "Storage could not be read. App-local storage remains available.";
  }

  private void ShowFilesWorkspace(string title, string subtitle)
  {
   SectionTitle.Text = title; SectionSubtitle.Text = subtitle;
   FilesWorkspace.Visibility = Visibility.Visible;
  }

  private async void OnAppStorageClick(object sender, RoutedEventArgs e) { await LoadAppStorageAsync(); }
  private async void OnUsbClick(object sender, RoutedEventArgs e) { await LoadRemovableStorageAsync(false); }

  private async void OnBackClick(object sender, RoutedEventArgs e)
  {
   await GoBackAsync();
  }

  private async Task GoBackAsync()
  {
   if (_history.Count > 0)
   {
    ExplorerLocation previous = _history.Pop();
    if (previous.IsRemovableRoot) await LoadRemovableStorageAsync(true);
    else if (previous.Folder != null) await LoadFolderAsync(previous.Folder, false);
    else await LoadAppStorageAsync();
   }
   else if (_isShowingRemovableRoot || _currentFolder != null)
   {
    await LoadAppStorageAsync();
   }
  }

  private void OnOptionsClick(object sender, RoutedEventArgs e)
  {
   ShowOptionsFlyout(OptionsButton);
  }

  private void ShowOptionsFlyout(FrameworkElement target)
  {
   MenuFlyout flyout = new MenuFlyout();
   MenuFlyoutItem refreshItem = new MenuFlyoutItem { Text = "Refresh" };
   refreshItem.Click += async (s, e) => await ReloadCurrentLocationAsync();
   flyout.Items.Add(refreshItem);
   flyout.ShowAt(target ?? OptionsButton);
  }

  private async void OnCoreWindowKeyDown(CoreWindow sender, KeyEventArgs args)
  {
   if (args.VirtualKey == VirtualKey.GamepadB)
   {
    args.Handled = true;
    await GoBackAsync();
   }
   else if (args.VirtualKey == VirtualKey.GamepadX)
   {
    args.Handled = true;
    ShowOptionsFlyout(OptionsButton);
   }
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
