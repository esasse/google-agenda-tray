using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgendaTray.Google;

internal sealed class ApiException(int status, string detail) : Exception
{
    public int Status { get; } = status;
    public string Detail { get; } = detail;

    /// <summary>The refresh token was revoked or expired: only a new login fixes it.</summary>
    public bool InvalidGrant => Detail.Contains("invalid_grant", StringComparison.Ordinal);

    public override string Message
    {
        get
        {
            var text = "";
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(Detail) ? "{}" : Detail);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.Object)
                        text = err.Str("message") is { Length: > 0 } m ? m : err.Str("status");
                    else if (err.ValueKind == JsonValueKind.String)
                        text = root.Str("error_description") is { Length: > 0 } d ? d : err.GetString() ?? "";
                }
            }
            catch (JsonException)
            {
            }
            if (string.IsNullOrEmpty(text))
            {
                var raw = Log.OneLine(Detail);
                text = raw.Length > 200 ? raw[..200] : raw;
            }
            if (string.IsNullOrEmpty(text)) text = "network failure";
            return Status > 0 ? $"HTTP {Status}: {text}" : text;
        }
    }
}

internal static class GoogleApi
{
    public const string AuthUri = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenUri = "https://oauth2.googleapis.com/token";
    public const string RevokeUri = "https://oauth2.googleapis.com/revoke";
    public const string UserInfoUri = "https://openidconnect.googleapis.com/v1/userinfo";
    public const string CalendarApi = "https://www.googleapis.com/calendar/v3";
    public const string Scopes = "openid email https://www.googleapis.com/auth/calendar.readonly";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("google-agenda-tray/1.0");
        return http;
    }

    public static Task<JsonElement> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        return SendAsync(request, ct);
    }

    public static Task<JsonElement> GetAsync(string url, string? bearer, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return SendAsync(request, ct);
    }

    private static async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new ApiException((int)response.StatusCode, body);
                if (string.IsNullOrWhiteSpace(body)) body = "{}";
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(0, ex.InnerException?.Message ?? ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(0, "request timed out");
        }
        catch (JsonException ex)
        {
            throw new ApiException(0, $"invalid response: {ex.Message}");
        }
    }

    public static string Query(params (string Key, string Value)[] pairs) =>
        string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    // ------------------------------------------------------------------ tokens

    // The app stays up for days, so the access tokens live in memory only; the
    // original cached them on disk because its helper was a fresh process per poll.
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> AccessTokens =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> AccessTokenAsync(Account account, OAuthClient client, CancellationToken ct)
    {
        if (AccessTokens.TryGetValue(account.Email, out var cached) && cached.Expires - TimeSpan.FromSeconds(60) > DateTimeOffset.Now)
            return cached.Token;

        var payload = await PostFormAsync(TokenUri, new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["refresh_token"] = account.RefreshToken,
            ["grant_type"] = "refresh_token",
        }, ct).ConfigureAwait(false);

        var token = payload.Str("access_token");
        if (string.IsNullOrEmpty(token)) throw new ApiException(0, "response without access_token");
        var lifetime = payload.TryGetProperty("expires_in", out var e) && e.TryGetDouble(out var secs) ? secs : 3600;
        AccessTokens[account.Email] = (token, DateTimeOffset.Now.AddSeconds(lifetime));
        return token;
    }

    public static void ForgetAccessToken(string email) => AccessTokens.TryRemove(email, out _);
}

internal static class JsonExtensions
{
    public static string Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : default;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];
}
