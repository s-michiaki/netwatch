using System.IO;
using System.Text.Json;

namespace NetWatch.Services;

/// <summary>%AppData%\NetWatch\settings.json に保存する設定。</summary>
public sealed class AppSettings
{
    public bool RecordAllProcesses { get; set; } = true;
    public List<string> RecordTargets { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetWatch", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* 壊れていたら既定値 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 保存できなくても動作は続ける */ }
    }
}
