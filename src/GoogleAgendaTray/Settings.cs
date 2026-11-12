using System.Text.Json;
using System.Text.Json.Serialization;

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
}
