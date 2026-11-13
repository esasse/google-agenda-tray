using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AgendaTray.Google;

namespace AgendaTray.Agenda;

/// <summary>
/// Fetches one day across every authorized account and calendar. A port of the
/// helper's `today` command: same merge, same de-duplication, same per-day cache
/// that answers when the network does not.
/// </summary>
internal static class AgendaService
{
    private static readonly JsonSerializerOptions CacheJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private const string EventFields =
        "items(id,status,summary,location,description,start,end,htmlLink,hangoutLink," +
        "transparency,recurringEventId,organizer,attendees(self,responseStatus,email,displayName)," +
        "conferenceData(entryPoints(entryPointType,uri),conferenceSolution(name)))";

    public static async Task<AgendaResult> DayAsync(DateOnly day, Settings settings, CancellationToken ct)
    {
        var client = OAuthClient.Load();
        var accounts = AccountStore.List();
        var result = new AgendaResult
        {
            Date = Fmt.IsoDate(day),
            GeneratedAt = DateTimeOffset.Now,
            Accounts = accounts.Select(a => a.Email).ToList(),
        };

        if (client == null)
        {
            result.NeedsSetup = true;
            result.Errors = ["OAuth client not configured"];
            return result;
        }
        if (accounts.Count == 0)
        {
            result.NeedsLogin = true;
            result.Errors = ["No Google account connected"];
            return result;
        }

        var (events, errors, reauth, failed, calendars) = await CollectAsync(day, settings, client, accounts, ct).ConfigureAwait(false);
        result.Calendars = calendars;
        result.Events = events;
        result.Errors = errors;
        result.Reauth = reauth;
        result.FailedAccounts = failed;
        result.NeedsLogin = reauth.Count > 0 && reauth.Count == accounts.Count;
        result.Ok = errors.Count == 0 || events.Count > 0;

        if (result.Ok && errors.Count == 0)
        {
            SaveCache(result);
        }
        else if (events.Count == 0)
        {
            var cached = LoadCache(day);
            if (cached is { Events.Count: > 0 })
            {
                cached.Stale = true;
                cached.Errors = errors.Count > 0 ? errors : cached.Errors;
                cached.Reauth = reauth;
                // The calendar lists that did come back are newer than the cached ones.
                if (calendars.Count > 0) cached.Calendars = calendars;
                return cached;
            }
        }
        return result;
    }

    private static async Task<(List<AgendaEvent> Events, List<string> Errors, List<string> Reauth, List<string> Failed,
        List<CalendarInfo> Calendars)> CollectAsync(
        DateOnly day, Settings settings, OAuthClient client, List<Account> accounts, CancellationToken ct)
    {
        var dayStart = LocalMidnight(day);
        var dayEnd = LocalMidnight(day.AddDays(1));
        var events = new ConcurrentBag<AgendaEvent>();
        var calendarsByAccount = new ConcurrentDictionary<string, List<CalendarInfo>>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentQueue<string>();
        var reauth = new ConcurrentQueue<string>();
        // Accounts that brought back less than their whole day, for any reason.
        var failed = new ConcurrentQueue<string>();

        async Task Work(Account account)
        {
            var label = settings.Aliases.TryGetValue(account.Email, out var alias) && !string.IsNullOrWhiteSpace(alias)
                ? alias
                : account.Email.Split('@')[0];
            void Fail(string error)
            {
                errors.Enqueue(error);
                failed.Enqueue(account.Email);
            }
            string token;
            try
            {
                token = await GoogleApi.AccessTokenAsync(account, client, ct).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                if (ex.InvalidGrant)
                {
                    reauth.Enqueue(account.Email);
                    Fail($"{account.Email}: authorization expired, connect it again (L)");
                }
                else
                {
                    Fail($"{account.Email}: {ex.Message}");
                }
                return;
            }

            List<JsonElement> calendars;
            try
            {
                calendars = await CalendarsAsync(token, ct).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                Fail($"{account.Email}: {ex.Message}");
                return;
            }
            calendarsByAccount[account.Email] = calendars
                .Select(c => new CalendarInfo { Account = account.Email, Id = c.Str("id"), Name = CalendarName(c), Primary = c.Bool("primary") })
                .OrderBy(c => !c.Primary).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            calendars = calendars.Where(c => !settings.IgnoreCalendars.Contains(c.Str("id"))).ToList();

            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(calendars.Select(async cal =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    foreach (var e in await EventsAsync(account.Email, label, token, cal, dayStart, dayEnd, settings, ct).ConfigureAwait(false))
                        events.Add(e);
                }
                catch (ApiException ex)
                {
                    Fail($"{account.Email} / {CalendarName(cal)}: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }

        await Task.WhenAll(accounts.Select(Work)).ConfigureAwait(false);

        // The same meeting shows up once per calendar it is on (yours and a shared
        // team calendar, say); per account it is listed once.
        var seen = new HashSet<(string, long, string)>();
        var unique = new List<AgendaEvent>();
        // When copies differ, keep the one that opens as the account itself (you are
        // invited directly) over a shared calendar's, and pick the same one every time.
        foreach (var e in events.OrderBy(e => !e.AllDay).ThenBy(e => e.Start).ThenBy(e => e.Title, StringComparer.Ordinal)
                     .ThenBy(e => !string.Equals(e.OpenAs, e.Account, StringComparison.OrdinalIgnoreCase))
                     .ThenBy(e => e.OpenAs, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add((e.Title, e.Start.ToUnixTimeSeconds(), e.Account))) unique.Add(e);
        }
        // Sorted: the accounts are fetched in parallel, and finishing order would make
        // the same failures read differently from one refresh to the next.
        var calendarList = accounts.SelectMany(a => calendarsByAccount.TryGetValue(a.Email, out var l) ? l : []).ToList();
        return (unique, errors.Order(StringComparer.Ordinal).ToList(), reauth.Distinct().Order(StringComparer.Ordinal).ToList(),
            failed.Distinct().Order(StringComparer.Ordinal).ToList(), calendarList);
    }

    private static string CalendarName(JsonElement calendar) =>
        calendar.Str("summaryOverride") is { Length: > 0 } o ? o
        : calendar.Str("summary") is { Length: > 0 } s ? s
        : calendar.Str("id");

    /// <summary>The calendars Google Calendar's own sidebar shows, ignored ones included.</summary>
    private static async Task<List<JsonElement>> CalendarsAsync(string token, CancellationToken ct)
    {
        var url = $"{GoogleApi.CalendarApi}/users/me/calendarList?" + GoogleApi.Query(
            ("maxResults", "250"),
            ("minAccessRole", "reader"),
            ("showHidden", "false"),
            ("fields", "items(id,summary,summaryOverride,primary,selected,deleted,backgroundColor,accessRole)"));
        var payload = await GoogleApi.GetAsync(url, token, ct).ConfigureAwait(false);
        var list = new List<JsonElement>();
        foreach (var item in payload.Arr("items"))
        {
            if (item.Bool("deleted")) continue;
            // Hidden in Google Calendar's own sidebar: hidden here too.
            if (item.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.False) continue;
            list.Add(item);
        }
        return list;
    }

    private static async Task<List<AgendaEvent>> EventsAsync(string email, string label, string token, JsonElement calendar,
        DateTimeOffset dayStart, DateTimeOffset dayEnd, Settings settings, CancellationToken ct)
    {
        var calendarId = calendar.Str("id");
        var url = $"{GoogleApi.CalendarApi}/calendars/{Uri.EscapeDataString(calendarId)}/events?" + GoogleApi.Query(
            ("timeMin", Rfc3339(dayStart)),
            ("timeMax", Rfc3339(dayEnd)),
            ("singleEvents", "true"),
            ("orderBy", "startTime"),
            ("maxResults", "100"),
            ("fields", EventFields));
        var payload = await GoogleApi.GetAsync(url, token, ct).ConfigureAwait(false);

        var calendarName = CalendarName(calendar);
        var list = new List<AgendaEvent>();
        foreach (var ev in payload.Arr("items"))
        {
            if (ev.Str("status") == "cancelled") continue;
            var (start, allDay) = ParseWhen(ev.Obj("start"), dayStart);
            var (end, _) = ParseWhen(ev.Obj("end"), dayEnd);
            if (allDay && !settings.IncludeAllDay) continue;
            var status = MyStatus(ev);
            if (settings.HideDeclined && status == "declined") continue;
            var (joinUrl, joinKind) = JoinLinks.Find(ev);
            var organizer = ev.Obj("organizer");
            var openAs = OpenAs(calendarId, calendar.Bool("primary"), calendar.Str("accessRole"), email);

            list.Add(new AgendaEvent
            {
                Id = ev.Str("id"),
                Account = email,
                AccountLabel = label,
                CalendarId = calendarId,
                Calendar = calendarName,
                Primary = calendar.Bool("primary"),
                Color = calendar.Str("backgroundColor"),
                Title = ev.Str("summary") is { Length: > 0 } t ? t : "(no title)",
                Start = start,
                End = end,
                AllDay = allDay,
                Busy = ev.Str("transparency") != "transparent",
                Recurring = ev.Str("recurringEventId").Length > 0,
                Location = ev.Str("location"),
                OpenAs = openAs,
                JoinUrl = JoinLinks.WithAuthUser(joinUrl, openAs),
                JoinKind = joinKind,
                HtmlLink = JoinLinks.WithAuthUser(ev.Str("htmlLink"), openAs),
                MyStatus = status,
                Organizer = organizer.Str("displayName") is { Length: > 0 } n ? n : organizer.Str("email"),
                OrganizerSelf = organizer.Bool("self"),
                AttendeeCount = ev.Arr("attendees").Count(),
            });
        }
        return list;
    }

    /// <summary>
    /// The Google identity a link should open under. For an event on another of your
    /// own accounts' calendars, shared with the one connected here, that is the
    /// calendar's owner - the address actually invited: opened as the fetching
    /// account, the Meet link lands on "ask to join" and the event on a read-only copy.
    ///
    /// Only for calendars you can edit (owner or writer), which is how you share your
    /// own work calendar with your personal account. A colleague's calendar shared with
    /// you to read keeps the connected account: its owner is never signed in to your
    /// browser, and authuser=them would trade a working "ask to join" for a sign-in page.
    ///
    /// A user's calendar is named by their primary address, which is what authuser
    /// accepts (the "self" attendee can be an alias); group, holiday, resource and
    /// imported calendars live under calendar.google.com and belong to no one.
    /// </summary>
    private static string OpenAs(string calendarId, bool primary, string accessRole, string account) =>
        !primary && (accessRole is "owner" or "writer") && IsPersonAddress(calendarId) ? calendarId : account;

    private static bool IsPersonAddress(string address) =>
        address.Contains('@') && !address.EndsWith(".calendar.google.com", StringComparison.OrdinalIgnoreCase);

    private static string MyStatus(JsonElement ev)
    {
        foreach (var attendee in ev.Arr("attendees"))
            if (attendee.Bool("self")) return attendee.Str("responseStatus");
        return ev.Obj("organizer").Bool("self") ? "accepted" : "";
    }

    private static (DateTimeOffset When, bool AllDay) ParseWhen(JsonElement node, DateTimeOffset fallback)
    {
        var dateTime = node.Str("dateTime");
        if (dateTime.Length > 0 && DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return (dto.ToLocalTime(), false);
        var date = node.Str("date");
        if (date.Length > 0 && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return (LocalMidnight(d), true);
        return (fallback, true);
    }

    public static DateTimeOffset LocalMidnight(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static string Rfc3339(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------- cache

    private static string CachePath(DateOnly day) =>
        Path.Combine(AppPaths.CacheDir, Fmt.IsoDate(day) + ".json");

    public static AgendaResult? LoadCache(DateOnly day)
    {
        try
        {
            var path = CachePath(day);
            return File.Exists(path) ? JsonSerializer.Deserialize<AgendaResult>(File.ReadAllText(path), CacheJson) : null;
        }
        catch (Exception ex)
        {
            Log.Write($"could not read the cache: {ex.Message}");
            return null;
        }
    }

    private static void SaveCache(AgendaResult result)
    {
        try
        {
            AppPaths.Ensure();
            var day = DateOnly.ParseExact(result.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Files.WriteAtomic(CachePath(day), JsonSerializer.Serialize(result, CacheJson));
            // Only today's file is ever read back; a week of them is plenty of history.
            foreach (var file in Directory.EnumerateFiles(AppPaths.CacheDir, "*.json"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7)) File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"could not write the cache: {ex.Message}");
        }
    }

    public static void ClearCache()
    {
        try
        {
            if (!Directory.Exists(AppPaths.CacheDir)) return;
            foreach (var file in Directory.EnumerateFiles(AppPaths.CacheDir, "*.json")) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Write($"could not clear the cache: {ex.Message}");
        }
    }
}
