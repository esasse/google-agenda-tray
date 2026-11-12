using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgendaTray.Google;

namespace AgendaTray.Agenda;

/// <summary>Finds the video-call link of an event. Ported from the helper's find_join.</summary>
internal static partial class JoinLinks
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("meet", new Regex(@"https://meet\.google\.com/[A-Za-z0-9?=&_\-/.]+", RegexOptions.Compiled)),
        ("zoom", new Regex(@"https://[A-Za-z0-9.\-]*zoom\.us/(?:j|my|w|s)/[^\s<>""']+", RegexOptions.Compiled)),
        ("teams", new Regex(@"https://teams\.(?:microsoft|live)\.com/(?:l/meetup-join|meet)/[^\s<>""']+", RegexOptions.Compiled)),
        ("webex", new Regex(@"https://[A-Za-z0-9.\-]*webex\.com/[^\s<>""']+", RegexOptions.Compiled)),
        ("whereby", new Regex(@"https://whereby\.com/[^\s<>""']+", RegexOptions.Compiled)),
        ("jitsi", new Regex(@"https://meet\.jit\.si/[^\s<>""']+", RegexOptions.Compiled)),
        ("chime", new Regex(@"https://[A-Za-z0-9.\-]*chime\.aws/[^\s<>""']+", RegexOptions.Compiled)),
        ("slack", new Regex(@"https://app\.slack\.com/huddle/[^\s<>""']+", RegexOptions.Compiled)),
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
        var lowered = uri.ToLowerInvariant();
        if (lowered.Contains("meet.google.com")) return "meet";
        if (lowered.Contains("zoom.us")) return "zoom";
        if (lowered.Contains("teams.")) return "teams";
        if (lowered.Contains("webex.com")) return "webex";
        var solution = conf.Obj("conferenceSolution").Str("name").ToLowerInvariant();
        return solution.Length > 0 ? solution.Split(' ')[0] : "other";
    }

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
