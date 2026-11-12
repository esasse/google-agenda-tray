using System.ComponentModel;
using AgendaTray.Agenda;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace AgendaTray;

/// <summary>One row of the popup. Everything time-dependent is recomputed on Tick,
/// which raises a single "all properties changed" so the countdowns stay alive
/// without rebuilding the list (and losing the selection).</summary>
public sealed class EventItem : INotifyPropertyChanged
{
    private DateTimeOffset _now;

    internal EventItem(AgendaEvent e, DateTimeOffset now)
    {
        Event = e;
        _now = now;
    }

    internal AgendaEvent Event { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Tick(DateTimeOffset now)
    {
        _now = now;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private RowState State => Fmt.State(Event, _now);

    public string StartText => Event.AllDay ? "all" : Fmt.Hm(Event.Start);
    public string EndText => Event.AllDay ? "day" : Fmt.Hm(Event.End);
    public string Title => Event.Title;
    public string Meta => Fmt.MetaLine(Event);
    public string Relative => State == RowState.Past ? "" : Fmt.Relative(Event, _now);

    /// <summary>The reminder's second line: "in 2 min · 14:00–14:30".</summary>
    public string WhenText => Fmt.Relative(Event, _now) + " · " + Fmt.Hm(Event.Start) + "–" + Fmt.Hm(Event.End);

    /// <summary>The reminder's button: the call when there is one, the event otherwise.</summary>
    public string ActionLabel => string.IsNullOrEmpty(Event.JoinUrl) ? "Open in Calendar" : "Join " + Fmt.JoinName(Event.JoinKind);
    public string ActionTooltip => string.IsNullOrEmpty(Event.JoinUrl) ? "Open the event in Google Calendar" : JoinTooltip;
    public string ActionGlyph => string.IsNullOrEmpty(Event.JoinUrl) ? "\uE787" : "\uE714";

    public double RowOpacity => State == RowState.Past || Fmt.Declined(Event) ? 0.45 : 1.0;

    public TextDecorations TitleDecorations => Fmt.Declined(Event) ? TextDecorations.Strikethrough : TextDecorations.None;

    public Visibility JoinVisibility => Fmt.IsJoinable(Event, _now) ? Visibility.Visible : Visibility.Collapsed;

    public string JoinTooltip =>
        "Join " + Fmt.JoinName(Event.JoinKind) +
        (!string.IsNullOrEmpty(Event.OpenAs) && !string.Equals(Event.OpenAs, Event.Account, StringComparison.OrdinalIgnoreCase)
            ? " as " + Event.OpenAs
            : "");

    /// <summary>The source calendar's colour: with several accounts in one list, it
    /// says where the appointment came from before you read a word.</summary>
    public Brush StripeBrush => TryParseColor(Event.Color) is { } c ? new SolidColorBrush(c) : Theme("AccentFillColorDefaultBrush");

    public Brush TimeBrush => State == RowState.Live ? Theme("SystemFillColorCriticalBrush") : Theme("TextFillColorPrimaryBrush");

    public Brush RelativeBrush => State switch
    {
        RowState.Live => Theme("SystemFillColorCriticalBrush"),
        RowState.Soon => Theme("SystemFillColorCautionBrush"),
        _ => Theme("TextFillColorSecondaryBrush"),
    };

    internal static Brush Theme(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b
            ? b
            : new SolidColorBrush(Colors.Gray);

    private static Windows.UI.Color? TryParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length != 7) return null;
        try
        {
            var rgb = Convert.ToInt32(hex[1..], 16);
            return ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
