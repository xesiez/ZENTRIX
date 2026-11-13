using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage;

namespace ZENTRIX
{
 /// <summary>
 /// Isolates UWP storage enumeration from the view. Failures on removable
 /// storage are returned as scoped results and never invalidate LocalFolder.
 /// </summary>
 public sealed class ExplorerStorageService
 {
  public StorageFolder GetAppLocalRoot()
  {
   // App-private storage is granted by the app container; no capability is needed.
   return ApplicationData.Current.LocalFolder;
  }

  public Task<StorageEnumerationResult<StorageFolder>> GetRemovableDevicesAsync()
  {
   return EnumerateFoldersAsync(KnownFolders.RemovableDevices);
  }

  public async Task<StorageEnumerationResult<StorageFolder>> EnumerateFoldersAsync(StorageFolder parent)
  {
   if (parent == null) return StorageEnumerationResult<StorageFolder>.Failure(new ArgumentNullException("parent"));
   try
   {
    IReadOnlyList<StorageFolder> items = await parent.GetFoldersAsync();
    return StorageEnumerationResult<StorageFolder>.Success(items);
   }
   catch (UnauthorizedAccessException ex) { return StorageEnumerationResult<StorageFolder>.Failure(ex); }
   catch (IOException ex) { return StorageEnumerationResult<StorageFolder>.Failure(ex); }
   catch (COMException ex) { return StorageEnumerationResult<StorageFolder>.Failure(ex); }
   catch (Exception ex) { return StorageEnumerationResult<StorageFolder>.Failure(ex); }
  }

  public async Task<StorageEnumerationResult<StorageFile>> EnumerateFilesAsync(StorageFolder parent)
  {
   if (parent == null) return StorageEnumerationResult<StorageFile>.Failure(new ArgumentNullException("parent"));
   try
   {
    IReadOnlyList<StorageFile> items = await parent.GetFilesAsync();
    return StorageEnumerationResult<StorageFile>.Success(items);
   }
   catch (UnauthorizedAccessException ex) { return StorageEnumerationResult<StorageFile>.Failure(ex); }
   catch (IOException ex) { return StorageEnumerationResult<StorageFile>.Failure(ex); }
   catch (COMException ex) { return StorageEnumerationResult<StorageFile>.Failure(ex); }
   catch (Exception ex) { return StorageEnumerationResult<StorageFile>.Failure(ex); }
  }
 }

 public sealed class StorageEnumerationResult<T>
 {
  private StorageEnumerationResult(IReadOnlyList<T> items, Exception error)
  {
   Items = items ?? new List<T>();
   Error = error;
  }

  public IReadOnlyList<T> Items { get; private set; }
  public Exception Error { get; private set; }
  public bool Succeeded { get { return Error == null; } }

  public static StorageEnumerationResult<T> Success(IReadOnlyList<T> items)
  {
   return new StorageEnumerationResult<T>(items, null);
  }

  public static StorageEnumerationResult<T> Failure(Exception error)
  {
   return new StorageEnumerationResult<T>(new List<T>(), error ?? new IOException("Unknown storage error."));
  }
 }
}
