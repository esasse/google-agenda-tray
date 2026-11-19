using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgendaTray.Google;

namespace AgendaTray.Agenda;

/// <summary>Finds the video-call link of an event. Ported from the helper's find_join.</summary>
internal static partial class JoinLinks
{
    // Subdomains are allowed (us02web.zoom.us, company.webex.com) but only as whole
    // labels ending in a dot: a host that merely ends in the name, like evilzoom.us,
    // must not pass for the real thing and get a join button. Hosts are case-insensitive,
    // so HTTPS://US02WEB.ZOOM.US/j/... is the same link and must match too.
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("meet", new Regex(@"https://meet\.google\.com/[A-Za-z0-9?=&_\-/.]+", Opts)),
        ("zoom", new Regex(@"https://(?:[A-Za-z0-9-]+\.)*zoom\.us/(?:j|my|w|s)/[^\s<>""']+", Opts)),
        ("teams", new Regex(@"https://teams\.(?:microsoft|live)\.com/(?:l/meetup-join|meet)/[^\s<>""']+", Opts)),
        ("webex", new Regex(@"https://(?:[A-Za-z0-9-]+\.)*webex\.com/[^\s<>""']+", Opts)),
        ("whereby", new Regex(@"https://whereby\.com/[^\s<>""']+", Opts)),
        ("jitsi", new Regex(@"https://meet\.jit\.si/[^\s<>""']+", Opts)),
        ("chime", new Regex(@"https://(?:[A-Za-z0-9-]+\.)*chime\.aws/[^\s<>""']+", Opts)),
        ("slack", new Regex(@"https://app\.slack\.com/huddle/[^\s<>""']+", Opts)),
    ];

    public static (string Url, string Kind) Find(JsonElement ev)
    {
        var conf = ev.Obj("conferenceData");
        foreach (var entry in conf.Arr("entryPoints"))
        {
            var uri = entry.Str("uri");
            if (entry.Str("entryPointType") == "video" && uri.Length > 0)
                return (uri, ConferenceKind(uri, conf));
        }
        var hangout = ev.Str("hangoutLink");
        if (hangout.Length > 0) return (hangout, "meet");

        // A Zoom/Teams invite often lives only inside <a href="...">, so the raw
        // HTML joins the haystack alongside the stripped text.
        var raw = ev.Str("description");
        var haystack = string.Join("\n", ev.Str("location"), WebUtility.HtmlDecode(raw), PlainText(raw));
        foreach (var (kind, pattern) in Patterns)
        {
            var m = pattern.Match(haystack);
            if (m.Success) return (m.Value.TrimEnd(')', '.', ',', ';', '>', '"', '\''), kind);
        }
        return ("", "");
    }

    private static string ConferenceKind(string uri, JsonElement conf)
    {
        // Same rule as the patterns above: judge the host, not a substring of the whole
        // URL, or evilzoom.us (or ?next=zoom.us) would be labelled as Zoom.
        var host = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : "";
        if (IsHost(host, "meet.google.com")) return "meet";
        if (IsHost(host, "zoom.us")) return "zoom";
        if (host.StartsWith("teams.", StringComparison.OrdinalIgnoreCase) || host.Contains(".teams.", StringComparison.OrdinalIgnoreCase))
        {
            if (IsHost(host, "microsoft.com") || IsHost(host, "live.com") || IsHost(host, "microsoft.us")) return "teams";
        }
        if (IsHost(host, "webex.com")) return "webex";
        if (IsHost(host, "chime.aws")) return "chime";
        var solution = conf.Obj("conferenceSolution").Str("name").ToLowerInvariant();
        return solution.Length > 0 ? solution.Split(' ')[0] : "other";
    }

    /// <summary>True when <paramref name="host"/> is <paramref name="domain"/> or a whole-label subdomain of it.</summary>
    private static bool IsHost(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static string PlainText(string html)
    {
        var text = html.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
        return WebUtility.HtmlDecode(Tags().Replace(text, " "));
    }

    /// <summary>Pins the right account on Google links, so a browser signed in to
    /// several accounts does not open the meeting under the wrong one.</summary>
    public static string WithAuthUser(string url, string email)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!uri.Host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase)) return url;
        var query = uri.Query.TrimStart('?');
        if (query.Split('&').Any(p => p.Split('=')[0] == "authuser")) return url;
        var builder = new UriBuilder(uri)
        {
            Query = (query.Length > 0 ? query + "&" : "") + "authuser=" + Uri.EscapeDataString(email),
        };
        return builder.Uri.AbsoluteUri;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
