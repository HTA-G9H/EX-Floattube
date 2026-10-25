using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace FloatTube;

public sealed record DetectedBrowser(string Name, string ExePath);

public sealed record BrowserSignInResult(string UserAgent, List<CdpCookie> Cookies);

public sealed class CdpCookie
{
    public string Name = "";
    public string Value = "";
    public string Domain = "";
    public string Path = "/";
    public double Expires;
    public bool Secure;
    public bool HttpOnly;
    public string SameSite = "";
}

/// <summary>
/// C# port of browser-login.js — launches the user's real Chrome / Edge /
/// Brave with a dedicated login profile + remote debugging, waits while the
/// user signs in to Google in that window (so Google never blocks it the way
/// it can block embedded webviews), then hands the session cookies back so
/// they can be recreated inside the WebView2 profile.
/// </summary>
public static class BrowserLogin
{
    private static readonly HttpClient Http = new();

    public static IReadOnlyList<DetectedBrowser> FindBrowsers()
    {
        string pf   = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        string la   = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";

        var candidates = new (string Name, string Path)[]
        {
            ("Chrome", pf   + "\\Google\\Chrome\\Application\\chrome.exe"),
            ("Chrome", pf86 + "\\Google\\Chrome\\Application\\chrome.exe"),
            ("Chrome", la   + "\\Google\\Chrome\\Application\\chrome.exe"),
            ("Edge",   pf86 + "\\Microsoft\\Edge\\Application\\msedge.exe"),
            ("Edge",   pf   + "\\Microsoft\\Edge\\Application\\msedge.exe"),
            ("Brave",  pf   + "\\BraveSoftware\\Brave-Browser\\Application\\brave.exe"),
            ("Brave",  pf86 + "\\BraveSoftware\\Brave-Browser\\Application\\brave.exe"),
            ("Brave",  la   + "\\BraveSoftware\\Brave-Browser\\Application\\brave.exe"),
        };

        var seen = new HashSet<string>();
        var found = new List<DetectedBrowser>();
        foreach (var (name, path) in candidates)
        {
            if (path.Length > 5 && File.Exists(path) && seen.Add(name))
                found.Add(new DetectedBrowser(name, path));
        }
        return found;
    }

    public static async Task<BrowserSignInResult> SignInAsync(
        string exePath, string profileDir,
        Action<string>? status = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(profileDir);
        int port = 9333 + Random.Shared.Next(500);
        string signIn =
            "https://accounts.google.com/ServiceLogin?service=youtube&continue=" +
            Uri.EscapeDataString("https://www.youtube.com/");

        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
        psi.ArgumentList.Add($"--remote-debugging-port={port}");
        psi.ArgumentList.Add($"--user-data-dir={profileDir}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--disable-session-crashed-bubble");
        psi.ArgumentList.Add("--hide-crash-restore-bubble");
        psi.ArgumentList.Add(signIn);
        var child = Process.Start(psi);

        // Wait for the browser's DevTools endpoint to come up.
        string? wsUrl = null;
        for (int i = 0; i < 40 && wsUrl == null && !ct.IsCancellationRequested; i++)
        {
            if (child is { HasExited: true }) break;
            try
            {
                string body = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var el))
                    wsUrl = el.GetString();
            }
            catch { await Task.Delay(500, ct); }
        }
        if (wsUrl == null)
        {
            TryKill(child);
            throw new InvalidOperationException(
                "Could not connect to the browser's debugging port. " +
                "Fully close that browser (all its windows) and try again.");
        }

        using var cdp = new CdpClient();
        await cdp.ConnectAsync(wsUrl, ct);

        string ua = "";
        try
        {
            var ver = await cdp.SendAsync("Browser.getVersion");
            if (ver.TryGetProperty("result", out var vr) &&
                vr.TryGetProperty("userAgent", out var uaEl))
                ua = (uaEl.GetString() ?? "").Replace("HeadlessChrome", "Chrome");
        }
        catch { /* UA is a nice-to-have */ }

        // Wait until the Google session cookies exist in the login profile.
        List<CdpCookie>? cookies = null;
        long sawGoogleAt = 0;
        var deadline = DateTime.UtcNow.AddMinutes(10);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && child is { HasExited: false })
        {
            var all = await GetCookiesAsync(cdp);
            bool google = all.Any(c => c.Name == "SAPISID"   && c.Domain.EndsWith("google.com",  StringComparison.OrdinalIgnoreCase));
            bool yt     = all.Any(c => c.Name == "LOGIN_INFO" && c.Domain.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase));
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (google && sawGoogleAt == 0) sawGoogleAt = now;

            if (google && (yt || now - sawGoogleAt > 30_000))
            {
                await Task.Delay(2_500, ct);
                var again = await GetCookiesAsync(cdp);
                cookies = again.Count > 0 ? again : all;
                break;
            }

            status?.Invoke("Sign in: finish in the browser window — waiting…");
            await Task.Delay(1_500, ct);
        }

        try { await cdp.SendAsync("Browser.close"); } catch { }
        _ = Task.Run(async () => { await Task.Delay(3_000); TryKill(child); });

        if (cookies == null)
            throw new InvalidOperationException(
                "Sign-in was not completed (the browser was closed or it timed out).");

        return new BrowserSignInResult(ua, cookies);
    }

    private static async Task<List<CdpCookie>> GetCookiesAsync(CdpClient cdp)
    {
        var list = new List<CdpCookie>();
        try
        {
            var root = await cdp.SendAsync("Storage.getCookies");
            if (!root.TryGetProperty("result", out var res)) return list;
            if (!res.TryGetProperty("cookies", out var arr))  return list;

            foreach (var el in arr.EnumerateArray())
            {
                var c = new CdpCookie();
                if (el.TryGetProperty("name",     out var n))  c.Name   = n.GetString() ?? "";
                if (el.TryGetProperty("value",    out var v))  c.Value  = v.GetString() ?? "";
                if (el.TryGetProperty("domain",   out var d))  c.Domain = d.GetString() ?? "";
                if (el.TryGetProperty("path",     out var p))  c.Path   = p.GetString() ?? "/";
                if (el.TryGetProperty("expires",  out var ex) && ex.ValueKind == JsonValueKind.Number) c.Expires = ex.GetDouble();
                if (el.TryGetProperty("secure",   out var se) && se.ValueKind == JsonValueKind.True)   c.Secure   = true;
                if (el.TryGetProperty("httpOnly", out var ho) && ho.ValueKind == JsonValueKind.True)   c.HttpOnly = true;
                if (el.TryGetProperty("sameSite", out var ss) && ss.ValueKind == JsonValueKind.String) c.SameSite = ss.GetString() ?? "";
                if (c.Name.Length > 0) list.Add(c);
            }
        }
        catch { /* transient failures are fine — we poll */ }
        return list;
    }

    private static void TryKill(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(true); } catch { }
    }

    /// <summary>Minimal Chrome DevTools Protocol client over WebSocket.</summary>
    private sealed class CdpClient : IDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _nextId;

        public async Task ConnectAsync(string url, CancellationToken ct)
        {
            _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await _ws.ConnectAsync(new Uri(url), ct);
            _ = Task.Run(ReceiveLoopAsync);
        }

        public async Task<JsonElement> SendAsync(string method)
        {
            int id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method });
            await _sendLock.WaitAsync();
            try   { await _ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _sendLock.Release(); }

            return await tcs.Task;
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    var r = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;

                    var frame = ms.ToArray();
                    ms.SetLength(0);
                    try
                    {
                        using var doc = JsonDocument.Parse(frame);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("id", out var idEl) &&
                            idEl.TryGetInt32(out int id) &&
                            _pending.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(root.Clone());
                        }
                    }
                    catch { /* ignore malformed frames */ }
                }
            }
            catch { /* socket closed */ }

            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            _pending.Clear();
        }

        public void Dispose() => _ws.Dispose();
    }
}
