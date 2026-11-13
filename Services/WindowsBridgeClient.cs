using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Security.Cryptography.Certificates;
using Windows.Web.Http;

namespace ZENTRIX
{
 /// <summary>
 /// HTTPS-only client for the Windows Companion. The platform certificate
 /// validator is intentionally left enabled; never add a trust-all callback.
 /// </summary>
 public sealed class WindowsBridgeClient : IDisposable
 {
  private HttpClient _http;
  private Uri _baseUri;
  public bool IsPaired { get; private set; }

  public async Task PairAsync(string host, string oneTimeCode, string expectedFingerprint)
  {
   if (String.IsNullOrWhiteSpace(host)) throw new ArgumentException("Enter the Windows host IP address.");
   if (String.IsNullOrWhiteSpace(expectedFingerprint)) throw new ArgumentException("Enter the SHA-256 certificate fingerprint shown by Windows Companion.");
   string hostInput = host.Trim();
   if (!hostInput.Contains("://")) hostInput = "https://" + hostInput;
   Uri hostUri = new Uri(hostInput);
   string hostName = hostUri.Host;
   Uri bootstrapUri = new Uri("http://" + hostName + ":47830/bootstrap/certificate");

   // Bootstrap only the public certificate over HTTP. Trust is established out-of-band
   // by comparing its SHA-256 fingerprint with the value shown on the Windows host.
   using (var bootstrap = new HttpClient())
   {
    var certificateResponse = await bootstrap.GetAsync(bootstrapUri);
    certificateResponse.EnsureSuccessStatusCode();
    IBuffer certificateBuffer = await certificateResponse.Content.ReadAsBufferAsync();
    var sha256 = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
    string actualFingerprint = CryptographicBuffer.EncodeToHexString(sha256.HashData(certificateBuffer));
    string expected = expectedFingerprint.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();
    if (!String.Equals(actualFingerprint, expected, StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("Certificate fingerprint mismatch. No certificate was trusted.");
    var certificate = new Certificate(certificateBuffer);
    CertificateStores.TrustedRootCertificationAuthorities.Add(certificate);
   }

   _baseUri = new Uri("https://" + hostName + ":47831/");
   _http = new HttpClient(); // created after app-container trust store update
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

  public async Task UploadAsync(string rootId, string relativePath, StorageFile source)
  {
   EnsurePaired();
   using (IRandomAccessStream stream = await source.OpenAsync(FileAccessMode.Read))
   {
    var content = new HttpStreamContent(stream.GetInputStreamAt(0));
    content.Headers.ContentLength = (ulong)stream.Size;
    var uri = new Uri(_baseUri, "api/v1/content?rootId=" + Uri.EscapeDataString(rootId) + "&path=" + Uri.EscapeDataString(relativePath));
    var response = await _http.PutAsync(uri, content);
    response.EnsureSuccessStatusCode();
   }
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

  public async Task CreateDirectoryAsync(string rootId, string relativePath)
  {
   EnsurePaired();
   var body = new JsonObject();
   body.Add("rootId", JsonValue.CreateStringValue(rootId));
   body.Add("path", JsonValue.CreateStringValue(relativePath ?? String.Empty));
   var response = await _http.PostAsync(new Uri(_baseUri, "api/v1/directories"),
    new HttpStringContent(body.Stringify(), UnicodeEncoding.Utf8, "application/json"));
   response.EnsureSuccessStatusCode();
  }

  public async Task RenameAsync(string rootId, string relativePath, string newName)
  {
   EnsurePaired();
   var body = new JsonObject();
   body.Add("rootId", JsonValue.CreateStringValue(rootId));
   body.Add("path", JsonValue.CreateStringValue(relativePath));
   body.Add("newName", JsonValue.CreateStringValue(newName));
   var response = await _http.PostAsync(new Uri(_baseUri, "api/v1/operations/rename"),
    new HttpStringContent(body.Stringify(), UnicodeEncoding.Utf8, "application/json"));
   response.EnsureSuccessStatusCode();
  }

  public async Task TransferAsync(string sourceRootId, string sourcePath, string destinationRootId, string destinationPath, bool move)
  {
   EnsurePaired();
   var body = new JsonObject();
   body.Add("sourceRootId", JsonValue.CreateStringValue(sourceRootId));
   body.Add("sourcePath", JsonValue.CreateStringValue(sourcePath));
   body.Add("destinationRootId", JsonValue.CreateStringValue(destinationRootId));
   body.Add("destinationPath", JsonValue.CreateStringValue(destinationPath ?? String.Empty));
   string endpoint = move ? "api/v1/operations/move" : "api/v1/operations/copy";
   var response = await _http.PostAsync(new Uri(_baseUri, endpoint),
    new HttpStringContent(body.Stringify(), UnicodeEncoding.Utf8, "application/json"));
   response.EnsureSuccessStatusCode();
  }

  public async Task DeleteAsync(string rootId, string relativePath)
  {
   EnsurePaired();
   var uri = new Uri(_baseUri, "api/v1/entries?rootId=" + Uri.EscapeDataString(rootId) + "&path=" + Uri.EscapeDataString(relativePath));
   var response = await _http.DeleteAsync(uri);
   response.EnsureSuccessStatusCode();
  }

  private void EnsurePaired()
  {
   if (!IsPaired || _baseUri == null) throw new InvalidOperationException("Pair with the Windows Companion first.");
  }

  public void Dispose() { if (_http != null) _http.Dispose(); }
 }
}
