using AgendaTray.Agenda;
using AgendaTray.Google;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AgendaTray;

/// <summary>
/// Owns the day and everything that keeps it current: the periodic poll, a clock for
/// the countdowns, the retries, and the one-click actions. The tray icon and the
/// popup only draw what is here. The counterpart of the plugin's Service.qml.
/// </summary>
internal sealed class AgendaController : IDisposable
{
    public const string SetupGuideUrl = "https://github.com/esasse/google-agenda-tray#setup-once";
    private static readonly int[] RetryDelaysSec = [20, 60, 120, 300];

    private readonly DispatcherQueue _dispatcher;
    private readonly TrayHost _tray;
    private readonly PopupWindow _popup;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _retryTimer;
    private CancellationTokenSource? _loginCts;
    private bool _fetching;
    private bool _refreshAgain;
    private int _retryCount;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTick = DateTimeOffset.Now;
    private string? _edgeRefreshedFor;
    private string? _dayRefreshedFor;
    private string _iconSignature = "";
    private bool _lightTaskbar = TrayIconRenderer.TaskbarIsLight();

    public AgendaController(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        AppPaths.Ensure();
        Settings = Settings.Load();
        Native.AllowDarkMenus();

        _tray = new TrayHost();
        _tray.Click += () => _popup!.Toggle();
        _tray.HotkeyPressed += () => _popup!.Toggle();
        _tray.ShowRequested += () => { if (!_popup!.IsOpen) _popup.ShowPopup(); };
        _tray.MiddleClick += JoinFocused;
        _tray.ContextMenuRequested += ShowMenu;
        _tray.AppearanceChanged += () =>
        {
            _lightTaskbar = TrayIconRenderer.TaskbarIsLight();
            _iconSignature = "";
            UpdateTray();
        };

        _popup = new PopupWindow(this);

        _pollTimer = dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(Settings.RefreshIntervalSec);
        _pollTimer.Tick += (_, _) => Refresh(force: true);

        // Keeps the countdowns alive without hitting the API; also notices a new day,
        // a resume from sleep, and a meeting about to start.
        _clockTimer = dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(15);
        _clockTimer.Tick += (_, _) => OnClock();

        _retryTimer = dispatcher.CreateTimer();
        _retryTimer.IsRepeating = false;
        _retryTimer.Tick += (_, _) => Refresh(force: true);
    }

    public Settings Settings { get; private set; }
    public AgendaResult? Result { get; private set; }
    public bool Loading { get; private set; }
    public bool LoggingIn => _loginCts != null;
    public string? LastError { get; private set; }
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.Now;
    public string? DownloadedClient => OAuthClient.FindDownloaded();

    private IReadOnlyList<AgendaEvent> Events => Result?.Events ?? [];

    public void Start(bool showPopup)
    {
        // Today's cache first, so the icon and the popup have something true to show
        // before the first fetch comes back.
        var cached = AgendaService.LoadCache(Today());
        if (cached != null && OAuthClient.Load() != null) Result = cached;

        RegisterHotkey();
        UpdateTray();
        _pollTimer.Start();
        _clockTimer.Start();
        Refresh(force: true);
        if (showPopup) _popup.ShowPopup();
        Log.Write($"started (refresh {Settings.RefreshIntervalSec}s, hotkey {(Settings.Hotkey.Length > 0 ? Settings.Hotkey : "none")})");
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    // ---------------------------------------------------------------- refresh

    public async void Refresh(bool force)
    {
        if (_fetching)
        {
            // The fetch in flight may predate what asked for this one (a new account,
            // a removed one, a reloaded config): run again when it ends.
            if (force) _refreshAgain = true;
            return;
        }
        if (!force && DateTimeOffset.Now - _lastFetch < TimeSpan.FromSeconds(20)) return;
        _fetching = true;
        Loading = true;
        Changed();

        var settings = Settings;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var result = await Task.Run(() => AgendaService.DayAsync(Today(), settings, cts.Token));
            Apply(result);
        }
        catch (Exception ex)
        {
            LastError = cts.IsCancellationRequested ? "Google took too long to answer (timeout)" : Log.OneLine(ex.Message);
            Log.Write($"fetch failed: {ex}");
            ScheduleRetry();
        }
        finally
        {
            _fetching = false;
            Loading = false;
            _lastFetch = DateTimeOffset.Now;
            Changed();
        }
        if (_refreshAgain)
        {
            _refreshAgain = false;
            Refresh(force: true);
        }
    }

    private void Apply(AgendaResult result)
    {
        Result = result;
        LastError = result.Errors.FirstOrDefault();
        foreach (var error in result.Errors) Log.Write($"fetch: {error}");

        // Retry soon only for what time can fix (the network); a missing setup or an
        // expired grant waits for the user.
        var transient = result.Errors.Count > 0 && !result.NeedsSetup && !result.NeedsLogin &&
                        result.Reauth.Count < result.Errors.Count;
        if (transient) ScheduleRetry();
        else
        {
            _retryCount = 0;
            _retryTimer.Stop();
        }
        if (result.Errors.Count == 0) Log.Write($"fetch ok: {result.Events.Count} events in {result.Accounts.Count} accounts");
    }

    /// <summary>Back off 20s, 60s, 120s, 300s, then wait for the regular poll. The
    /// usual cause is a resume from sleep with the network a few seconds behind.</summary>
    private void ScheduleRetry()
    {
        if (_retryCount >= RetryDelaysSec.Length) return;
        var delay = RetryDelaysSec[_retryCount++];
        _retryTimer.Interval = TimeSpan.FromSeconds(delay);
        _retryTimer.Start();
        Log.Write($"retrying in {delay}s");
    }

    private void OnClock()
    {
        var now = DateTimeOffset.Now;
        // Timers do not fire while the machine sleeps: a jump in the wall clock means
        // a resume, and the data may be hours old.
        if ((now - _lastTick).TotalSeconds > 180)
        {
            Log.Write($"clock jumped {(int)(now - _lastTick).TotalMinutes}min (resume from sleep); refreshing");
            _retryCount = 0;
            Refresh(force: true);
        }
        _lastTick = now;

        // Once per day: if that fetch fails, the retry backoff takes over instead of
        // a new attempt on every tick.
        var today = Fmt.IsoDate(Today());
        if (Result != null && Result.Date != today && _dayRefreshedFor != today)
        {
            _dayRefreshedFor = today;
            Log.Write("new day; refreshing");
            Refresh(force: true);
        }

        // A meeting about to start: one extra pass to pick up a link or a room
        // that changed at the last minute.
        var next = Fmt.NextEvent(Events, now);
        if (next != null && next.Key != _edgeRefreshedFor)
        {
            var delta = (next.Start - now).TotalSeconds;
            if (delta > 0 && delta < 90)
            {
                _edgeRefreshedFor = next.Key;
                Refresh(force: true);
            }
        }

        Changed();
    }

    /// <summary>Redraws whatever depends on the state or the clock.</summary>
    private void Changed()
    {
        Now = DateTimeOffset.Now;
        try
        {
            UpdateTray();
            if (_popup.IsOpen) _popup.Render();
        }
        catch (Exception ex)
        {
            Log.Write($"error updating the UI: {ex}");
        }
    }

    // -------------------------------------------------------------------- tray

    private void UpdateTray()
    {
        var now = DateTimeOffset.Now;
        var events = Events;
        TrayGlyph glyph;
        // The calendar page carries how many events are left today. Before the first
        // fetch, and after midnight until today's fetch lands (yesterday's events have
        // all ended), that number is unknown, and a 0 would claim a clear day.
        var known = Result != null && Result.Date == Fmt.IsoDate(Today());
        var text = known ? Math.Min(Fmt.Remaining(events, now), 99).ToString() : "…";

        var live = Fmt.LiveEvent(events, now);
        var next = Fmt.NextEvent(events, now);
        if (Result is { NeedsSetup: true } or { NeedsLogin: true })
        {
            glyph = TrayGlyph.Attention;
        }
        else if (live != null)
        {
            glyph = TrayGlyph.Live;
        }
        else if (next != null && (next.Start - now).TotalSeconds <= Fmt.SoonSec)
        {
            glyph = TrayGlyph.Soon;
            text = Math.Max(1, (int)Math.Ceiling((next.Start - now).TotalMinutes)).ToString();
        }
        else if (Result != null && events.Count == 0 && Result.Errors.Count > 0)
        {
            glyph = TrayGlyph.Attention;
        }
        else
        {
            glyph = TrayGlyph.Day;
        }

        var tooltip = Fmt.Tooltip(Result, now);
        var size = TrayIconRenderer.IconSize();
        var signature = $"{glyph}|{text}|{size}|{_lightTaskbar}";
        if (signature == _iconSignature)
        {
            _tray.SetTooltip(tooltip);
            return;
        }
        _iconSignature = signature;
        _tray.Update(TrayIconRenderer.Render(glyph, text, size, _lightTaskbar), tooltip);
    }

    private enum Cmd
    {
        Open = 1, Refresh, Join, Calendar, AddAccount, Setup, Calendars, Config, ReloadConfig, LogFile, Quit,
        RemoveAccount = 100,
    }

    private void ShowMenu()
    {
        _popup.HidePopup();
        var accounts = AccountStore.List();
        var focus = Fmt.FocusEvent(Events, DateTimeOffset.Now);

        var accountItems = new List<MenuEntry> { new("Add a Google account…", (int)Cmd.AddAccount, Enabled: !LoggingIn) };
        if (accounts.Count > 0) accountItems.Add(MenuEntry.Separator);
        for (var i = 0; i < accounts.Count; i++)
            accountItems.Add(new MenuEntry($"Remove {accounts[i].Email}…", (int)Cmd.RemoveAccount + i));

        var joinText = focus == null ? "Join the next meeting"
            : $"Join: {Fmt.ShortTitle(focus.Title, 40)}";
        var entries = new List<MenuEntry>
        {
            new("Open agenda", (int)Cmd.Open, IsDefault: true),
            new(joinText, (int)Cmd.Join, Enabled: focus != null),
            new("Refresh now", (int)Cmd.Refresh),
            MenuEntry.Separator,
            new("Open Google Calendar", (int)Cmd.Calendar),
            new("Accounts", Children: accountItems),
            new("Set up Google access…", (int)Cmd.Setup),
            new("Show calendars and their ids", (int)Cmd.Calendars, Enabled: accounts.Count > 0),
            MenuEntry.Separator,
            new("Edit configuration", (int)Cmd.Config),
            new("Reload configuration", (int)Cmd.ReloadConfig),
            new("Open log", (int)Cmd.LogFile),
            MenuEntry.Separator,
            new("Quit", (int)Cmd.Quit),
        };

        var chosen = _tray.ShowMenu(entries);
        switch ((Cmd)chosen)
        {
            case Cmd.Open: _popup.ShowPopup(); break;
            case Cmd.Refresh: _retryCount = 0; Refresh(force: true); break;
            case Cmd.Join: JoinFocused(); break;
            case Cmd.Calendar: OpenInCalendar(null); break;
            case Cmd.AddAccount: StartLogin(); break;
            case Cmd.Setup: Setup(); break;
            case Cmd.Calendars: ShowCalendars(); break;
            case Cmd.Config: Shell.Edit(AppPaths.ConfigFile); break;
            case Cmd.ReloadConfig: ReloadSettings(); break;
            case Cmd.LogFile: Shell.Edit(AppPaths.LogFile); break;
            case Cmd.Quit: Quit(); break;
            default:
                var index = chosen - (int)Cmd.RemoveAccount;
                if (index >= 0 && index < accounts.Count) RemoveAccount(accounts[index]);
                break;
        }
    }

    private void RegisterHotkey()
    {
        var spec = Settings.Hotkey;
        if (string.IsNullOrWhiteSpace(spec))
        {
            // An empty hotkey turns it off, also on a reload that had one before.
            _tray.UnregisterHotkey();
            return;
        }
        if (!Hotkeys.TryParse(spec, out var mods, out var vk))
        {
            _tray.UnregisterHotkey();
            Log.Write($"could not parse the hotkey \"{spec}\"");
            return;
        }
        if (_tray.RegisterHotkey(mods, vk)) Log.Write($"global hotkey registered: {spec}");
        else Log.Write($"global hotkey {spec} is taken by another program; continuing without it");
    }

    private void ReloadSettings()
    {
        Settings = Settings.Load();
        _pollTimer.Interval = TimeSpan.FromSeconds(Settings.RefreshIntervalSec);
        RegisterHotkey();
        Log.Write("configuration reloaded");
        Refresh(force: true);
    }

    // ---------------------------------------------------------------- actions

    /// <summary>One click = join. With no video link, the event in Google Calendar is
    /// the best "open this" a room-less appointment has.</summary>
    public void Activate(AgendaEvent? e)
    {
        if (e == null) return;
        Shell.Open(!string.IsNullOrEmpty(e.JoinUrl) ? e.JoinUrl : e.HtmlLink);
    }

    public void JoinFocused() => Activate(Fmt.FocusEvent(Events, DateTimeOffset.Now));

    public void OpenInCalendar(AgendaEvent? e) =>
        Shell.Open(!string.IsNullOrEmpty(e?.HtmlLink) ? e.HtmlLink : "https://calendar.google.com/calendar/r/day");

    public void SetupFromFile(string path)
    {
        try
        {
            OAuthClient.FromFile(path).Save();
            Log.Write($"OAuth client stored from {Path.GetFileName(path)}");
            if (AccountStore.List().Count == 0) StartLogin();
            else Refresh(force: true);
        }
        catch (Exception ex)
        {
            Log.Write($"setup failed: {ex.Message}");
            LastError = ex.Message;
            _tray.Balloon("Could not use that file", ex.Message, warning: true);
            Changed();
        }
    }

    private void Setup()
    {
        // The popup has the setup card with the file picker; with a client already
        // stored it would not show it, so say what is there and how to replace it.
        if (OAuthClient.Load() == null)
        {
            _popup.ShowPopup();
            return;
        }
        var found = DownloadedClient;
        var message = found != null
            ? $"An OAuth client is already stored.\n\nReplace it with {Path.GetFileName(found)} from your Downloads?"
            : "An OAuth client is already stored. To replace it, download a new client_secret JSON " +
              "from Google Cloud to your Downloads folder and choose this again.";
        var flags = found != null ? Native.MB_YESNO | Native.MB_ICONQUESTION : 0x40u;
        if (Native.MessageBoxW(_tray.Handle, message, "Google Agenda Tray", flags | Native.MB_SETFOREGROUND) == Native.IDYES && found != null)
            SetupFromFile(found);
    }

    public async void StartLogin()
    {
        if (LoggingIn) return;
        var client = OAuthClient.Load();
        if (client == null)
        {
            _popup.ShowPopup();
            return;
        }
        _loginCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Changed();
        try
        {
            var token = _loginCts.Token;
            var email = await Task.Run(() => GoogleLogin.RunAsync(client, token));
            AgendaService.ClearCache();
            _tray.Balloon("Google account connected", email);
            _retryCount = 0;
            _loginCts.Dispose();
            _loginCts = null;
            Refresh(force: true);
        }
        catch (OperationCanceledException)
        {
            Log.Write("login cancelled or timed out");
        }
        catch (Exception ex)
        {
            Log.Write($"login failed: {ex}");
            LastError = $"Login failed: {Log.OneLine(ex.Message)}";
            _tray.Balloon("Login failed", Log.OneLine(ex.Message), warning: true);
        }
        finally
        {
            _loginCts?.Dispose();
            _loginCts = null;
            Changed();
        }
    }

    public void CancelLogin() => _loginCts?.Cancel();

    /// <summary>
    /// Forgets the account here without revoking it at Google. Revoking ends the grant
    /// for the whole OAuth client, and the client is meant to be shared with
    /// omarchy-agenda (the same Cloud project): on a dual-boot machine a removal here
    /// would quietly disconnect the account on the Linux side too.
    /// </summary>
    private async void RemoveAccount(Account account)
    {
        var answer = Native.MessageBoxW(_tray.Handle,
            $"Remove {account.Email} from this agenda?\n\n" +
            "Its access is not revoked at Google, so other apps using the same Google Cloud project " +
            "(omarchy-agenda) keep working. To revoke it everywhere, use myaccount.google.com/permissions.",
            "Google Agenda Tray", Native.MB_YESNO | Native.MB_ICONQUESTION | Native.MB_SETFOREGROUND);
        if (answer != Native.IDYES) return;
        try
        {
            await Task.Run(() => GoogleLogin.LogoutAsync(account, revoke: false, CancellationToken.None));
            AgendaService.ClearCache();
        }
        catch (Exception ex)
        {
            Log.Write($"removing {account.Email} failed: {ex.Message}");
        }
        Refresh(force: true);
    }

    private async void ShowCalendars()
    {
        try
        {
            var settings = Settings;
            var text = await Task.Run(() => AgendaService.DescribeCalendarsAsync(settings, CancellationToken.None));
            File.WriteAllText(AppPaths.CalendarsFile, text);
            Shell.Edit(AppPaths.CalendarsFile);
        }
        catch (Exception ex)
        {
            Log.Write($"listing calendars failed: {ex.Message}");
        }
    }

    public void Quit()
    {
        Log.Write("quit");
        Dispose();
        Application.Current.Exit();
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _clockTimer.Stop();
        _retryTimer.Stop();
        _loginCts?.Cancel();
        _tray.Dispose();
        _popup.CloseForGood();
    }
}

internal static class Hotkeys
{
    /// <summary>"Ctrl+Win+A" → modifiers and virtual key. Accepts Ctrl/Control, Alt,
    /// Shift, Win/Super, and a letter, digit or F1-F24.</summary>
    public static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win" or "super": modifiers |= Native.MOD_WIN; break;
                default:
                    var key = raw.ToUpperInvariant();
                    if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) vk = key[0];
                    else if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
                    else return false;
                    break;
            }
        }
        return vk != 0 && modifiers != 0;
    }
}
