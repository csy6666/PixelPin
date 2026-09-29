using System.Text.Json;
using PixelPin.Models;

namespace PixelPin.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public AppSettings Load()
    {
        AppPaths.EnsureDirectories();

        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions);
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // A broken local preference file should not prevent capture from starting.
        }

        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        AppPaths.EnsureDirectories();

        var temporary = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
    }

    public AppSettings Import(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("所选设置文件已不存在。", path);
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("设置文件不包含有效的 PixelPin 设置对象。");
        settings.Normalize();
        return settings;
    }

    public void Export(AppSettings settings, string path)
    {
        settings.Normalize();
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("请选择包含父文件夹的文件路径。");
        }

        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
