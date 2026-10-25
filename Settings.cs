using System.Text.Json;

namespace FloatTube;

internal static class Paths
{
    public static readonly string DataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatTube");
}

/// <summary>A global hotkey combo expressed as Win32 MOD_* flags + a virtual key.</summary>
public sealed class HotkeyCombo
{
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    public uint Modifiers { get; set; } = ModControl | ModAlt;
    public uint Key { get; set; } = 0x46; // VK 'F'

    public static HotkeyCombo Default => new();

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModAlt) != 0)     parts.Add("Alt");
        if ((Modifiers & ModShift) != 0)   parts.Add("Shift");
        if ((Modifiers & ModWin) != 0)     parts.Add("Win");
        parts.Add(Enum.IsDefined(typeof(Keys), (int)Key) ? ((Keys)Key).ToString() : $"0x{Key:X}");
        return string.Join("+", parts);
    }
}

public sealed class AppSettings
{
    public bool AutoHideUi { get; set; } = true;
    public bool BlockAds { get; set; } = true;
    public HotkeyCombo ClickThrough { get; set; } = HotkeyCombo.Default;

    private static string FilePath => Path.Combine(Paths.DataRoot, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
