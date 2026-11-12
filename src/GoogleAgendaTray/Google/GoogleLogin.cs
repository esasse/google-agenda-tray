using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgendaTray.Google;

internal sealed class LoginException(string message) : Exception(message);

/// <summary>
/// The installed-app OAuth flow: open the browser on Google's consent page and wait
/// for the redirect on a loopback port. A raw TcpListener rather than HttpListener,
/// because HttpListener goes through http.sys and wants a URL reservation (admin)
/// for anything but "localhost" - and Google wants 127.0.0.1.
/// PKCE on top of what the original did; Google recommends it for desktop clients.
/// </summary>
internal static class GoogleLogin
{
    public static async Task<string> RunAsync(OAuthClient client, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/";
            var state = Base64Url(RandomNumberGenerator.GetBytes(24));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            var authUrl = GoogleApi.AuthUri + "?" + GoogleApi.Query(
                ("client_id", client.ClientId),
                ("redirect_uri", redirectUri),
                ("response_type", "code"),
                ("scope", GoogleApi.Scopes),
                ("access_type", "offline"),
                ("prompt", "consent select_account"),
                ("state", state),
                ("code_challenge", challenge),
                ("code_challenge_method", "S256"));

            Log.Write($"login: waiting on {redirectUri}");
            Shell.Open(authUrl);

            string? code = null;
            while (code == null)
            {
                using var tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                await using var stream = tcp.GetStream();

                // Connections are served one at a time, so one that never sends a request
                // (a browser's speculative preconnect, say) must not hold up the redirect
                // arriving on the next one.
                string path;
                Dictionary<string, string> query;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        (path, query) = await ReadRequestAsync(stream, readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        continue;
                    }
                }

                if (path != "/")
                {
                    // the browser asks for /favicon.ico too
                    await RespondAsync(stream, 404, "", ct).ConfigureAwait(false);
                    continue;
                }
                if (query.TryGetValue("error", out var error))
                {
                    await RespondAsync(stream, 200, $"Authorization denied: {WebUtility.HtmlEncode(error)}", ct).ConfigureAwait(false);
                    throw new LoginException($"authorization denied ({error})");
                }
                if (!query.TryGetValue("state", out var gotState) || gotState != state ||
                    !query.TryGetValue("code", out var gotCode) || gotCode.Length == 0)
                {
                    // Anything else knocking on the port is not our redirect; keep waiting for it.
                    await RespondAsync(stream, 400, "Invalid request.", ct).ConfigureAwait(false);
                    continue;
                }
                code = gotCode;
                await RespondAsync(stream, 200, "Account authorized. You can close this tab.", ct).ConfigureAwait(false);
            }

            var payload = await GoogleApi.PostFormAsync(GoogleApi.TokenUri, new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            }, ct).ConfigureAwait(false);

            var email = EmailFromIdToken(payload.Str("id_token"));
            if (email.Length == 0) email = await UserInfoEmailAsync(payload.Str("access_token"), ct).ConfigureAwait(false);
            if (email.Length == 0) throw new LoginException("could not determine the e-mail of the authorized account");

            var refresh = payload.Str("refresh_token");
            var existing = AccountStore.Find(email);
            if (refresh.Length == 0)
            {
                refresh = existing?.RefreshToken ?? "";
                if (refresh.Length == 0)
                    throw new LoginException("Google returned no refresh token. Remove the app's access at " +
                                             "myaccount.google.com/permissions and connect again.");
            }

            AccountStore.Save(new Account(email, refresh, existing?.AddedAt ?? ""));
            GoogleApi.ForgetAccessToken(email);
            Log.Write($"login: authorized {email}");
            return email;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<(string Path, Dictionary<string, string> Query)> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, total);
        var firstLine = head.Split("\r\n", 2)[0];               // GET /?code=...&state=... HTTP/1.1
        var parts = firstLine.Split(' ');
        var target = parts.Length >= 2 ? parts[1] : "/";
        var q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq >= 0 ? pair[..eq] : pair).Replace('+', ' '));
                var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                query.TryAdd(key, value);
            }
        }
        return (path, query);
    }

    private static async Task RespondAsync(Stream stream, int status, string message, CancellationToken ct)
    {
        var body = status == 404 ? "" :
            "<!doctype html><meta charset=utf-8><title>Google Agenda Tray</title>" +
            "<body style=\"font:16px 'Segoe UI',system-ui;padding:3rem;background:#1b1d22;color:#e8eaed\">" +
            $"<p>{message}</p></body>";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                   $"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string EmailFromIdToken(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) return "";
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.Str("email");
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> UserInfoEmailAsync(string accessToken, CancellationToken ct)
    {
        if (accessToken.Length == 0) return "";
        try
        {
            var info = await GoogleApi.GetAsync(GoogleApi.UserInfoUri, accessToken, ct).ConfigureAwait(false);
            return info.Str("email");
        }
        catch (ApiException)
        {
            return "";
        }
    }

    /// <summary>Removes the account here and, unless told otherwise, revokes it at Google.</summary>
    public static async Task LogoutAsync(Account account, bool revoke, CancellationToken ct)
    {
        if (revoke)
        {
            try
            {
                await GoogleApi.PostFormAsync(GoogleApi.RevokeUri,
                    new Dictionary<string, string> { ["token"] = account.RefreshToken }, ct).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                Log.Write($"logout: revoking {account.Email} at Google failed ({ex.Message})");
            }
        }
        AccountStore.Remove(account.Email);
        GoogleApi.ForgetAccessToken(account.Email);
        Log.Write($"logout: removed {account.Email}");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
