using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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

        var (events, errors, reauth) = await CollectAsync(day, settings, client, accounts, ct).ConfigureAwait(false);
        result.Events = events;
        result.Errors = errors;
        result.Reauth = reauth;
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
                return cached;
            }
        }
        return result;
    }

    private static async Task<(List<AgendaEvent>, List<string>, List<string>)> CollectAsync(
        DateOnly day, Settings settings, OAuthClient client, List<Account> accounts, CancellationToken ct)
    {
        var dayStart = LocalMidnight(day);
        var dayEnd = LocalMidnight(day.AddDays(1));
        var events = new ConcurrentBag<AgendaEvent>();
        var errors = new ConcurrentQueue<string>();
        var reauth = new ConcurrentQueue<string>();

        async Task Work(Account account)
        {
            var label = settings.Aliases.TryGetValue(account.Email, out var alias) && !string.IsNullOrWhiteSpace(alias)
                ? alias
                : account.Email.Split('@')[0];
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
                    errors.Enqueue($"{account.Email}: authorization expired, connect it again (L)");
                }
                else
                {
                    errors.Enqueue($"{account.Email}: {ex.Message}");
                }
                return;
            }

            List<JsonElement> calendars;
            try
            {
                calendars = await CalendarsAsync(token, settings, ct).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                errors.Enqueue($"{account.Email}: {ex.Message}");
                return;
            }

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
                    var name = cal.Str("summary") is { Length: > 0 } s ? s : cal.Str("id");
                    errors.Enqueue($"{account.Email} / {name}: {ex.Message}");
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
        foreach (var e in events.OrderBy(e => !e.AllDay).ThenBy(e => e.Start).ThenBy(e => e.Title, StringComparer.Ordinal))
        {
            if (seen.Add((e.Title, e.Start.ToUnixTimeSeconds(), e.Account))) unique.Add(e);
        }
        return (unique, errors.ToList(), reauth.Distinct().Order(StringComparer.Ordinal).ToList());
    }

    public static async Task<List<JsonElement>> CalendarsAsync(string token, Settings settings, CancellationToken ct)
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
            if (settings.IgnoreCalendars.Contains(item.Str("id"))) continue;
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

        var calendarName = calendar.Str("summaryOverride") is { Length: > 0 } o ? o
                         : calendar.Str("summary") is { Length: > 0 } s ? s
                         : calendarId;
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
                JoinUrl = JoinLinks.WithAuthUser(joinUrl, email),
                JoinKind = joinKind,
                HtmlLink = JoinLinks.WithAuthUser(ev.Str("htmlLink"), email),
                MyStatus = status,
                Organizer = organizer.Str("displayName") is { Length: > 0 } n ? n : organizer.Str("email"),
                OrganizerSelf = organizer.Bool("self"),
                AttendeeCount = ev.Arr("attendees").Count(),
            });
        }
        return list;
    }

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

    /// <summary>The "calendars" command: each account's visible calendars and their ids,
    /// which is what ignore_calendars in config.json takes.</summary>
    public static async Task<string> DescribeCalendarsAsync(Settings settings, CancellationToken ct)
    {
        var client = OAuthClient.Load();
        var sb = new StringBuilder();
        sb.AppendLine("Visible calendars per account. Copy an id into \"ignore_calendars\" in config.json to hide it.");
        sb.AppendLine("P = the account's primary calendar.");
        if (client == null)
        {
            sb.AppendLine().AppendLine("OAuth client not configured.");
            return sb.ToString();
        }
        foreach (var account in AccountStore.List())
        {
            sb.AppendLine().AppendLine(account.Email);
            try
            {
                var token = await GoogleApi.AccessTokenAsync(account, client, ct).ConfigureAwait(false);
                foreach (var cal in await CalendarsAsync(token, new Settings(), ct).ConfigureAwait(false))
                {
                    var name = cal.Str("summaryOverride") is { Length: > 0 } o ? o : cal.Str("summary");
                    var flag = cal.Bool("primary") ? "P" : " ";
                    var ignored = settings.IgnoreCalendars.Contains(cal.Str("id")) ? "  (ignored)" : "";
                    sb.AppendLine($"  {flag} {name}  <{cal.Str("id")}>{ignored}");
                }
            }
            catch (ApiException ex)
            {
                sb.AppendLine($"  ! {ex.Message}");
            }
        }
        return sb.ToString();
    }
}
