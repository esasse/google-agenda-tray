using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgendaTray.Google;

/// <summary>The OAuth client of the user's own Google Cloud project.</summary>
internal sealed record OAuthClient(string ClientId, string ClientSecret)
{
    public static OAuthClient? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ClientFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ClientFile));
            var id = doc.RootElement.Str("client_id");
            var secret = doc.RootElement.Str("client_secret");
            return id.Length > 0 && secret.Length > 0 ? new OAuthClient(id, secret) : null;
        }
        catch (Exception ex)
        {
            Log.Write($"could not read client.json: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads either the client_secret_*.json the Cloud Console downloads
    /// ({"installed": {...}}) or omarchy-agenda's own client.json (flat).
    /// </summary>
    public static OAuthClient FromFile(string path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not read the JSON: {ex.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            var node = root.Obj("installed").ValueKind == JsonValueKind.Object ? root.Obj("installed")
                     : root.Obj("web").ValueKind == JsonValueKind.Object ? root.Obj("web")
                     : root;
            var id = node.Str("client_id");
            var secret = node.Str("client_secret");
            if (id.Length == 0 || secret.Length == 0)
                throw new InvalidDataException("The JSON has no client_id/client_secret.");
            if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
                Log.Write("warning: a Client ID normally ends in .apps.googleusercontent.com");
            return new OAuthClient(id, secret);
        }
    }

    public void Save()
    {
        AppPaths.Ensure();
        var json = new JsonObject { ["client_id"] = ClientId, ["client_secret"] = ClientSecret };
        Files.WriteAtomic(AppPaths.ClientFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The most recent client_secret*.json in Downloads or on the Desktop.</summary>
    public static string? FindDownloaded()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        return folders
            .Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "client_secret*.json"))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }
}

internal sealed record Account(string Email, string RefreshToken, string AddedAt);

/// <summary>
/// One file per account under accounts\. The refresh token is encrypted with DPAPI
/// for the current Windows user, which is what the original's 0600 permission bits
/// were for. A file copied from omarchy-agenda carries the token in plain text; it
/// is read as is and re-saved encrypted.
/// </summary>
internal static partial class AccountStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("google-agenda-tray/refresh-token");

    public static List<Account> List()
    {
        var accounts = new List<Account>();
        if (!Directory.Exists(AppPaths.AccountsDir)) return accounts;
        foreach (var path in Directory.EnumerateFiles(AppPaths.AccountsDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var email = root.Str("email");
                if (email.Length == 0) continue;
                if (accounts.Any(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase))) continue;
                var added = root.Str("added_at");

                var sealedToken = root.Str("refresh_token_protected");
                if (sealedToken.Length > 0)
                {
                    var token = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                        Convert.FromBase64String(sealedToken), Entropy, DataProtectionScope.CurrentUser));
                    accounts.Add(new Account(email, token, added));
                    continue;
                }

                var plain = root.Str("refresh_token");
                if (plain.Length > 0)
                {
                    var account = new Account(email, plain, added);
                    Save(account);
                    // A copied file named differently from ours would keep the token in
                    // plain text and list the account twice.
                    if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(PathFor(email)), StringComparison.OrdinalIgnoreCase))
                        File.Delete(path);
                    Log.Write($"imported {email} and encrypted its refresh token");
                    accounts.Add(account);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"skipping unreadable account file {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return accounts;
    }

    public static Account? Find(string email) =>
        List().FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));

    public static void Save(Account account)
    {
        AppPaths.Ensure();
        var sealedToken = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(account.RefreshToken), Entropy, DataProtectionScope.CurrentUser));
        var json = new JsonObject
        {
            ["email"] = account.Email,
            ["refresh_token_protected"] = sealedToken,
            ["scopes"] = GoogleApi.Scopes,
            ["added_at"] = string.IsNullOrEmpty(account.AddedAt)
                ? DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")
                : account.AddedAt,
        };
        Files.WriteAtomic(PathFor(account.Email), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Remove(string email)
    {
        var path = PathFor(email);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string email) =>
        Path.Combine(AppPaths.AccountsDir, UnsafeChars().Replace(email, "_") + ".json");

    [GeneratedRegex(@"[^A-Za-z0-9@._+-]")]
    private static partial Regex UnsafeChars();
}

internal static class Files
{
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
