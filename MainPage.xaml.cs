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
  private WindowsBridgeClient _bridgeClient;
  private string _remoteRootId;
  private string _remoteCurrentPath = String.Empty;
  private bool _isRemoteLocation;
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
   else if (_currentFolder == null && !_isShowingRemovableRoot && !_isRemoteLocation)
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
   if (_isRemoteLocation)
   {
    if (String.IsNullOrEmpty(_remoteRootId)) await LoadRemoteRootsAsync();
    else await LoadRemoteEntriesAsync(_remoteRootId, _remoteCurrentPath);
   }
   else if (_isShowingRemovableRoot) await LoadRemovableStorageAsync(true);
   else if (_currentFolder != null) await LoadFolderAsync(_currentFolder, false);
   else await LoadAppStorageAsync();
  }

  private async Task LoadAppStorageAsync()
  {
   if (!_isPageActive) return;
   _history.Clear();
   _isRemoteLocation = false; _remoteRootId = null; _remoteCurrentPath = String.Empty;
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
  private async void OnWindowsPcClick(object sender, RoutedEventArgs e)
  {
   if (_bridgeClient != null && _bridgeClient.IsPaired) { await LoadRemoteRootsAsync(); return; }
   TextBox hostBox = new TextBox { PlaceholderText = "Windows PC IP address (example: 192.168.1.10)", MinWidth = 420 };
   TextBox codeBox = new TextBox { PlaceholderText = "One-time pairing code", MinWidth = 420 };
   StackPanel panel = new StackPanel();
   panel.Children.Add(new TextBlock { Text = "Enter the address shown by ZENTRIX Windows Companion, then its temporary pairing code." });
   panel.Children.Add(hostBox); panel.Children.Add(codeBox);
   ContentDialog dialog = new ContentDialog { Title = "Connect to Windows PC", Content = panel, PrimaryButtonText = "Pair securely", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
   if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
   try
   {
    WindowsBridgeClient client = new WindowsBridgeClient();
    await client.PairAsync(hostBox.Text, codeBox.Text);
    _bridgeClient = client;
    await LoadRemoteRootsAsync();
   }
   catch (Exception ex)
   {
    StatusText.Text = "Secure connection failed. Check the address and certificate trust.";
    ContentDialog error = new ContentDialog { Title = "Secure connection failed", Content = ex.Message, CloseButtonText = "OK" };
    await error.ShowAsync();
   }
  }

  private async Task LoadRemoteRootsAsync()
  {
   if (_bridgeClient == null || !_bridgeClient.IsPaired) return;
   _isRemoteLocation = true; _remoteRootId = null; _remoteCurrentPath = String.Empty;
   ShowFilesWorkspace("Windows PC", "Approved shared folders");
   _items.Clear(); StatusText.Text = "Loading shared folders...";
   try
   {
    IReadOnlyList<RemoteEntry> roots = await _bridgeClient.GetRootsAsync();
    foreach (RemoteEntry root in roots)
     _items.Add(new FileEntry { Name = root.Name, Details = "Shared folder", IconGlyph = "\uE8B7", RemoteRootId = root.RootId, RemotePath = String.Empty, IsRemoteFolder = true });
    StatusText.Text = roots.Count == 0 ? "No folders are shared. Approve a folder in Windows Companion." : roots.Count + " shared folder(s)";
   }
   catch (Exception ex) { StatusText.Text = "Windows PC unavailable: " + ex.Message; }
  }

  private async Task LoadRemoteEntriesAsync(string rootId, string path)
  {
   if (_bridgeClient == null) return;
   _isRemoteLocation = true; _remoteRootId = rootId; _remoteCurrentPath = path ?? String.Empty;
   ShowFilesWorkspace("Windows PC", String.IsNullOrEmpty(path) ? "Shared folder" : path);
   _items.Clear(); StatusText.Text = "Loading remote folder...";
   try
   {
    IReadOnlyList<RemoteEntry> entries = await _bridgeClient.GetEntriesAsync(rootId, path);
    foreach (RemoteEntry remote in entries)
     _items.Add(new FileEntry { Name = remote.Name, Details = remote.Type == "directory" ? "Folder" : (remote.Size.HasValue ? remote.Size.Value.ToString() + " bytes" : "File"), IconGlyph = remote.Type == "directory" ? "\uE8B7" : "\uE8A5", RemoteRootId = rootId, RemotePath = remote.Path, IsRemoteFolder = remote.Type == "directory" });
    StatusText.Text = entries.Count == 0 ? "This remote folder is empty." : entries.Count + " item(s)";
   }
   catch (Exception ex) { StatusText.Text = "Remote location unavailable: " + ex.Message; }
  }

  private async Task OpenRemoteFileAsync(FileEntry entry)
  {
   try
   {
    StorageFile local = await ApplicationData.Current.LocalFolder.CreateFileAsync(Guid.NewGuid().ToString("N") + "_" + entry.Name, CreationCollisionOption.GenerateUniqueName);
    await _bridgeClient.DownloadToAsync(entry.RemoteRootId, entry.RemotePath, local);
    bool launched = await Launcher.LaunchFileAsync(local);
    StatusText.Text = launched ? "Remote file downloaded and opened." : "File downloaded; no compatible app is available.";
   }
   catch (Exception ex) { StatusText.Text = "Download failed: " + ex.Message; }
  }

  private async void OnBackClick(object sender, RoutedEventArgs e)
  {
   await GoBackAsync();
  }

  private async Task GoBackAsync()
  {
   if (_isRemoteLocation)
   {
    if (!String.IsNullOrEmpty(_remoteRootId) && !String.IsNullOrEmpty(_remoteCurrentPath))
    {
     int slash = _remoteCurrentPath.LastIndexOf('/');
     string parent = slash < 0 ? String.Empty : _remoteCurrentPath.Substring(0, slash);
     await LoadRemoteEntriesAsync(_remoteRootId, parent);
    }
    else if (!String.IsNullOrEmpty(_remoteRootId)) await LoadRemoteRootsAsync();
    else await LoadAppStorageAsync();
    return;
   }
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
   if (_isRemoteLocation && _bridgeClient != null && !String.IsNullOrEmpty(_remoteRootId))
   {
    MenuFlyoutItem createFolder = new MenuFlyoutItem { Text = "Create folder" };
    createFolder.Click += async (s, e) => await CreateRemoteFolderAsync();
    flyout.Items.Add(createFolder);
    FileEntry selected = FilesListView.SelectedItem as FileEntry;
    if (selected != null && !String.IsNullOrEmpty(selected.RemoteRootId) && !String.IsNullOrEmpty(selected.RemotePath))
    {
     MenuFlyoutItem rename = new MenuFlyoutItem { Text = "Rename selected" };
     rename.Click += async (s, e) => await RenameSelectedRemoteAsync(selected);
     flyout.Items.Add(rename);
     MenuFlyoutItem copy = new MenuFlyoutItem { Text = "Copy selected to..." };
     copy.Click += async (s, e) => await TransferSelectedRemoteAsync(selected, false);
     flyout.Items.Add(copy);
     MenuFlyoutItem move = new MenuFlyoutItem { Text = "Move selected to..." };
     move.Click += async (s, e) => await TransferSelectedRemoteAsync(selected, true);
     flyout.Items.Add(move);
     MenuFlyoutItem delete = new MenuFlyoutItem { Text = "Delete selected" };
     delete.Click += async (s, e) => await DeleteSelectedRemoteAsync(selected);
     flyout.Items.Add(delete);
    }
   }
   else if (!_isRemoteLocation && _bridgeClient != null && _bridgeClient.IsPaired)
   {
    FileEntry localSelected = FilesListView.SelectedItem as FileEntry;
    if (localSelected != null && localSelected.File != null)
    {
     MenuFlyoutItem upload = new MenuFlyoutItem { Text = "Upload selected to Windows PC..." };
     upload.Click += async (s, e) => await UploadSelectedToWindowsAsync(localSelected.File);
     flyout.Items.Add(upload);
    }
   }
   flyout.ShowAt(target ?? OptionsButton);
  }

  private async Task UploadSelectedToWindowsAsync(StorageFile file)
  {
   try
   {
    IReadOnlyList<RemoteEntry> roots = await _bridgeClient.GetRootsAsync();
    ComboBox rootPicker = new ComboBox { ItemsSource = roots, DisplayMemberPath = "Name", MinWidth = 360, SelectedIndex = roots.Count > 0 ? 0 : -1 };
    TextBox destinationPath = new TextBox { Text = file.Name, PlaceholderText = "Destination path", MinWidth = 360 };
    StackPanel panel = new StackPanel();
    panel.Children.Add(new TextBlock { Text = "Choose an approved destination and filename:" });
    panel.Children.Add(rootPicker); panel.Children.Add(destinationPath);
    ContentDialog dialog = new ContentDialog { Title = "Upload to Windows PC", Content = panel, PrimaryButtonText = "Upload", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
    if (roots.Count == 0 || await dialog.ShowAsync() != ContentDialogResult.Primary) return;
    RemoteEntry root = rootPicker.SelectedItem as RemoteEntry;
    if (root == null || String.IsNullOrWhiteSpace(destinationPath.Text)) return;
    await _bridgeClient.UploadAsync(root.RootId, destinationPath.Text.TrimStart('/'), file);
    StatusText.Text = "Upload completed: " + file.Name;
   }
   catch (Exception ex) { StatusText.Text = "Upload failed: " + ex.Message; }
  }

  private async Task<string> PromptForTextAsync(string title, string placeholder)
  {
   TextBox box = new TextBox { PlaceholderText = placeholder, MinWidth = 360 };
   ContentDialog dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = "Confirm", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
   if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
   return box.Text;
  }

  private async Task CreateRemoteFolderAsync()
  {
   string name = await PromptForTextAsync("Create folder", "Folder name");
   if (String.IsNullOrWhiteSpace(name)) return;
   string path = String.IsNullOrEmpty(_remoteCurrentPath) ? name : _remoteCurrentPath + "/" + name;
   try
   {
    await _bridgeClient.CreateDirectoryAsync(_remoteRootId, path);
    await LoadRemoteEntriesAsync(_remoteRootId, _remoteCurrentPath);
   }
   catch (Exception ex) { StatusText.Text = "Create folder failed: " + ex.Message; }
  }

  private async Task RenameSelectedRemoteAsync(FileEntry entry)
  {
   string name = await PromptForTextAsync("Rename", "New name");
   if (String.IsNullOrWhiteSpace(name)) return;
   try
   {
    await _bridgeClient.RenameAsync(entry.RemoteRootId, entry.RemotePath, name);
    await LoadRemoteEntriesAsync(_remoteRootId, _remoteCurrentPath);
   }
   catch (Exception ex) { StatusText.Text = "Rename failed: " + ex.Message; }
  }

  private async Task TransferSelectedRemoteAsync(FileEntry entry, bool move)
  {
   try
   {
    IReadOnlyList<RemoteEntry> roots = await _bridgeClient.GetRootsAsync();
    ComboBox rootPicker = new ComboBox { ItemsSource = roots, DisplayMemberPath = "Name", MinWidth = 360, SelectedIndex = roots.Count > 0 ? 0 : -1 };
    TextBox destinationPath = new TextBox { PlaceholderText = "Destination folder path (blank = root)", MinWidth = 360 };
    StackPanel panel = new StackPanel();
    panel.Children.Add(new TextBlock { Text = "Choose an approved destination folder:" });
    panel.Children.Add(rootPicker);
    panel.Children.Add(destinationPath);
    ContentDialog dialog = new ContentDialog { Title = move ? "Move selected item" : "Copy selected item", Content = panel, PrimaryButtonText = move ? "Move" : "Copy", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
    if (roots.Count == 0 || await dialog.ShowAsync() != ContentDialogResult.Primary) return;
    RemoteEntry root = rootPicker.SelectedItem as RemoteEntry;
    if (root == null) return;
    string target = String.IsNullOrWhiteSpace(destinationPath.Text) ? entry.Name : destinationPath.Text.TrimEnd('/') + "/" + entry.Name;
    await _bridgeClient.TransferAsync(entry.RemoteRootId, entry.RemotePath, root.RootId, target, move);
    await LoadRemoteEntriesAsync(_remoteRootId, _remoteCurrentPath);
   }
   catch (Exception ex) { StatusText.Text = (move ? "Move" : "Copy") + " failed: " + ex.Message; }
  }

  private async Task DeleteSelectedRemoteAsync(FileEntry entry)
  {
   ContentDialog confirm = new ContentDialog { Title = "Delete permanently?", Content = "Delete " + entry.Name + " from the Windows shared folder?", PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
   if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
   try
   {
    await _bridgeClient.DeleteAsync(entry.RemoteRootId, entry.RemotePath);
    await LoadRemoteEntriesAsync(_remoteRootId, _remoteCurrentPath);
   }
   catch (Exception ex) { StatusText.Text = "Delete failed: " + ex.Message; }
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
   if (!String.IsNullOrEmpty(entry.RemoteRootId))
   {
    if (entry.IsRemoteFolder)
    {
     if (String.IsNullOrEmpty(entry.RemotePath)) await LoadRemoteEntriesAsync(entry.RemoteRootId, String.Empty);
     else await LoadRemoteEntriesAsync(entry.RemoteRootId, entry.RemotePath);
    }
    else await OpenRemoteFileAsync(entry);
    return;
   }
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
