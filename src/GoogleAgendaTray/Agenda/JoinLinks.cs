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
        // The hosts here are the ones KnownKind below accepts in the conference data
        // (bar classic Hangouts, which only Google's own fields carry), so a link is
        // judged alike in both places.
        ("zoom", new Regex(@"https://(?:[A-Za-z0-9-]+\.)*zoom(?:\.us|gov\.com)/(?:j|my|w|s)/[^\s<>""']+", Opts)),
        ("teams", new Regex(@"https://(?:[A-Za-z0-9-]+\.)*teams\.(?:microsoft\.com|live\.com|cloud\.microsoft|microsoft\.us)/(?:l/meetup-join|meet)/[^\s<>""']+", Opts)),
        // Only Webex's join links: personal rooms (/meet/, /join/), meetings, events and
        // Event Center (<site>/j.php, e.php, onstage/g.php) and the web app. An invite
        // also carries call-in numbers, setup and system-check pages on the same host,
        // and none of them is a meeting.
        ("webex", new Regex(@"https://(?:[A-Za-z0-9-]+\.)*webex\.com/(?:meet/|join/|(?:[A-Za-z0-9_\-]+/)*[jeg]\.php\?|wbxmjs/joinservice/|webappng/sites/)[^\s<>""']+", Opts)),
        ("whereby", new Regex(@"https://whereby\.com/[^\s<>""']+", Opts)),
        ("jitsi", new Regex(@"https://meet\.jit\.si/[^\s<>""']+", Opts)),
        ("chime", new Regex(@"https://(?:[A-Za-z0-9-]+\.)*chime\.aws/[^\s<>""']+", Opts)),
        ("slack", new Regex(@"https://app\.slack\.com/huddle/[^\s<>""']+", Opts)),
    ];

    public static (string Url, string Kind) Find(JsonElement ev)
    {
        // The conference data is trusted no more than the description: a video link
        // gets the join button only on the host of a provider the app knows, over
        // https. Anything else (a provider not listed, or a planted link) falls through
        // to the description, and the event still opens in Google Calendar.
        foreach (var entry in ev.Obj("conferenceData").Arr("entryPoints"))
        {
            if (entry.Str("entryPointType") == "video" && Known(entry.Str("uri")) is { Kind.Length: > 0 } found) return found;
        }
        if (Known(ev.Str("hangoutLink")) is { Kind: "meet" } hangout) return hangout;

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

    /// <summary>
    /// The provider of an https link, judged by its parsed host (the domain or a
    /// whole-label subdomain of it, so neither evilzoom.us nor ?next=zoom.us passes for
    /// Zoom), with the URL as parsed; ("", "") for anything else. The link handed back
    /// is the parsed one, escaped, never the raw string: a quote or a space that the
    /// parser accepted must not reach the browser's command line as written.
    /// </summary>
    private static (string Url, string Kind) Known(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return ("", "");
        // Uri keeps a Unicode host as it is: evil.com／.zoom.us (a fullwidth slash) ends
        // in .zoom.us here, while a browser that maps it to "/" opens evil.com. The
        // providers' hosts are all plain ASCII, so nothing else needs to pass.
        if (!uri.Host.All(char.IsAscii)) return ("", "");
        var kind = KnownKind(uri.Host);
        return kind.Length > 0 ? (uri.AbsoluteUri, kind) : ("", "");
    }

    private static string KnownKind(string host)
    {
        // Classic Hangouts links still sit on old recurring events.
        if (IsHost(host, "meet.google.com") || IsHost(host, "hangouts.google.com")) return "meet";
        // Zoom for Government lives on its own domain.
        if (IsHost(host, "zoom.us") || IsHost(host, "zoomgov.com")) return "zoom";
        // Commercial, personal, the cloud.microsoft domain and US government clouds
        // (gov.teams.microsoft.us).
        if (IsHost(host, "teams.microsoft.com") || IsHost(host, "teams.live.com")
            || IsHost(host, "teams.cloud.microsoft") || IsHost(host, "teams.microsoft.us")) return "teams";
        if (IsHost(host, "webex.com")) return "webex";
        if (IsHost(host, "whereby.com")) return "whereby";
        if (IsHost(host, "meet.jit.si")) return "jitsi";
        if (IsHost(host, "chime.aws")) return "chime";
        if (IsHost(host, "app.slack.com")) return "slack";
        return "";
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
