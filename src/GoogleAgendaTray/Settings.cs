using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgendaTray.Google;

namespace AgendaTray;

/// <summary>
/// config.json. The first four keys are the ones omarchy-agenda reads from its own
/// config.json, with the same names, so that file can be copied over as it is.
/// </summary>
internal sealed class Settings
{
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> IgnoreCalendars { get; set; } = [];
    public bool HideDeclined { get; set; }
    public bool IncludeAllDay { get; set; } = true;

    public int RefreshIntervalSec { get; set; } = 180;
    public string Hotkey { get; set; } = "Ctrl+Win+A";
    /// <summary>Minutes before an event that the "about to start" card appears; 0 turns it off.</summary>
    public int ReminderMinutes { get; set; } = 2;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings Load()
    {
        var settings = new Settings();
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.ConfigFile), Json) ?? new Settings();
            }
            else
            {
                File.WriteAllText(AppPaths.ConfigFile, JsonSerializer.Serialize(settings, Json));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"invalid config.json, using the defaults: {ex.Message}");
            settings = new Settings();
        }

        settings.Aliases = new Dictionary<string, string>(settings.Aliases ?? [], StringComparer.OrdinalIgnoreCase);
        settings.IgnoreCalendars ??= [];
        settings.Hotkey ??= "";
        settings.RefreshIntervalSec = Math.Clamp(settings.RefreshIntervalSec, 30, 3600);
        settings.ReminderMinutes = Math.Clamp(settings.ReminderMinutes, 0, 60);
        return settings;
    }

    private static readonly JsonSerializerOptions WriteJson = new()
    {
        WriteIndented = true,
        // Aliases with accents stay readable instead of turning into \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Adds <paramref name="id"/> to ignore_calendars in config.json, or takes it out,
    /// and touches nothing else: the other keys stay as the user wrote them, including
    /// any only omarchy-agenda reads. The list edited is the one in the file, not the
    /// one in memory: the file may hold edits not reloaded yet, or a value Load could
    /// not read (it then fell back to the defaults, with an empty list), and rewriting
    /// the key from memory would drop every other id in it. Comments do not survive
    /// (System.Text.Json cannot keep them). A file that does not parse throws instead
    /// of being replaced: it holds the user's edits.
    /// </summary>
    public static void SetCalendarIgnored(string id, bool ignore)
    {
        var root = new JsonObject();
        if (File.Exists(AppPaths.ConfigFile))
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile), null, options) as JsonObject
                   ?? throw new JsonException("config.json is not a JSON object");
        }
        var ids = (root["ignore_calendars"] as JsonArray ?? new JsonArray())
            .Where(n => n?.GetValueKind() == JsonValueKind.String)
            .Select(n => n!.GetValue<string>())
            .Where(x => x != id)
            .ToList();
        if (ignore) ids.Add(id);
        root["ignore_calendars"] = new JsonArray(ids.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        Files.WriteAtomic(AppPaths.ConfigFile, root.ToJsonString(WriteJson));
    }
}
