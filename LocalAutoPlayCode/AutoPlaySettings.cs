using System.Text.Json;
using Godot;

namespace LocalAutoPlay;

internal static class AutoPlaySettings
{
    private static bool _loaded;
    private static bool _fullAutoEnabled;

    internal static bool FullAutoEnabled
    {
        get
        {
            LoadOnce();
            return _fullAutoEnabled;
        }
        set
        {
            LoadOnce();
            if (_fullAutoEnabled == value) return;
            _fullAutoEnabled = value;
            Save();
        }
    }

    private static string SettingsPath =>
        ProjectSettings.GlobalizePath("user://local_auto_play_settings.json");

    private static void LoadOnce()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (document.RootElement.TryGetProperty("fullAutoEnabled", out JsonElement enabled))
                _fullAutoEnabled = enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            MainFile.Log.Warn($"[LocalAutoPlay] settings load failed: {ex.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                fullAutoEnabled = _fullAutoEnabled
            }));
            File.Move(temporary, path, true);
        }
        catch (Exception ex)
        {
            MainFile.Log.Warn($"[LocalAutoPlay] settings save failed: {ex.Message}");
        }
    }
}
