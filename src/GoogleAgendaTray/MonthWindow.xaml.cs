using System.Collections.ObjectModel;
using System.Globalization;
using AgendaTray.Agenda;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Text;

namespace AgendaTray;

/// <summary>
/// The month at a glance: a grid like Google Calendar's, with the selected day in full
/// on the side. Unlike the popup it is an ordinary window - it stays open while you
/// look elsewhere, and Alt+Tab finds it. Created once and hidden on close, so it comes
/// back on the month you left it.
/// </summary>
public sealed partial class MonthWindow : Window
{
    private const double DayHeaderDip = 28;
    private const double LineDip = 20;
    private const double CellPaddingDip = 4;
    // Below this a cell has room for the time or for the title, not both, and the
    // title is what says what the event is (the time is in the tooltip and the panel).
    private const double TimeMinCellDip = 150;
    // A month already fetched is drawn at once and refetched behind it after this long.
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(60);

    private readonly AgendaController _app;
    private readonly ObservableCollection<EventItem> _dayItems = [];
    private readonly Dictionary<DateOnly, (AgendaResult Result, DateTimeOffset At)> _months = [];
    private readonly HashSet<DateOnly> _inFlight = [];
    private readonly Dictionary<DateOnly, string> _errors = [];
    private DateOnly _month;
    private DateOnly _selected;
    private DateOnly _today;
    private (int Lines, bool Times) _cellLayout = (-1, false);
    private DateOnly _gridFirst;
    private int _gridWeeks = 1;
    // Bumped by Invalidate: a fetch that started before it brings back the old state.
    private int _generation;
    private bool _placed;
    private bool _allowClose;

    internal MonthWindow(AgendaController app)
    {
        _app = app;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        AppWindow.SetIcon(icon);
        TitleIcon.Source = new BitmapImage(new Uri(icon));
        AppWindow.Title = "Agenda";
        AppWindow.Closing += (_, e) =>
        {
            // Closing hides it; only Quit really closes it.
            if (_allowClose) return;
            e.Cancel = true;
            AppWindow.Hide();
        };

        SystemBackdrop = new MicaBackdrop();
        Root.PreviewKeyDown += OnPreviewKeyDown;
        DayList.ItemsSource = _dayItems;
        // On the grid, by position: a bar belongs to no single cell, and a click
        // re-renders the cells, so the second click of a double click lands on a cell
        // that did not get the first one.
        MonthGrid.Tapped += (_, e) =>
        {
            if (DayAtPoint(e.GetPosition(MonthGrid)) is not { } day) return;
            Select(day);
            GridHost.Focus(FocusState.Pointer);
        };
        // The first tap already selected the day; a day in the next or previous month
        // moved the grid, so the second tap may sit on another date. Open the selection.
        MonthGrid.DoubleTapped += (_, e) =>
        {
            if (DayAtPoint(e.GetPosition(MonthGrid)) is not null) OpenDay(_selected);
        };

        _today = Today();
        _selected = _today;
        _month = FirstOfMonth(_today);
        FooterText.Text = "← → ↑ ↓ day · Page Up/Down month · T today · Enter open the day in Calendar · R refresh · Esc close";
    }

    public bool IsOpen => AppWindow.IsVisible;

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    // ------------------------------------------------------------ show / hide

    public void ShowMonth()
    {
        if (!_placed)
        {
            Place();
            _placed = true;
        }
        // Reopened on another day: back to today, not to the day that was "today" then.
        if (_today != Today()) GoTo(Today());
        Render();
        AppWindow.Show(true);
        Activate();
        GridHost.Focus(FocusState.Programmatic);
        Load(_month, force: false);
    }

    internal void CloseForGood()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>The first time: centred on the screen the mouse is on, as big as a
    /// month wants and the screen allows. After that it stays where it was left.</summary>
    private void Place()
    {
        Native.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var scale = Native.ScaleAt(cursor.X, cursor.Y);
        var wa = area.WorkArea;
        var w = Math.Min((int)Math.Round(1240 * scale), wa.Width * 92 / 100);
        var h = Math.Min((int)Math.Round(860 * scale), wa.Height * 92 / 100);
        AppWindow.MoveAndResize(new RectInt32(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h));
    }

    // ------------------------------------------------------------------- data

    /// <summary>Whatever changed what a fetch returns - a calendar turned off, an account
    /// added or removed - makes every month fetched so far wrong.</summary>
    internal void Invalidate()
    {
        // A fetch in flight started under the old state: its result is thrown away.
        _generation++;
        var shown = _months.TryGetValue(_month, out var m) ? m.Result : null;
        _months.Clear();
        _errors.Clear();
        if (!IsOpen) return;
        Load(_month, force: true);
        // A calendar just unchecked leaves the grid right away, as in the popup; the
        // rest waits for the fetch.
        if (shown != null)
        {
            _app.DropIgnored(shown);
            RenderGrid(shown);
            RenderDay(shown);
        }
    }

    /// <summary>The poll's beat, while the window is up.</summary>
    internal void Poll()
    {
        if (IsOpen) Load(_month, force: true);
    }

    /// <summary>The clock's beat: a new day moves the "today" circle.</summary>
    internal void Tick()
    {
        if (!IsOpen) return;
        if (_today != Today())
        {
            Render();
            return;
        }
        // The side panel's rows: a meeting that ended dims and loses its join button.
        var now = DateTimeOffset.Now;
        foreach (var item in _dayItems) item.Tick(now);
    }

    private async void Load(DateOnly month, bool force)
    {
        if (!force && _months.TryGetValue(month, out var have) && DateTimeOffset.Now - have.At < FreshFor) return;
        if (!_inFlight.Add(month)) return;
        RenderSpinner();
        var generation = _generation;
        var (first, weeks) = MonthModel.Grid(month.Year, month.Month, MonthModel.FirstDayOfWeek);
        try
        {
            var result = await _app.RangeAsync(first, first.AddDays(weeks * 7));
            if (generation == _generation)
            {
                // A failure with nothing to show keeps what was there: a month fetched a
                // minute ago beats an empty grid.
                if (result.Events.Count > 0 || result.Errors.Count == 0 || !_months.ContainsKey(month))
                    _months[month] = (result, DateTimeOffset.Now);
                if (result.Errors.Count > 0) _errors[month] = string.Join(" · ", result.Errors);
                else _errors.Remove(month);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"month {Fmt.IsoDate(month)}: fetch failed: {ex}");
            if (generation == _generation)
                _errors[month] = ex is OperationCanceledException ? "Google took too long to answer (timeout)" : Log.OneLine(ex.Message);
        }
        finally
        {
            _inFlight.Remove(month);
        }
        // Invalidated while it ran (a calendar shown again, an account added or
        // removed): the Load that Invalidate asked for was skipped, so run it now.
        if (generation != _generation && month == _month && IsOpen)
        {
            Load(month, force: true);
            return;
        }
        if (month == _month) Render();
        else RenderSpinner();
    }

    // ----------------------------------------------------------------- render

    private void RenderSpinner()
    {
        var loading = _inFlight.Contains(_month);
        Spinner.IsActive = loading;
        Spinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Render()
    {
        _today = Today();
        var inv = CultureInfo.InvariantCulture;
        MonthTitle.Text = _month.ToString("MMMM yyyy", inv);
        AppWindow.Title = "Agenda - " + MonthTitle.Text;
        RenderSpinner();

        var have = _months.TryGetValue(_month, out var entry);
        var result = have ? entry.Result : null;
        if (result is { NeedsSetup: true } or { NeedsLogin: true })
        {
            StatusText.Text = result.NeedsSetup
                ? "Google access is not set up yet: open the agenda from the tray icon to do it."
                : result.Accounts.Count == 0
                    ? "No Google account connected: open the agenda from the tray icon and connect one."
                    : "Google asks every account to sign in again: open the agenda from the tray icon and connect them.";
            StatusText.Foreground = EventItem.Theme("SystemFillColorCautionBrush");
            StatusText.Visibility = Visibility.Visible;
        }
        else if (_errors.TryGetValue(_month, out var error))
        {
            StatusText.Text = have && result!.Events.Count > 0 && result.Errors.Count == 0
                ? "Could not refresh - showing what was fetched at " + Fmt.Hm(entry.At) + ". " + error
                : error;
            StatusText.Foreground = EventItem.Theme("SystemFillColorCriticalBrush");
            StatusText.Visibility = Visibility.Visible;
        }
        else
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
        ToolTipService.SetToolTip(StatusText, StatusText.Visibility == Visibility.Visible ? StatusText.Text : null);

        RenderGrid(result);
        RenderDay(result);

        var updated = have ? "updated " + Fmt.Hm(entry.At) + " · " : "";
        FooterText.Text = updated + "← → ↑ ↓ day · Page Up/Down month · T today · Enter open the day in Calendar · R refresh · Esc close";
    }

    private void RenderGrid(AgendaResult? result)
    {
        var firstDay = MonthModel.FirstDayOfWeek;
        var (first, weeks) = MonthModel.Grid(_month.Year, _month.Month, firstDay);

        if (WeekdayHeader.Children.Count == 0)
        {
            var names = MonthModel.WeekdayNames(firstDay);
            for (var c = 0; c < 7; c++)
            {
                WeekdayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var name = new TextBlock
                {
                    Text = names[c].ToUpperInvariant(),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = EventItem.Theme("TextFillColorSecondaryBrush"),
                    Margin = new Thickness(10, 0, 0, 0),
                };
                Grid.SetColumn(name, c);
                WeekdayHeader.Children.Add(name);
            }
        }

        MonthGrid.Children.Clear();
        MonthGrid.RowDefinitions.Clear();
        for (var r = 0; r < weeks; r++) MonthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _gridFirst = first;
        _gridWeeks = weeks;

        _cellLayout = CellLayout(weeks);
        // One per meeting: a meeting you are invited to on two accounts is one bar or line.
        var events = MonthModel.Distinct(result?.Events ?? []);
        var bars = events.Where(MonthModel.IsBar).ToList();
        var timed = events.Where(e => !MonthModel.IsBar(e)).ToList();
        for (var w = 0; w < weeks; w++)
        {
            var week = Week(first.AddDays(w * 7), bars, timed, _cellLayout.Lines, _cellLayout.Times);
            Grid.SetRow(week, w);
            MonthGrid.Children.Add(week);
        }
    }

    /// <summary>How many event lines fit in a cell at the grid's current size (the
    /// last one gives way to "+N more" when a day has more than that), and whether a
    /// line has room for the time.</summary>
    private (int Lines, bool Times) CellLayout(int weeks)
    {
        var height = MonthGrid.ActualHeight;
        var width = MonthGrid.ActualWidth;
        if (height <= 0 || width <= 0) return (3, true);
        var lines = Math.Max(1, (int)Math.Floor((height / weeks - DayHeaderDip - 2 * CellPaddingDip) / LineDip));
        return (lines, width / 7 >= TimeMinCellDip);
    }

    private void MonthGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CellLayout(_gridWeeks) != _cellLayout) RenderGrid(_months.TryGetValue(_month, out var m) ? m.Result : null);
    }

    /// <summary>A tiling window manager may hand the window a third of the screen: the
    /// panel gives up width before the month does, and in a narrow column it goes away
    /// (the tooltips still have each event's details).</summary>
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width >= 1100 ? 340 : e.NewSize.Width >= 860 ? 270 : 0;
        if (PanelColumn.Width.Value == width) return;
        PanelColumn.Width = new GridLength(width);
        DayPanel.Visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;
        Body.ColumnSpacing = width > 0 ? 16 : 0;
    }

    /// <summary>
    /// One week: seven cells with their day numbers, the bars on lanes across them, and
    /// under each day's bars its timed events. A cell holds <paramref name="lines"/>
    /// lines; when a day has more than fit, the last one it can use says "+N more".
    /// </summary>
    private Grid Week(DateOnly weekStart, List<AgendaEvent> bars, List<AgendaEvent> timed, int lines, bool times)
    {
        var week = new Grid();
        for (var c = 0; c < 7; c++) week.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        week.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayHeaderDip) });
        for (var l = 0; l < lines; l++) week.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LineDip) });
        week.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        void Put(FrameworkElement element, int row, int column, int columnSpan = 1, int rowSpan = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetColumnSpan(element, columnSpan);
            Grid.SetRowSpan(element, rowSpan);
            week.Children.Add(element);
        }

        for (var c = 0; c < 7; c++)
        {
            var day = weekStart.AddDays(c);
            Put(new Border
            {
                BorderThickness = new Thickness(0, 0, 1, 1),
                BorderBrush = EventItem.Theme("CardStrokeColorDefaultBrush"),
                Background = day == _selected ? EventItem.Theme("SubtleFillColorSecondaryBrush") : new SolidColorBrush(Colors.Transparent),
            }, 0, c, rowSpan: lines + 2);
            Put(DayBadge(day), 0, c);
        }

        // Every lane fits, unless some day needs the last line for "+N more" and a bar
        // sits there: then the bars give up the last line everywhere in the week.
        var segments = MonthModel.Lanes(bars, weekStart);
        var lanes = segments.Count == 0 ? 0 : segments.Max(s => s.Lane) + 1;
        int TopLane(int column, int visible) =>
            segments.Where(s => s.Lane < visible && s.From <= column && column <= s.To).Select(s => s.Lane + 1).DefaultIfEmpty(0).Max();
        var timedOn = Enumerable.Range(0, 7).Select(c => MonthModel.On(timed, weekStart.AddDays(c))).ToArray();
        var visibleLanes = lanes <= lines ? lanes : Math.Max(0, lines - 1);
        if (visibleLanes == lanes && Enumerable.Range(0, 7).Any(c => TopLane(c, lanes) == lines && timedOn[c].Count > 0))
            visibleLanes = Math.Max(0, lines - 1);

        foreach (var segment in segments.Where(s => s.Lane < visibleLanes))
            Put(Bar(segment, weekStart), 1 + segment.Lane, segment.From, segment.To - segment.From + 1);

        for (var c = 0; c < 7; c++)
        {
            var day = weekStart.AddDays(c);
            var inMonth = day.Month == _month.Month;
            var dayTimed = timedOn[c];
            var used = TopLane(c, visibleLanes);
            var free = lines - used;
            var hidden = segments.Count(s => s.Lane >= visibleLanes && s.From <= c && c <= s.To);
            var shown = hidden == 0 && dayTimed.Count <= free ? dayTimed.Count : Math.Max(0, Math.Min(dayTimed.Count, free - 1));
            for (var i = 0; i < shown; i++) Put(TimedLine(dayTimed[i], day, inMonth, times), 1 + used + i, c);
            var more = hidden + dayTimed.Count - shown;
            if (more > 0)
            {
                var moreText = new TextBlock
                {
                    // A cell too narrow for the times is too narrow for the word.
                    Text = times ? $"+{more} more" : $"+{more}",
                    FontSize = 11,
                    Padding = new Thickness(CellPaddingDip + 6, 2, 0, 0),
                    Foreground = EventItem.Theme("TextFillColorSecondaryBrush"),
                };
                // With the side panel gone (a narrow window) this is the only place
                // the events left out of the cell can be read.
                var left = segments.Where(s => s.Lane >= visibleLanes && s.From <= c && c <= s.To).Select(s => s.Event)
                    .Concat(dayTimed.Skip(shown))
                    .Select(ev => ev.Title + "  " + MonthModel.PanelTime(ev));
                ToolTipService.SetToolTip(moreText, string.Join("\n", left));
                Put(moreText, 1 + used + shown, c);
            }
        }

        var selected = _selected.DayNumber - weekStart.DayNumber;
        if (selected is >= 0 and < 7)
        {
            // An inner accent outline, on top of the bars, so the borders between cells
            // keep their width.
            Put(new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = EventItem.Theme("AccentFillColorDefaultBrush"),
                IsHitTestVisible = false,
                Margin = new Thickness(-1, -1, 0, 0),
            }, 0, selected, rowSpan: lines + 2);
        }
        return week;
    }

    /// <summary>The day number: a filled circle for today, the month's name on its 1st.</summary>
    private FrameworkElement DayBadge(DateOnly day)
    {
        var isToday = day == _today;
        var label = day.Day == 1 ? day.ToString("MMM d", CultureInfo.InvariantCulture) : day.Day.ToString(CultureInfo.InvariantCulture);
        var number = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isToday ? EventItem.Theme("TextOnAccentFillColorPrimaryBrush")
                : day.Month == _month.Month ? EventItem.Theme("TextFillColorPrimaryBrush")
                : EventItem.Theme("TextFillColorTertiaryBrush"),
        };
        return new Border
        {
            Height = 22,
            MinWidth = 22,
            Padding = new Thickness(label.Length > 2 ? 7 : 0, 0, label.Length > 2 ? 7 : 0, 0),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(CellPaddingDip + 2, 3, 0, 3),
            Background = isToday ? EventItem.Theme("AccentFillColorDefaultBrush") : null,
            Child = number,
        };
    }

    /// <summary>The day of the cell under a point of the grid: rows and columns are
    /// all the same size.</summary>
    private DateOnly? DayAtPoint(Windows.Foundation.Point p)
    {
        var width = MonthGrid.ActualWidth;
        var height = MonthGrid.ActualHeight;
        if (width <= 0 || height <= 0 || p.X < 0 || p.Y < 0 || p.X >= width || p.Y >= height) return null;
        var column = Math.Min(6, (int)(p.X / (width / 7)));
        var row = Math.Min(_gridWeeks - 1, (int)(p.Y / (height / _gridWeeks)));
        return _gridFirst.AddDays(row * 7 + column);
    }

    /// <summary>One week's piece of an all-day or multi-day event: a filled bar across
    /// its days, with square ends where it goes on into the week before or after.</summary>
    private FrameworkElement Bar(BarSegment segment, DateOnly weekStart)
    {
        var e = segment.Event;
        var color = EventItem.TryParseColor(e.Color);
        var declined = Fmt.Declined(e);
        var text = new TextBlock
        {
            // A timed event spanning days says when it starts, on the day it starts.
            Text = !e.AllDay && !segment.ContinuesLeft ? Fmt.Hm(e.Start) + " " + e.Title : e.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            // Without a calendar colour the bar is the accent, which is light in the dark
            // theme: the theme's own text-on-accent brush stays readable on it.
            Foreground = color is not { } bg ? EventItem.Theme("TextOnAccentFillColorPrimaryBrush")
                : Luminance(bg) > 0.6 ? new SolidColorBrush(Colors.Black)
                : new SolidColorBrush(Colors.White),
            TextDecorations = declined ? TextDecorations.Strikethrough : TextDecorations.None,
        };

        double left = segment.ContinuesLeft ? 0 : 4, right = segment.ContinuesRight ? 0 : 4;
        var bar = new Border
        {
            Margin = new Thickness(segment.ContinuesLeft ? 0 : CellPaddingDip, 1, segment.ContinuesRight ? 0 : CellPaddingDip + 1, 1),
            Padding = new Thickness(6, 0, 4, 0),
            CornerRadius = new CornerRadius(left, right, right, left),
            Background = color is { } c ? new SolidColorBrush(c) : EventItem.Theme("AccentFillColorDefaultBrush"),
            Child = text,
        };
        var inMonth = Enumerable.Range(segment.From, segment.To - segment.From + 1)
            .Any(i => weekStart.AddDays(i).Month == _month.Month);
        if (declined || !inMonth) bar.Opacity = declined ? 0.45 : 0.6;
        ToolTipService.SetToolTip(bar, e.Title + "\n" + MonthModel.PanelTime(e) + "\n" + Fmt.MetaLine(e));
        return bar;
    }

    /// <summary>A timed event in its day's cell: a coloured dot, the time on the day it
    /// starts, and the title.</summary>
    private static FrameworkElement TimedLine(AgendaEvent e, DateOnly day, bool inMonth, bool times)
    {
        var color = EventItem.TryParseColor(e.Color);
        var brush = color is { } c ? new SolidColorBrush(c) : EventItem.Theme("AccentFillColorDefaultBrush");
        var declined = Fmt.Declined(e);
        var time = MonthModel.CellTime(e, day);
        var line = new Grid { ColumnSpacing = 6, Padding = new Thickness(CellPaddingDip + 6, 0, CellPaddingDip + 2, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
        // One TextBlock, so the trimming cuts the line as a whole.
        var text = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (times && time.Length > 0)
            text.Inlines.Add(new Run { Text = time + " ", Foreground = EventItem.Theme("TextFillColorSecondaryBrush") });
        text.Inlines.Add(new Run
        {
            Text = e.Title,
            TextDecorations = declined ? TextDecorations.Strikethrough : TextDecorations.None,
        });
        Grid.SetColumn(text, 1);
        line.Children.Add(text);

        if (declined || !inMonth) line.Opacity = declined ? 0.45 : 0.6;
        ToolTipService.SetToolTip(line, e.Title + "\n" + MonthModel.PanelTime(e) + "\n" + Fmt.MetaLine(e));
        return line;
    }

    private static double Luminance(Windows.UI.Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    private void RenderDay(AgendaResult? result)
    {
        DayTitle.Text = _selected.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        var now = DateTimeOffset.Now;
        _dayItems.Clear();
        if (result == null)
        {
            DayMeta.Text = _inFlight.Contains(_month) ? "loading…" : "";
            DayEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        var events = MonthModel.On(result.Events, _selected);
        foreach (var e in events) _dayItems.Add(new EventItem(e, now));

        var distinct = MonthModel.Distinct(events);
        var meetings = distinct.Count(e => !string.IsNullOrEmpty(e.JoinUrl));
        var parts = new List<string>();
        if (distinct.Count > 0) parts.Add(distinct.Count + (distinct.Count == 1 ? " event" : " events"));
        if (meetings > 0) parts.Add(meetings + (meetings == 1 ? " meeting" : " meetings"));
        var busy = BusyTime(distinct);
        if (busy > TimeSpan.Zero) parts.Add(Fmt.Duration(busy.TotalSeconds) + " booked");
        DayMeta.Text = parts.Count > 0 ? string.Join(" · ", parts) : "";
        DayEmpty.Text = "Nothing scheduled.";
        DayEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>How much of the selected day is taken by timed, busy, not declined
    /// events - overlaps counted once, as the time you actually lose. Anything a day
    /// long or longer (a trip, a cruise entered with times) is context, not a booking:
    /// counted, it made every day of a week-long trip "24h booked".</summary>
    private TimeSpan BusyTime(List<AgendaEvent> events)
    {
        var dayStart = AgendaService.LocalMidnight(_selected);
        var dayEnd = AgendaService.LocalMidnight(_selected.AddDays(1));
        var spans = events
            .Where(e => !e.AllDay && e.Busy && !Fmt.Declined(e) && e.End - e.Start < TimeSpan.FromHours(24))
            .Select(e => (Start: e.Start < dayStart ? dayStart : e.Start, End: e.End > dayEnd ? dayEnd : e.End))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
        var total = TimeSpan.Zero;
        DateTimeOffset? runStart = null, runEnd = null;
        foreach (var (start, end) in spans)
        {
            if (runEnd is { } re && start <= re)
            {
                if (end > re) runEnd = end;
                continue;
            }
            if (runStart is { } rs) total += runEnd!.Value - rs;
            runStart = start;
            runEnd = end;
        }
        if (runStart is { } last) total += runEnd!.Value - last;
        return total;
    }

    // ---------------------------------------------------------------- actions

    private void Select(DateOnly day)
    {
        if (day == _selected) return;
        GoTo(day);
        Render();
        Load(_month, force: false);
    }

    /// <summary>Moves the selection, and the month with it when the day is in another.</summary>
    private void GoTo(DateOnly day)
    {
        _selected = day;
        _month = FirstOfMonth(day);
    }

    private void MoveMonths(int delta)
    {
        // Same day in the other month, clamped: Jan 31 + 1 month is Feb 28.
        var target = _month.AddMonths(delta);
        var day = Math.Min(_selected.Day, DateTime.DaysInMonth(target.Year, target.Month));
        GoTo(new DateOnly(target.Year, target.Month, day));
        Render();
        Load(_month, force: false);
    }

    private void Today_Click(object sender, RoutedEventArgs e) => Select(Today());
    private void Prev_Click(object sender, RoutedEventArgs e) => MoveMonths(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => MoveMonths(1);
    private void Refresh_Click(object sender, RoutedEventArgs e) => Load(_month, force: true);

    private void DayList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EventItem item) _app.OpenInCalendar(item.Event);
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EventItem item) _app.Activate(item.Event);
    }

    private static void OpenDay(DateOnly day) =>
        Shell.Open($"https://calendar.google.com/calendar/r/day/{day.Year}/{day.Month}/{day.Day}");

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                AppWindow.Hide();
                break;
            case VirtualKey.Left: Select(_selected.AddDays(-1)); break;
            case VirtualKey.Right: Select(_selected.AddDays(1)); break;
            case VirtualKey.Up: Select(_selected.AddDays(-7)); break;
            case VirtualKey.Down: Select(_selected.AddDays(7)); break;
            case VirtualKey.PageUp: MoveMonths(-1); break;
            case VirtualKey.PageDown: MoveMonths(1); break;
            case VirtualKey.T or VirtualKey.Home: Select(Today()); break;
            // Enter on a focused button (Today, previous, next, refresh) presses it.
            case VirtualKey.Enter when e.OriginalSource is ButtonBase: return;
            case VirtualKey.Enter or VirtualKey.O: OpenDay(_selected); break;
            case VirtualKey.R: Load(_month, force: true); break;
            default: return;
        }
        e.Handled = true;
    }
}
