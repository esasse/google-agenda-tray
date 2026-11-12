using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AgendaTray.Agenda;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AgendaTray;

/// <summary>
/// The "about to start" card: a few minutes before an event, in the corner above the
/// tray, with one button to join. It appears without taking the focus - a reminder
/// that grabbed the keyboard would eat whatever you were typing - and it stays until
/// you join, dismiss it, or the event is well under way.
/// </summary>
public sealed partial class ReminderWindow : Window
{
    private const double WidthDip = 380;
    private const double HeaderDip = 32 + 10 + 14 + 4;   // header row + root padding + spacing
    private const double RowDip = 122;

    private readonly AgendaController _app;
    private readonly ObservableCollection<EventItem> _items = [];
    private readonly IntPtr _hwnd;
    private readonly Native.SubclassProc _subclass;   // kept alive: the window calls into it
    private int _zOrderLogs;
    private bool _suspended;
    private bool _allowClose;

    internal ReminderWindow(AgendaController app)
    {
        _app = app;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        _subclass = OnWindowMessage;
        if (!Native.SetWindowSubclass(_hwnd, _subclass, (UIntPtr)1, UIntPtr.Zero))
            Log.Write("reminder card: SetWindowSubclass failed; it may drop out of the topmost band");

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            DismissAll();
        };

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Rows.ItemsSource = _items;
    }

    /// <summary>Same identity as the count on the icon: a meeting you are invited to
    /// on two accounts is one reminder.</summary>
    internal static string KeyOf(AgendaEvent e) => e.Title + "|" + e.Start.ToUnixTimeSeconds();

    /// <summary>How long a reminder stays up unanswered: ten minutes into the event,
    /// or its end if it is shorter than that.</summary>
    internal static DateTimeOffset Expiry(AgendaEvent e) =>
        e.End < e.Start.AddMinutes(10) ? e.End : e.Start.AddMinutes(10);

    internal void Add(AgendaEvent e, DateTimeOffset now)
    {
        var key = KeyOf(e);
        if (_items.Any(i => KeyOf(i.Event) == key)) return;
        var at = 0;
        while (at < _items.Count && _items[at].Event.Start <= e.Start) at++;
        _items.Insert(at, new EventItem(e, now));
        Show();
    }

    /// <summary>
    /// Follows the day as it is refetched: a reminder picks up a join link or a time
    /// that changed at the last minute, and goes away when its event is cancelled,
    /// declined, or has run long enough. A meeting on two accounts follows the copy
    /// the card came from, and a copy declined on one account does not take down the
    /// card of the one still accepted. When <paramref name="complete"/> is false some
    /// account failed to bring back its day, so a card missing from it stays up.
    /// </summary>
    internal void Sync(IReadOnlyList<AgendaEvent> events, bool complete, DateTimeOffset now)
    {
        if (_items.Count == 0) return;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var current = _items[i].Event;
            var key = KeyOf(current);
            AgendaEvent? fresh = null;
            var declined = false;
            foreach (var e in events)
            {
                if (KeyOf(e) != key) continue;
                if (Fmt.Declined(e)) declined = true;
                else if (fresh == null || (e.Account == current.Account && fresh.Account != current.Account)) fresh = e;
            }
            if (fresh == null)
            {
                if (complete || declined || now >= Expiry(current)) _items.RemoveAt(i);
                else _items[i].Tick(now);
                continue;
            }
            if (now >= Expiry(fresh))
            {
                _items.RemoveAt(i);
                continue;
            }
            if (!ReferenceEquals(fresh, current)) _items[i] = new EventItem(fresh, now);
            else _items[i].Tick(now);
        }
        if (_items.Count == 0)
        {
            AppWindow.Hide();
            return;
        }
        if (_suspended) return;
        Place();
        KeepOnTop();
    }

    /// <summary>
    /// The agenda popup lists the same events with the same join buttons, in the same
    /// corner: while it is open the card steps aside instead of fighting it for the top
    /// (the popup loses its own topmost style the same way the card did), and comes
    /// back when it closes. Reminders that fall due meanwhile are queued, not lost.
    /// </summary>
    internal void Suspend()
    {
        _suspended = true;
        if (AppWindow.IsVisible) AppWindow.Hide();
    }

    internal void Resume()
    {
        _suspended = false;
        // Deferred: the popup hides first and then acts (Enter, J, a join button), and
        // a card shown in between would blink up for the very event being joined.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_allowClose && _items.Count > 0) Show();
        });
    }

    /// <summary>Joined or opened from anywhere (the popup, the tray, J): its card goes.</summary>
    internal void Forget(AgendaEvent e)
    {
        var key = KeyOf(e);
        Remove(_items.FirstOrDefault(i => KeyOf(i.Event) == key));
    }

    private void Show()
    {
        if (_suspended) return;
        Place();
        if (!AppWindow.IsVisible)
        {
            _zOrderLogs = 0;   // each card that comes up gets its own few log lines
            AppWindow.Show(false);
        }
        // To the front even when the card was already up under another always-on-top
        // window (the popup, a video player): a new reminder must be seen.
        Native.RaiseTopmost(_hwnd);
    }

    /// <summary>
    /// Keeps the card in the topmost band. Something keeps re-inserting this window
    /// below ordinary ones while it is up without being active - seen at runtime: the
    /// style went away within a second of the first show and again every few seconds,
    /// with OverlappedPresenter.IsAlwaysOnTop still reporting true - and a card that
    /// drops out of the topmost band is covered by whatever window is in that corner.
    /// Every z-order change reaches the window as WM_WINDOWPOSCHANGING whoever asks for
    /// it, so the change is dropped there, before it lands (or made HWND_TOPMOST, if the
    /// card is somehow out of the band already).
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING && !_allowClose && lParam != IntPtr.Zero)
        {
            var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            if ((pos.flags & Native.SWP_NOZORDER) == 0 && pos.hwndInsertAfter != Native.HWND_TOPMOST)
            {
                // Already in the topmost band: drop the reorder rather than turning it
                // into HWND_TOPMOST, which would also lift the card to the very top every
                // few seconds - over the popup opened after it, in the same corner.
                // Raising it for a new reminder is Show()'s job, through HWND_TOPMOST,
                // which passes here untouched.
                var topmost = Native.IsTopmost(hWnd);
                if (_zOrderLogs++ < 5)
                    Log.Write($"reminder card kept topmost against a z-order change (insert after 0x{pos.hwndInsertAfter.ToInt64():X}, flags 0x{pos.flags:X}, {(topmost ? "dropped" : "made topmost")})");
                if (topmost) pos.flags |= Native.SWP_NOZORDER;
                else pos.hwndInsertAfter = Native.HWND_TOPMOST;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        else if (msg == Native.WM_NCDESTROY)
        {
            // Destroyed without CloseForGood (session end, a crash path): comctl32 wants
            // the subclass gone before the window is.
            Native.RemoveWindowSubclass(hWnd, _subclass, id);
        }
        return Native.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>Backstop for OnWindowMessage, on every Sync: if the card ever does end
    /// up out of the topmost band, it goes back and the log says so.</summary>
    private void KeepOnTop()
    {
        if (_allowClose || !AppWindow.IsVisible || Native.IsTopmost(_hwnd)) return;
        Native.RaiseTopmost(_hwnd);
        Log.Write($"reminder card raised back to topmost (now {Native.IsTopmost(_hwnd)})");
    }

    private void Place()
    {
        // The primary display's corner, where the tray is: the reminder belongs next
        // to the icon it is about, not on whatever monitor the mouse wandered to.
        var area = DisplayArea.Primary;
        var wa = area.WorkArea;
        var ob = area.OuterBounds;
        var scale = Native.ScaleAt(wa.X + wa.Width - 1, wa.Y + wa.Height - 1);
        int Px(double dip) => (int)Math.Round(dip * scale);

        var margin = Px(12);
        var w = Px(WidthDip);
        var h = Math.Min(Px(HeaderDip + _items.Count * RowDip), wa.Height - 2 * margin);
        var x = wa.X + wa.Width - w - margin;
        var y = wa.Y + wa.Height - h - margin;
        if (wa.X > ob.X) x = wa.X + margin;
        if (wa.Y > ob.Y) y = wa.Y + margin;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void Remove(EventItem? item)
    {
        if (item == null) return;
        _items.Remove(item);
        if (_items.Count == 0) AppWindow.Hide();
        else Place();
    }

    private void DismissAll()
    {
        _items.Clear();
        AppWindow.Hide();
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as EventItem;
        if (item == null) return;
        _app.Activate(item.Event);   // takes the card away too, through Forget
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) =>
        Remove((sender as FrameworkElement)?.DataContext as EventItem);

    private void DismissAll_Click(object sender, RoutedEventArgs e) => DismissAll();

    internal void CloseForGood()
    {
        _allowClose = true;
        Native.RemoveWindowSubclass(_hwnd, _subclass, (UIntPtr)1);
        Close();
    }
}
