using System.IO;
using System.Text.Json;
using WaptCenter.Models;

namespace WaptCenter.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _configPath;

    public ConfigService()
    {
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WaptCenter");

        _configPath = Path.Combine(appDataDirectory, "config.json");
    }

    public WaptConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            return new WaptConfig();
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<WaptConfig>(json, SerializerOptions) ?? new WaptConfig();
        }
        catch (IOException)
        {
            return new WaptConfig();
        }
        catch (JsonException)
        {
            return new WaptConfig();
        }
    }

    public void Save(WaptConfig config)
    {
        var directoryPath = Path.GetDirectoryName(_configPath);

        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(_configPath, json);
    }
}
