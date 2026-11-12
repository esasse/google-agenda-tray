using System.Globalization;

namespace AgendaTray.Agenda;

internal enum RowState { Past, Live, Soon, Future }

/// <summary>
/// Formatting and derivations: a port of the plugin's Model.js. Stateless - it takes
/// the day and the current time and returns what the UI has to draw.
/// </summary>
internal static class Fmt
{
    public const double SoonSec = 10 * 60;   // "starting soon"
    public const double GraceSec = 5 * 60;   // a meeting is still joinable after it ends

    public static string IsoDate(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Hm(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string TimeLabel(AgendaEvent e) => e.AllDay ? "all day" : Hm(e.Start);

    public static string ShortTitle(string title, int max)
    {
        var t = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (max <= 0 || t.Length <= max) return t;
        return t[..Math.Max(1, max - 1)] + "…";
    }

    public static string Duration(double seconds)
    {
        var mins = (int)Math.Max(0, Math.Round(seconds / 60, MidpointRounding.AwayFromZero));
        if (mins < 60) return mins + " min";
        var hours = mins / 60;
        var rest = mins % 60;
        return rest == 0 ? hours + "h" : hours + "h" + rest.ToString("00", CultureInfo.InvariantCulture);
    }

    private static double Secs(DateTimeOffset a, DateTimeOffset b) => (a - b).TotalSeconds;

    /// <summary>"in 25 min" / "now" / "10 min ago", always relative to the start.</summary>
    public static string Relative(AgendaEvent e, DateTimeOffset now)
    {
        if (e.AllDay) return "today";
        var delta = Secs(e.Start, now);
        if (delta > 30) return "in " + Duration(delta);
        if (e.End > now) return "now";
        return Duration(Secs(now, e.Start)) + " ago";
    }

    public static RowState State(AgendaEvent e, DateTimeOffset now)
    {
        if (e.AllDay) return RowState.Future;
        if (now >= e.Start && now < e.End) return RowState.Live;
        if (now >= e.End) return RowState.Past;
        if (Secs(e.Start, now) <= SoonSec) return RowState.Soon;
        return RowState.Future;
    }

    public static bool IsJoinable(AgendaEvent e, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(e.JoinUrl)) return false;
        if (e.AllDay) return true;
        return Secs(now, e.End) < GraceSec;
    }

    public static bool Declined(AgendaEvent e) => e.MyStatus == "declined";

    /// <summary>The meeting happening right now (the one ending first, when they overlap).</summary>
    public static AgendaEvent? LiveEvent(IReadOnlyList<AgendaEvent> events, DateTimeOffset now)
    {
        AgendaEvent? best = null;
        foreach (var e in events)
        {
            if (e.AllDay || Declined(e)) continue;
            if (now >= e.Start && now < e.End && (best == null || e.End < best.End)) best = e;
        }
        return best;
    }

    public static AgendaEvent? NextEvent(IReadOnlyList<AgendaEvent> events, DateTimeOffset now) =>
        events.FirstOrDefault(e => !e.AllDay && !Declined(e) && e.Start > now);

    /// <summary>What a middle click (and J) should open: the meeting in progress wins
    /// over the next one.</summary>
    public static AgendaEvent? FocusEvent(IReadOnlyList<AgendaEvent> events, DateTimeOffset now) =>
        LiveEvent(events, now) ?? NextEvent(events, now);

    /// <summary>Timed, not declined, not over yet. A meeting you are invited to on two
    /// accounts is one row per account in the list, but one meeting here.</summary>
    public static int Remaining(IReadOnlyList<AgendaEvent> events, DateTimeOffset now) =>
        events.Where(e => !e.AllDay && !Declined(e) && e.End > now)
            .Select(e => (e.Title, e.Start))
            .Distinct()
            .Count();

    public static string HeroMeta(AgendaResult? result, DateTimeOffset now)
    {
        if (result == null) return "loading…";
        if (result.NeedsSetup) return "setup pending";
        if (result.NeedsLogin) return "no account connected";
        var events = result.Events;
        if (events.Count == 0) return "nothing scheduled";
        var meetings = events.Count(e => !string.IsNullOrEmpty(e.JoinUrl));
        var parts = new List<string> { events.Count + (events.Count == 1 ? " event" : " events") };
        if (meetings > 0) parts.Add(meetings + (meetings == 1 ? " meeting" : " meetings"));
        var live = LiveEvent(events, now);
        var next = NextEvent(events, now);
        if (live != null) parts.Add("now until " + Hm(live.End));
        else if (next != null) parts.Add("next " + Relative(next, now));
        else if (Remaining(events, now) == 0) parts.Add("all done");
        return string.Join(" · ", parts);
    }

    public static string DateLabel(string isoDate) =>
        DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dddd, MMMM d", CultureInfo.InvariantCulture)
            : "";

    public static string JoinName(string kind) => kind switch
    {
        "meet" => "Google Meet",
        "zoom" => "Zoom",
        "teams" => "Teams",
        "webex" => "Webex",
        "whereby" => "Whereby",
        "jitsi" => "Jitsi",
        "chime" => "Chime",
        "slack" => "Slack",
        "" => "",
        _ => "video call",
    };

    public static string StatusMark(AgendaEvent e) => e.MyStatus switch
    {
        "declined" => "declined",
        "tentative" => "tentative",
        "needsAction" => "no reply",
        _ => "",
    };

    /// <summary>A row's second line: account, calendar when it differs, room or call,
    /// RSVP status and guest count.</summary>
    public static string MetaLine(AgendaEvent e)
    {
        var bits = new List<string> { string.IsNullOrEmpty(e.AccountLabel) ? e.Account : e.AccountLabel };
        if (!string.IsNullOrEmpty(e.Calendar) && e.Calendar != e.Account && !e.Primary) bits.Add(e.Calendar);
        var join = JoinName(e.JoinKind);
        if (join.Length > 0) bits.Add(join);
        else if (!string.IsNullOrEmpty(e.Location)) bits.Add(ShortTitle(e.Location, 28));
        var mark = StatusMark(e);
        if (mark.Length > 0) bits.Add(mark);
        if (e.AttendeeCount > 1) bits.Add(e.AttendeeCount + " guests");
        return string.Join(" · ", bits);
    }

    /// <summary>The tray tooltip. Shell_NotifyIcon allows 127 characters, so each
    /// title is shortened to keep both lines and the stale note in: at their longest
    /// the fixed parts take 69 (22 + 24 + 21 + two line breaks), which leaves 58 for
    /// the two titles. The "N events left today" line on top only goes in when it
    /// still fits.</summary>
    public static string Tooltip(AgendaResult? result, DateTimeOffset now)
    {
        if (result == null) return "Agenda - loading…";
        if (result.NeedsSetup) return "Agenda - set up Google access";
        if (result.NeedsLogin) return "Agenda - connect a Google account";
        var events = result.Events;
        var lines = new List<string>();
        var live = LiveEvent(events, now);
        var next = NextEvent(events, now);
        if (live != null) lines.Add($"Now: {ShortTitle(live.Title, 30)} (ends in {Duration(Secs(live.End, now))})");
        if (next != null) lines.Add($"Next: {Hm(next.Start)} {ShortTitle(next.Title, 28)} ({Relative(next, now)})");
        if (lines.Count == 0) lines.Add(events.Count > 0 ? "Nothing left today" : "Nothing scheduled today");
        if (result.Stale) lines.Add("(cached - no network)");
        else if (result.Errors.Count > 0 && events.Count == 0) lines.Add("(could not refresh)");

        // What the number on the icon means. It goes first, but it is the line that
        // gives way when the two titles and the stale note already fill the 127.
        var tooltip = string.Join("\n", lines);
        // Same rule as the icon: yesterday's list, before today's fetch, has no count.
        var left = result.Date == IsoDate(DateOnly.FromDateTime(now.LocalDateTime)) ? Remaining(events, now) : 0;
        if (left > 0)
        {
            var count = left + (left == 1 ? " event left today" : " events left today");
            if (count.Length + 1 + tooltip.Length <= 127) tooltip = count + "\n" + tooltip;
        }
        return tooltip;
    }
}
