using Windows.Storage;
namespace ZENTRIX
{
 public sealed class FileEntry
 {
  public string Name { get; set; }
  public string Details { get; set; }
  public string IconGlyph { get; set; }
  public StorageFolder Folder { get; set; }
  public StorageFile File { get; set; }\n  public string RemoteRootId { get; set; }\n  public string RemotePath { get; set; }\n  public bool IsRemoteFolder { get; set; }
 }
}
