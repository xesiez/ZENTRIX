using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;

namespace ZENTRIX
{
 /// <summary>
 /// HTTPS-only client for the Windows Companion. The platform certificate
 /// validator is intentionally left enabled; never add a trust-all callback.
 /// </summary>
 public sealed class WindowsBridgeClient : IDisposable
 {
  private readonly HttpClient _http = new HttpClient();
  private Uri _baseUri;
  public bool IsPaired { get; private set; }

  public async Task PairAsync(string host, string oneTimeCode)
  {
   if (String.IsNullOrWhiteSpace(host)) throw new ArgumentException("Enter the Windows host IP address.");
   string address = host.Trim();
   if (!address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    address = "https://" + address;
   if (!address.Contains(":47831")) address = address.TrimEnd('/') + ":47831";
   _baseUri = new Uri(address.TrimEnd('/') + "/");
   var body = new JsonObject();
   body.Add("code", JsonValue.CreateStringValue(oneTimeCode ?? String.Empty));
   var response = await _http.PostAsync(new Uri(_baseUri, "api/v1/pair"),
    new HttpStringContent(body.Stringify(), UnicodeEncoding.Utf8, "application/json"));
   response.EnsureSuccessStatusCode();
   JsonObject result = JsonObject.Parse(await response.Content.ReadAsStringAsync());
   string token = result.GetNamedString("token");
   if (String.IsNullOrEmpty(token)) throw new InvalidOperationException("The host did not return a pairing token.");
   _http.DefaultRequestHeaders.TryAppendWithoutValidation("Authorization", "Bearer " + token);
   IsPaired = true;
  }

  public async Task<IReadOnlyList<RemoteEntry>> GetRootsAsync()
  {
   EnsurePaired();
   string json = await _http.GetStringAsync(new Uri(_baseUri, "api/v1/roots"));
   JsonArray array = JsonArray.Parse(json);
   var result = new List<RemoteEntry>();
   foreach (IJsonValue value in array)
   {
    JsonObject item = value.GetObject();
    result.Add(new RemoteEntry { Name = item.GetNamedString("name"), RootId = item.GetNamedString("id"), Path = "", Type = "root" });
   }
   return result;
  }

  public async Task<IReadOnlyList<RemoteEntry>> GetEntriesAsync(string rootId, string relativePath)
  {
   EnsurePaired();
   string url = "api/v1/entries?rootId=" + Uri.EscapeDataString(rootId) + "&path=" + Uri.EscapeDataString(relativePath ?? "");
   JsonArray array = JsonArray.Parse(await _http.GetStringAsync(new Uri(_baseUri, url)));
   var result = new List<RemoteEntry>();
   foreach (IJsonValue value in array)
   {
    JsonObject item = value.GetObject();
    long? size = null;
    if (item.ContainsKey("size") && item.GetNamedValue("size").ValueType == JsonValueType.Number)
     size = (long)item.GetNamedNumber("size");
    result.Add(new RemoteEntry { Name = item.GetNamedString("name"), Type = item.GetNamedString("type"), Path = item.GetNamedString("path"), Size = size, RootId = rootId });
   }
   return result;
  }

  public async Task DownloadToAsync(string rootId, string relativePath, StorageFile destination)
  {
   EnsurePaired();
   string url = "api/v1/content?rootId=" + Uri.EscapeDataString(rootId) + "&path=" + Uri.EscapeDataString(relativePath);
   IInputStream input = await _http.GetInputStreamAsync(new Uri(_baseUri, url));
   using (IRandomAccessStream output = await destination.OpenAsync(FileAccessMode.ReadWrite))
   {
    output.Size = 0;
    await RandomAccessStream.CopyAsync(input, output);
    await output.FlushAsync();
   }
  }

  private void EnsurePaired()
  {
   if (!IsPaired || _baseUri == null) throw new InvalidOperationException("Pair with the Windows Companion first.");
  }

  public void Dispose() { _http.Dispose(); }
 }
}
