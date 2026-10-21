using System.Text.Json;
using System.Text.Json.Serialization;
using Clotch.Core.Diagnostics;
using Clotch.Core.Layout;

namespace Clotch.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Notch horizontal centre in physical screen pixels; null = top-centre of primary monitor.</summary>
    public int? NotchCenterX { get; set; }

    /// <summary>Notch top edge in physical screen pixels.</summary>
    public int? NotchTop { get; set; }

    /// <summary>Docked edge; null in settings written before side docking existed (derived from NotchTop then).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NotchEdge>))]
    public NotchEdge? NotchEdge { get; set; }

    /// <summary>Vertical centre of the side-docked strip in physical screen pixels.</summary>
    public int? NotchCenterY { get; set; }

    public bool NotchHidden { get; set; }
    public int PollMinutes { get; set; } = 5;
    public bool AutoHideFullscreen { get; set; } = true;
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SettingsJsonContext : JsonSerializerContext;

public sealed class SettingsStore(string path)
{
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clotch", "settings.json");

    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AppSettings();
            var s = JsonSerializer.Deserialize(File.ReadAllText(Path), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            s.PollMinutes = Math.Clamp(s.PollMinutes, 2, 60);
            return s;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"settings unreadable, using defaults: {ex.GetType().Name}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"settings save failed: {ex.GetType().Name}");
        }
    }
}
