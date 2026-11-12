using System.Collections.ObjectModel;
using AgendaTray.Agenda;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;

namespace AgendaTray;

/// <summary>
/// The day, in a flyout anchored to the corner of the screen where the taskbar is.
/// Created once and shown/hidden, never closed: it hides itself the moment it loses
/// the focus, like the popups of the original plugin and the Windows 11 flyouts.
/// </summary>
public sealed partial class PopupWindow : Window
{
    private const double WidthDip = 430;
    private const double MaxHeightDip = 640;

    private readonly AgendaController _app;
    private readonly ObservableCollection<EventItem> _items = [];
    private readonly IntPtr _hwnd;
    private AgendaResult? _shown;
    private long _lastHideTicks;
    private bool _suppressHide;
    private bool _allowClose;
    private bool _cursorTouched;
    private Native.POINT _anchor;
    private SetupMode _setupMode = SetupMode.None;

    private enum SetupMode { None, Waiting, NeedsClient, NeedsAccount }

    internal PopupWindow(AgendaController app)
    {
        _app = app;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

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
            // Alt+F4 hides it; only Quit really closes it.
            if (_allowClose) return;
            e.Cancel = true;
            HidePopup();
        };

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Activated += OnActivated;
        Root.PreviewKeyDown += OnPreviewKeyDown;
        EventsList.ItemsSource = _items;
    }

    public bool IsOpen => AppWindow.IsVisible;

    // ------------------------------------------------------------ show / hide

    public void Toggle()
    {
        if (IsOpen)
        {
            HidePopup();
            return;
        }
        // A click on the tray icon while the popup is open first deactivates it (which
        // hides it) and then arrives here as a click: without this it would reopen.
        if (Environment.TickCount64 - _lastHideTicks < 300) return;
        ShowPopup();
    }

    public void ShowPopup()
    {
        try
        {
            Native.GetCursorPos(out _anchor);
            _cursorTouched = false;
            Render();
            FocusCurrentRow();
            Place(EstimateHeightDip());
            AppWindow.Show(true);
            Activate();
            Native.SetForegroundWindow(_hwnd);
            // Placed again now that it is on the target monitor: a window moved onto
            // a display with another scale gets resized by the DPI change.
            Place(EstimateHeightDip());
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                RefineHeight();
                if (EventsList.SelectedItem is { } item) EventsList.ScrollIntoView(item);
                if (EventsList.Visibility == Visibility.Visible) EventsList.Focus(FocusState.Programmatic);
                else if (SetupPrimary.Visibility == Visibility.Visible) SetupPrimary.Focus(FocusState.Programmatic);
            });
            _app.Refresh(force: false);
        }
        catch (Exception ex)
        {
            Log.Write($"error opening the popup: {ex}");
        }
    }

    public void HidePopup()
    {
        if (!IsOpen) return;
        _lastHideTicks = Environment.TickCount64;
        AppWindow.Hide();
    }

    internal void CloseForGood()
    {
        _allowClose = true;
        Close();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_suppressHide) HidePopup();
    }

    // ----------------------------------------------------------------- layout

    private void Place(double heightDip)
    {
        var area = DisplayArea.GetFromPoint(new PointInt32(_anchor.X, _anchor.Y), DisplayAreaFallback.Nearest);
        var scale = Native.ScaleAt(_anchor.X, _anchor.Y);
        int Px(double dip) => (int)Math.Round(dip * scale);

        var wa = area.WorkArea;
        var ob = area.OuterBounds;
        var margin = Px(12);
        var w = Px(WidthDip);
        var h = Math.Min(Px(heightDip), wa.Height - 2 * margin);

        // The corner nearest the taskbar, wherever the taskbar is.
        var x = wa.X + wa.Width - w - margin;
        var y = wa.Y + wa.Height - h - margin;
        if (wa.X > ob.X) x = wa.X + margin;
        if (wa.Y > ob.Y) y = wa.Y + margin;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    /// <summary>A guess from fixed row heights, good enough to open at the right size;
    /// RefineHeight corrects it from the real layout right after.</summary>
    private double EstimateHeightDip()
    {
        double h = 12 + 10 + 52 + 8 + 8 + 16;
        if (StatusText.Visibility == Visibility.Visible) h += 16 + 8;
        if (SetupPanel.Visibility == Visibility.Visible) h += 200;
        else if (_items.Count > 0 && EventsList.Visibility == Visibility.Visible) h += _items.Count * 58 + 8;
        else h += 96;
        return Math.Clamp(h, 200, MaxHeightDip);
    }

    private void RefineHeight()
    {
        if (!IsOpen || Root.ActualHeight <= 0) return;
        var chrome = Root.ActualHeight - Body.ActualHeight;
        double body;
        if (SetupPanel.Visibility == Visibility.Visible)
        {
            SetupPanel.Measure(new Windows.Foundation.Size(Body.ActualWidth, double.PositiveInfinity));
            body = SetupPanel.DesiredSize.Height + 8;
        }
        else if (EventsList.Visibility == Visibility.Visible && FindScrollViewer(EventsList) is { } sv && sv.ExtentHeight > 0)
        {
            body = sv.ExtentHeight + 4;
        }
        else
        {
            body = 96;
        }
        var target = Math.Clamp(chrome + body, 200, MaxHeightDip);
        if (Math.Abs(target - Root.ActualHeight) > 2) Place(target);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    // ----------------------------------------------------------------- render

    internal void Render()
    {
        var now = _app.Now;
        var result = _app.Result;
        var events = result?.Events ?? [];
        var live = Fmt.LiveEvent(events, now) != null;

        HeaderIcon.Glyph = live ? "" : "";
        HeaderIcon.Foreground = EventItem.Theme(live ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush");
        DateText.Text = Fmt.DateLabel(result?.Date ?? Fmt.IsoDate(DateOnly.FromDateTime(DateTime.Now)));
        MetaText.Text = Fmt.HeroMeta(result, now);
        Spinner.IsActive = _app.Loading;
        Spinner.Visibility = _app.Loading ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = _app.Loading ? Visibility.Collapsed : Visibility.Visible;

        _setupMode = _app.LoggingIn ? SetupMode.Waiting
                   : result is { NeedsSetup: true } ? SetupMode.NeedsClient
                   : result is { NeedsLogin: true } ? SetupMode.NeedsAccount
                   : SetupMode.None;

        if (result is { Stale: true })
        {
            StatusText.Text = "No network — showing the last saved data.";
            StatusText.Foreground = EventItem.Theme("TextFillColorSecondaryBrush");
            StatusText.Visibility = Visibility.Visible;
        }
        else if (_setupMode == SetupMode.None && !string.IsNullOrEmpty(_app.LastError))
        {
            StatusText.Text = _app.LastError;
            StatusText.Foreground = EventItem.Theme("SystemFillColorCriticalBrush");
            StatusText.Visibility = Visibility.Visible;
        }
        else
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
        ToolTipService.SetToolTip(StatusText, StatusText.Visibility == Visibility.Visible ? StatusText.Text : null);

        RenderSetup();

        if (_setupMode == SetupMode.None)
        {
            SyncItems(result, now);
            var any = _items.Count > 0;
            EventsList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            EmptyText.Text = result == null ? "Loading…"
                : result.Errors.Count > 0 ? "Could not reach Google."
                : "Nothing scheduled today.";
            FooterText.Text = "Enter join · J next · R refresh · O open in Calendar · L add account";
        }
        else
        {
            EventsList.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;
            FooterText.Text = "Esc close";
        }

        if (IsOpen) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefineHeight);
    }

    private void RenderSetup()
    {
        SetupPanel.Visibility = _setupMode == SetupMode.None ? Visibility.Collapsed : Visibility.Visible;
        SetupSecondary.Visibility = Visibility.Collapsed;
        SetupPrimary.Visibility = Visibility.Visible;
        SetupLink.Visibility = Visibility.Collapsed;

        switch (_setupMode)
        {
            case SetupMode.Waiting:
                SetupTitle.Text = "Waiting for Google…";
                SetupBody.Text = "Finish signing in in the browser. The app asks for read-only access to your calendars and nothing else.";
                SetupPrimary.Visibility = Visibility.Collapsed;
                SetupSecondary.Content = "Cancel";
                SetupSecondary.Visibility = Visibility.Visible;
                break;

            case SetupMode.NeedsClient:
                var found = _app.DownloadedClient;
                SetupTitle.Text = "Set up Google access";
                SetupBody.Text =
                    "The app reads your calendars through an OAuth client of your own Google Cloud project " +
                    "(the one omarchy-agenda uses works). Pick its JSON: the client_secret_*.json from the " +
                    "Cloud Console, or omarchy-agenda's client.json." +
                    (found != null ? $"\n\nFound in your files: {Path.GetFileName(found)}" : "");
                SetupPrimary.Content = found != null ? "Use that file" : "Choose JSON file…";
                if (found != null)
                {
                    SetupSecondary.Content = "Choose another…";
                    SetupSecondary.Visibility = Visibility.Visible;
                }
                SetupLink.Content = "How to create one in Google Cloud";
                SetupLink.Visibility = Visibility.Visible;
                break;

            case SetupMode.NeedsAccount:
                SetupTitle.Text = "Connect a Google account";
                SetupBody.Text = "Opens the browser so you can allow read-only access to your calendars. " +
                                 "Repeat it for each account: L, or Accounts in the tray menu.";
                SetupPrimary.Content = "Connect account";
                break;
        }
    }

    private void SyncItems(AgendaResult? result, DateTimeOffset now)
    {
        if (ReferenceEquals(result, _shown))
        {
            foreach (var item in _items) item.Tick(now);
            return;
        }

        var selectedKey = (EventsList.SelectedItem as EventItem)?.Event.Key;
        _items.Clear();
        foreach (var e in result?.Events ?? []) _items.Add(new EventItem(e, now));
        _shown = result;

        // Keep the highlight on the meeting, not on the row number, across refreshes
        // - unless nobody has moved it yet, in which case it follows the clock.
        var index = _cursorTouched && selectedKey != null ? IndexOf(selectedKey) : -1;
        if (index >= 0) EventsList.SelectedIndex = index;
        else FocusCurrentRow();
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Event.Key == key) return i;
        return -1;
    }

    /// <summary>Opening the popup lands on the meeting that matters now, not on the
    /// first one of the day: someone opening it at 3pm wants the 3pm one.</summary>
    private void FocusCurrentRow()
    {
        if (_items.Count == 0) return;
        var target = Fmt.FocusEvent(_items.Select(i => i.Event).ToList(), _app.Now);
        var index = target != null ? IndexOf(target.Key) : -1;
        EventsList.SelectedIndex = index >= 0 ? index : _items.Count - 1;
    }

    // ---------------------------------------------------------------- actions

    private void ActivateItem(EventItem? item)
    {
        if (item == null) return;
        HidePopup();
        _app.Activate(item.Event);
    }

    private void EventsList_ItemClick(object sender, ItemClickEventArgs e) => ActivateItem(e.ClickedItem as EventItem);

    private void Join_Click(object sender, RoutedEventArgs e) => ActivateItem((sender as FrameworkElement)?.DataContext as EventItem);

    private void Refresh_Click(object sender, RoutedEventArgs e) => _app.Refresh(force: true);

    private async void SetupPrimary_Click(object sender, RoutedEventArgs e)
    {
        switch (_setupMode)
        {
            case SetupMode.NeedsClient:
                if (_app.DownloadedClient is { } found) _app.SetupFromFile(found);
                else await PickClientFileAsync();
                break;
            case SetupMode.NeedsAccount:
                _app.StartLogin();
                break;
        }
    }

    private async void SetupSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_setupMode == SetupMode.Waiting) _app.CancelLogin();
        else if (_setupMode == SetupMode.NeedsClient) await PickClientFileAsync();
    }

    private void SetupLink_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
        Shell.Open(AgendaController.SetupGuideUrl);
    }

    private async Task PickClientFileAsync()
    {
        // The picker takes the focus; without this the popup would hide under it.
        _suppressHide = true;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file != null) _app.SetupFromFile(file.Path);
        }
        catch (Exception ex)
        {
            Log.Write($"file picker failed: {ex.Message}");
        }
        finally
        {
            _suppressHide = false;
            Activate();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var inSetup = _setupMode != SetupMode.None;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                HidePopup();
                e.Handled = true;
                break;
            case VirtualKey.Enter when !inSetup:
                ActivateItem(EventsList.SelectedItem as EventItem);
                e.Handled = true;
                break;
            case VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown when !inSetup:
                _cursorTouched = true;
                if (EventsList.FocusState == FocusState.Unfocused) EventsList.Focus(FocusState.Keyboard);
                break;
            case VirtualKey.J when !inSetup:
                HidePopup();
                _app.JoinFocused();
                e.Handled = true;
                break;
            case VirtualKey.R:
                _app.Refresh(force: true);
                e.Handled = true;
                break;
            case VirtualKey.O when !inSetup:
                HidePopup();
                _app.OpenInCalendar((EventsList.SelectedItem as EventItem)?.Event);
                e.Handled = true;
                break;
            case VirtualKey.L when _setupMode is SetupMode.None or SetupMode.NeedsAccount:
                _app.StartLogin();
                e.Handled = true;
                break;
        }
    }
}
