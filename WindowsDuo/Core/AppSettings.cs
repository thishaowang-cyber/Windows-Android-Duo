using System.Text.Json;

namespace WindowsDuo.Core;

// Small persisted state.
public sealed class AppSettings
{
    public float Frost { get; set; } = 0.09f;
    /// <summary>Edge glow reach as a fraction of screen height.</summary>
    public float Glow { get; set; } = 0.06f;
    public float Strength { get; set; } = 1.0f;
    /// <summary>Phone's Wi-Fi address; the phone shows it once sending is enabled.</summary>
    public string PhoneHost { get; set; } = "";
    public int AnglePort { get; set; } = 45680;

    private static string PathFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsDuo", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile)) ?? new AppSettings();
        }
        catch { /* corrupt or unreadable: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(PathFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* persistence is best-effort */ }
    }
}
