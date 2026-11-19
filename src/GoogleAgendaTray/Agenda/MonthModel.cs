using System.Globalization;

namespace AgendaTray.Agenda;

/// <summary>
/// The month view's arithmetic: which days the grid shows and which events land on
/// each. Stateless, like Fmt.
/// </summary>
internal static class MonthModel
{
    /// <summary>The week starts where the Windows region says (Sunday in Brazil and
    /// the US, Monday in most of Europe), like the taskbar's own calendar.</summary>
    public static DayOfWeek FirstDayOfWeek => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>The first cell of the grid and how many weeks it has: whole weeks from
    /// the one holding the 1st to the one holding the last day, so 4 to 6 rows, as
    /// Google Calendar draws it.</summary>
    public static (DateOnly First, int Weeks) Grid(int year, int month, DayOfWeek firstDayOfWeek)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var start = first.AddDays(-(((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        var weeks = (last.DayNumber - start.DayNumber) / 7 + 1;
        return (start, weeks);
    }

    /// <summary>
    /// Whether the event takes up part of <paramref name="day"/>. A multi-day event is
    /// on every day it covers - an all-day one ends at midnight of the day after its
    /// last, so it does not spill into that one. An event that ends as it starts sits
    /// on the day it starts.
    /// </summary>
    public static bool Covers(AgendaEvent e, DateOnly day)
    {
        var dayStart = AgendaService.LocalMidnight(day);
        var dayEnd = AgendaService.LocalMidnight(day.AddDays(1));
        if (e.End <= e.Start) return e.Start >= dayStart && e.Start < dayEnd;
        return e.Start < dayEnd && e.End > dayStart;
    }

    /// <summary>The day's events in the order the cell draws them: all-day first, then
    /// by start time.</summary>
    public static List<AgendaEvent> On(IEnumerable<AgendaEvent> events, DateOnly day) =>
        events.Where(e => Covers(e, day))
            .OrderBy(e => !e.AllDay)
            .ThenBy(e => e.Start)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>One per meeting, for the cells: a meeting you are invited to on two
    /// accounts takes one line there. The side panel lists both, like the popup.</summary>
    public static List<AgendaEvent> Distinct(List<AgendaEvent> events)
    {
        var seen = new HashSet<(string, long)>();
        return events.Where(e => seen.Add((e.Title, e.Start.ToUnixTimeSeconds()))).ToList();
    }

    /// <summary>The first and last day the event takes up. An all-day event ends at
    /// midnight of the day after its last, and so does a timed one ending at midnight.
    /// Measured against LocalMidnight, like Covers: where DST starts at midnight that
    /// "midnight" is 01:00, and the end minus a tick would still be on the next day.</summary>
    public static (DateOnly First, DateOnly Last) Days(AgendaEvent e)
    {
        var first = DateOnly.FromDateTime(e.Start.LocalDateTime);
        if (e.End <= e.Start) return (first, first);
        var last = DateOnly.FromDateTime(e.End.LocalDateTime);
        if (AgendaService.LocalMidnight(last) >= e.End) last = last.AddDays(-1);
        return (first, last < first ? first : last);
    }

    /// <summary>Drawn as a bar across its days, like Google Calendar: all-day events and
    /// anything a day long or longer. A meeting that only crosses midnight stays a
    /// line with its time.</summary>
    public static bool IsBar(AgendaEvent e) => e.AllDay || e.End - e.Start >= TimeSpan.FromHours(24);

    /// <summary>
    /// The bars of one week, each on a lane (a line of the cells) that no other bar
    /// uses on the same days. Earlier bars go first, and the longer of two starting
    /// together, so they take the upper lanes, like Google Calendar; each week is packed on its own, so a bar crossing into the next
    /// week may sit on another lane there.
    /// </summary>
    public static List<BarSegment> Lanes(IEnumerable<AgendaEvent> bars, DateOnly weekStart)
    {
        var weekEnd = weekStart.AddDays(6);
        var segments = new List<(AgendaEvent Event, int From, int To, bool Left, bool Right)>();
        foreach (var e in bars)
        {
            var (first, last) = Days(e);
            if (last < weekStart || first > weekEnd) continue;
            var from = first < weekStart ? 0 : first.DayNumber - weekStart.DayNumber;
            var to = last > weekEnd ? 6 : last.DayNumber - weekStart.DayNumber;
            segments.Add((e, from, to, (first < weekStart), (last > weekEnd)));
        }

        var taken = new List<bool[]>();
        var placed = new List<BarSegment>();
        foreach (var s in segments
                     .OrderBy(s => s.From)
                     .ThenByDescending(s => s.To - s.From)
                     .ThenBy(s => s.Event.Start)
                     .ThenBy(s => s.Event.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var lane = 0;
            while (lane < taken.Count && Enumerable.Range(s.From, s.To - s.From + 1).Any(c => taken[lane][c])) lane++;
            if (lane == taken.Count) taken.Add(new bool[7]);
            for (var c = s.From; c <= s.To; c++) taken[lane][c] = true;
            placed.Add(new BarSegment(s.Event, s.From, s.To, s.Left, s.Right, lane));
        }
        return placed;
    }

    /// <summary>"09:30" for a meeting that starts on this day; nothing for an all-day
    /// event or for the later days of one that started before.</summary>
    public static string CellTime(AgendaEvent e, DateOnly day) =>
        e.AllDay || DateOnly.FromDateTime(e.Start.LocalDateTime) != day ? "" : Fmt.Hm(e.Start);

    /// <summary>The side panel's times: "09:30–10:00", "all day", or the dates for
    /// anything spanning days ("Nov 18 14:00 – Nov 20 10:00").</summary>
    public static string PanelTime(AgendaEvent e)
    {
        var inv = CultureInfo.InvariantCulture;
        if (e.AllDay)
        {
            var (firstDay, lastDay) = Days(e);
            return lastDay <= firstDay ? "all day" : $"{firstDay.ToString("MMM d", inv)} – {lastDay.ToString("MMM d", inv)}";
        }
        var startDay = DateOnly.FromDateTime(e.Start.LocalDateTime);
        var endDay = DateOnly.FromDateTime(e.End.LocalDateTime);
        // Ending at midnight is ending on the same day, for the reader.
        if (endDay == startDay || (endDay == startDay.AddDays(1) && e.End.LocalDateTime.TimeOfDay == TimeSpan.Zero))
            return $"{Fmt.Hm(e.Start)}–{Fmt.Hm(e.End)}";
        return $"{startDay.ToString("MMM d", inv)} {Fmt.Hm(e.Start)} – {endDay.ToString("MMM d", inv)} {Fmt.Hm(e.End)}";
    }

    /// <summary>Sunday-first names rotated to the region's first day, in English like
    /// the rest of the app.</summary>
    public static string[] WeekdayNames(DayOfWeek firstDayOfWeek)
    {
        var names = CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames;
        return Enumerable.Range(0, 7).Select(i => names[((int)firstDayOfWeek + i) % 7]).ToArray();
    }
}

/// <summary>One week's piece of a bar: columns <see cref="From"/> to <see cref="To"/>
/// (0-6), and whether the event goes on before or after this week.</summary>
internal sealed record BarSegment(AgendaEvent Event, int From, int To, bool ContinuesLeft, bool ContinuesRight, int Lane);
