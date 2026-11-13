using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ZENTRIX.Companion;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new CompanionForm());
    }
}

internal sealed class CompanionForm : Form
{
    private readonly ListBox _roots = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 54, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _add = new() { Text = "Approve folder…", AutoSize = true };
    private readonly Button _remove = new() { Text = "Revoke selected", AutoSize = true };
    private readonly Button _start = new() { Text = "Start secure service", AutoSize = true };
    private readonly Button _stop = new() { Text = "Stop service", AutoSize = true, Enabled = false };
    private readonly RootStore _store = new();
    private WebApplication? _server;
    private readonly string _pairCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
    private string? _sessionToken;
    private bool _pairCodeUsed;

    public CompanionForm()
    {
        Text = "ZENTRIX Windows Companion";
        Width = 760; Height = 520; MinimumSize = new Size(620, 420);
        StartPosition = FormStartPosition.CenterScreen;
        var heading = new Label { Text = "ZENTRIX  /  WINDOWS COMPANION", Dock = DockStyle.Top, Height = 58, Padding = new Padding(16), Font = new Font("Segoe UI", 15, FontStyle.Bold) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 6, 12, 4) };
        toolbar.Controls.AddRange([_add, _remove, _start, _stop]);
        var note = new Label { Dock = DockStyle.Bottom, Height = 74, Padding = new Padding(12), Text = "Only folders explicitly approved here are exposed. Keep Windows on a private network. HTTPS uses a local development certificate; the Xbox must independently trust the issuing certificate before pairing can be considered secure." };
        Controls.Add(_roots); Controls.Add(note); Controls.Add(_status); Controls.Add(toolbar); Controls.Add(heading);
        _add.Click += (_, _) => AddRoot();
        _remove.Click += (_, _) => RemoveRoot();
        _start.Click += async (_, _) => await StartServerAsync();
        _stop.Click += async (_, _) => await StopServerAsync();
        RefreshRoots();
        _status.Text = "Service stopped. No folders are shared until approved.";
        FormClosing += async (_, _) => await StopServerAsync();
    }

    private void AddRoot()
    {
        using var picker = new FolderBrowserDialog { Description = "Choose a folder ZENTRIX may access", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (picker.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(picker.SelectedPath)) return;
        try { _store.Add(picker.SelectedPath); RefreshRoots(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not approve folder", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void RemoveRoot()
    {
        if (_roots.SelectedItem is not ApprovedRoot root) return;
        _store.Remove(root.Id); RefreshRoots();
    }

    private void RefreshRoots()
    {
        _roots.Items.Clear();
        foreach (var root in _store.GetAll()) _roots.Items.Add(root);
    }

    private async Task StartServerAsync()
    {
        if (_server is not null) return;
        try
        {
            var cert = CertificateFactory.LoadOrCreate();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(47830); // Public certificate bootstrap only; all other HTTP paths are blocked below.
                options.ListenAnyIP(47831, listen => listen.UseHttps(cert));
            });
            builder.Services.AddSingleton(_store);
            builder.Services.AddSingleton(new AuthState(_pairCode));
            var app = builder.Build();
            app.Use(async (ctx, next) =>
            {
                if (!ctx.Request.IsHttps && ctx.Request.Path != "/bootstrap/certificate") { ctx.Response.StatusCode = 404; return; }
                if (ctx.Request.Path == "/api/v1/health" || ctx.Request.Path == "/api/v1/pair" || ctx.Request.Path == "/bootstrap/certificate")
                { await next(); return; }
                var auth = ctx.RequestServices.GetRequiredService<AuthState>();
                if (!auth.IsAuthorized(ctx.Request.Headers.Authorization.ToString()))
                { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }); return; }
                await next();
            });
            app.MapGet("/bootstrap/certificate", () => Results.File(File.ReadAllBytes(CertificateFactory.CertificatePath), "application/x-x509-ca-cert", "zentrix-companion.cer"));
            app.MapGet("/api/v1/health", () => Results.Ok(new { service = "ZENTRIX Companion", status = "ok", protocol = "https", version = 1 }));
            app.MapPost("/api/v1/pair", (PairRequest request, AuthState auth) =>
            {
                var token = auth.TryPair(request.Code);
                return token is null ? Results.Unauthorized() : Results.Ok(new { token });
            });
            app.MapGet("/api/v1/roots", (RootStore roots) => Results.Ok(roots.GetAll().Select(r => new { id = r.Id, name = r.Name })));
            app.MapGet("/api/v1/entries", (string rootId, string? path, RootStore roots) =>
            {
                if (!roots.TryResolve(rootId, path ?? "", out var folder)) return Results.NotFound(new { error = "not_found" });
                try
                {
                    var dirs = Directory.EnumerateDirectories(folder).Select(p => new EntryDto(Path.GetFileName(p), "directory", null, Path.GetRelativePath(roots.GetPath(rootId)!, p)));
                    var files = Directory.EnumerateFiles(folder).Select(p => new FileInfo(p)).Select(f => new EntryDto(f.Name, "file", f.Length, Path.GetRelativePath(roots.GetPath(rootId)!, f.FullName)));
                    return Results.Ok(dirs.Concat(files).OrderBy(x => x.Type == "directory" ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
                }
                catch (UnauthorizedAccessException) { return Results.Problem("Access denied", statusCode: 403); }
                catch (IOException) { return Results.Problem("The volume is unavailable", statusCode: 409); }
            });
            app.MapGet("/api/v1/content", async (string rootId, string path, HttpContext ctx, RootStore roots) =>
            {
                if (!roots.TryResolve(rootId, path, out var filePath) || !File.Exists(filePath)) return Results.NotFound(new { error = "not_found" });
                try
                {
                    var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    ctx.Response.ContentType = "application/octet-stream";
                    ctx.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName(filePath))}";
                    ctx.Response.ContentLength = stream.Length;
                    await using (stream) await stream.CopyToAsync(ctx.Response.Body, 1024 * 128, ctx.RequestAborted);
                    return Results.Empty;
                }
                catch (UnauthorizedAccessException) { return Results.Problem("Access denied", statusCode: 403); }
                catch (IOException) { return Results.Problem("The volume is unavailable", statusCode: 409); }
            });
            app.MapPost("/api/v1/directories", (PathRequest request, RootStore roots) =>
            {
                if (!roots.TryResolve(request.RootId, request.Path, out var target)) return Results.BadRequest(new { error = "invalid_path" });
                try { Directory.CreateDirectory(target); return Results.Ok(new { created = true }); }
                catch (UnauthorizedAccessException) { return Results.Problem("Access denied", statusCode: 403); }
                catch (IOException) { return Results.Problem("The volume is unavailable or full", statusCode: 409); }
            });
            app.MapPut("/api/v1/content", async (string rootId, string path, HttpContext ctx, RootStore roots) =>
            {
                if (!roots.TryResolve(rootId, path, out var target)) return Results.BadRequest(new { error = "invalid_path" });
                var parent = Path.GetDirectoryName(target);
                if (parent is null || !Directory.Exists(parent)) return Results.Conflict(new { error = "parent_not_found" });
                var temp = target + ".zentrix-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await ctx.Request.Body.CopyToAsync(output, 1024 * 128, ctx.RequestAborted);
                    if (File.Exists(target)) return Results.Conflict(new { error = "already_exists" });
                    File.Move(temp, target);
                    return Results.Ok(new { uploaded = true });
                }
                catch (UnauthorizedAccessException) { return Results.Problem("Access denied", statusCode: 403); }
                catch (IOException) { return Results.Problem("Upload failed; check free space and volume state", statusCode: 409); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            });
            app.MapPost("/api/v1/operations/copy", (TransferRequest request, RootStore roots) => CompanionOperations.Transfer(request, roots, false));
            app.MapPost("/api/v1/operations/move", (TransferRequest request, RootStore roots) => CompanionOperations.Transfer(request, roots, true));
            app.MapPost("/api/v1/operations/rename", (RenameRequest request, RootStore roots) =>
            {
                if (string.IsNullOrWhiteSpace(request.NewName) || request.NewName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || request.NewName is "." or "..")
                    return Results.BadRequest(new { error = "invalid_name" });
                if (!roots.TryResolve(request.RootId, request.Path, out var source)) return Results.BadRequest(new { error = "invalid_path" });
                var destination = Path.Combine(Path.GetDirectoryName(source)!, request.NewName);
                if (File.Exists(destination) || Directory.Exists(destination)) return Results.Conflict(new { error = "already_exists" });
                try
                {
                    if (Directory.Exists(source)) Directory.Move(source, destination);
                    else if (File.Exists(source)) File.Move(source, destination);
                    else return Results.NotFound(new { error = "not_found" });
                    return Results.Ok(new { renamed = true });
                }
                catch (UnauthorizedAccessException) { return Results.Problem("Access denied", statusCode: 403); }
                catch (IOException) { return Results.Conflict(new { error = "operation_failed" }); }
            });
            app.MapDelete("/api/v1/entries", (string rootId, string path, RootStore roots) =>
            {
                if (!roots.TryResolve(rootId, path, out var target) || target == roots.GetPath(rootId)) return Results.BadRequest(new { error = "invalid_path" });
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    else if (File.Exists(target)) File.Delete(target);
                    else return Results.NotFound(new { error = "not_found" });
                    return Results.Ok(new { deleted = true });
                }
                catch (UnauthorizedAccessException) { return Results.Problem("Access denied", statusCode: 403); }
                catch (IOException) { return Results.Conflict(new { error = "operation_failed" }); }
            });
            await app.StartAsync();
            _server = app;
            _start.Enabled = false; _stop.Enabled = true;
            var addresses = GetLocalAddresses();
            _status.Text = "HTTPS service active on port 47831. Pair code (one use): " + _pairCode + Environment.NewLine + "Host addresses: " + string.Join(", ", addresses);
            MessageBox.Show(this, "Pairing code: " + _pairCode + Environment.NewLine + Environment.NewLine + "Certificate: " + CertificateFactory.CertificatePath + Environment.NewLine + "Export this certificate and establish Xbox trust before attempting pairing. Do not bypass certificate validation.", "ZENTRIX pairing", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "Could not start service: " + ex.Message;
            MessageBox.Show(this, ex.ToString(), "Service startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopServerAsync()
    {
        if (_server is null) return;
        try { await _server.StopAsync(); await _server.DisposeAsync(); }
        finally { _server = null; _start.Enabled = true; _stop.Enabled = false; _status.Text = "Service stopped."; }
    }

    private static string[] GetLocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address.ToString()).Distinct().ToArray();
}

internal sealed class AuthState(string pairingCode)
{
    private readonly object _gate = new();
    private bool _used;
    private readonly DateTimeOffset _expires = DateTimeOffset.UtcNow.AddMinutes(5);
    private readonly string _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public string? TryPair(string supplied)
    {
        lock (_gate)
        {
            if (_used || DateTimeOffset.UtcNow > _expires || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pairingCode), Encoding.UTF8.GetBytes(supplied ?? ""))) return null;
            _used = true;
            return _token;
        }
    }
    public bool IsAuthorized(string header) => header.StartsWith("Bearer ", StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_token), Encoding.UTF8.GetBytes(header[7..]));
}

internal static class CompanionOperations
{
public static IResult Transfer(TransferRequest request, RootStore roots, bool move)
{
    if (!roots.TryResolve(request.SourceRootId, request.SourcePath, out var source) ||
        !roots.TryResolve(request.DestinationRootId, request.DestinationPath, out var destination))
        return Results.BadRequest(new { error = "invalid_path" });
    if (File.Exists(destination) || Directory.Exists(destination)) return Results.Conflict(new { error = "already_exists" });
    try
    {
        if (Directory.Exists(source))
        {
            if (move) Directory.Move(source, destination);
            else CopyDirectory(source, destination);
        }
        else if (File.Exists(source))
        {
            if (move) File.Move(source, destination);
            else File.Copy(source, destination, false);
        }
        else return Results.NotFound(new { error = "not_found" });
        return Results.Ok(new { completed = true });
    }
    catch (UnauthorizedAccessException) { return Results.Problem("Access denied", statusCode: 403); }
    catch (IOException) { return Results.Conflict(new { error = "operation_failed" }); }
}
internal static void CopyDirectory(string source, string destination)
{
    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("Copying reparse points is not permitted.");
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Copying reparse points is not permitted.");
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
    }
    foreach (var directory in Directory.EnumerateDirectories(source))
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Copying reparse points is not permitted.");
        CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
}
internal sealed record PairRequest(string Code);
internal sealed record PathRequest(string RootId, string Path);
internal sealed record RenameRequest(string RootId, string Path, string NewName);
internal sealed record TransferRequest(string SourceRootId, string SourcePath, string DestinationRootId, string DestinationPath);
internal sealed record EntryDto(string Name, string Type, long? Size, string Path);
internal sealed record ApprovedRoot(string Id, string Name, string Path)
{
    public override string ToString() => Name + "  —  " + Path;
}

internal sealed class RootStore
{
    private readonly string _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZENTRIX", "approved-roots.json");
    private readonly object _gate = new();
    public RootStore() { Directory.CreateDirectory(Path.GetDirectoryName(_file)!); }
    public IReadOnlyList<ApprovedRoot> GetAll()
    {
        lock (_gate) return Read();
    }
    public void Add(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        lock (_gate)
        {
            var items = Read();
            if (items.Any(x => string.Equals(x.Path, full, StringComparison.OrdinalIgnoreCase))) return;
            items.Add(new ApprovedRoot(Guid.NewGuid().ToString("N"), DirectoryInfoName(full), full));
            Write(items);
        }
    }
    public void Remove(string id) { lock (_gate) { var items = Read(); items.RemoveAll(x => x.Id == id); Write(items); } }
    public string? GetPath(string id) => GetAll().FirstOrDefault(x => x.Id == id)?.Path;
    public bool TryResolve(string id, string relative, out string resolved)
    {
        resolved = "";
        var root = GetPath(id);
        if (root is null || (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) || Path.IsPathRooted(relative) || relative.Contains((char)92) || relative.Split('/').Any(s => s == ".." || s == ".")) return false;
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!candidate.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        // Do not follow reparse points: reject any existing reparse-point segment.
        var current = Path.GetFullPath(root);
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
        }
        resolved = candidate;
        return true;
    }
    private List<ApprovedRoot> Read()
    {
        if (!File.Exists(_file)) return [];
        try { return JsonSerializer.Deserialize<List<ApprovedRoot>>(File.ReadAllText(_file)) ?? []; }
        catch { return []; }
    }
    private void Write(List<ApprovedRoot> items) => File.WriteAllText(_file, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
    private static string DirectoryInfoName(string p) => new DirectoryInfo(p).Name is { Length: > 0 } n ? n : p;
}

internal static class CertificateFactory
{
    public static string CertificatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZENTRIX", "companion-dev.cer");
    private static string PfxPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZENTRIX", "companion-dev.pfx");
    public static X509Certificate2 LoadOrCreate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PfxPath)!);
        if (File.Exists(PfxPath)) return new X509Certificate2(PfxPath, (string?)null, X509KeyStorageFlags.Exportable);
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest("CN=ZENTRIX-Companion-Development", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        foreach (var ip in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(PfxPath, cert.Export(X509ContentType.Pfx));
        File.WriteAllBytes(CertificatePath, cert.Export(X509ContentType.Cert));
        return new X509Certificate2(PfxPath, (string?)null, X509KeyStorageFlags.Exportable);
    }
}
