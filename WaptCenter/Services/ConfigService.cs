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
            return ApplyDefaults(new WaptConfig());
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            return ApplyDefaults(JsonSerializer.Deserialize<WaptConfig>(json, SerializerOptions) ?? new WaptConfig());
        }
        catch (IOException)
        {
            return ApplyDefaults(new WaptConfig());
        }
        catch (JsonException)
        {
            return ApplyDefaults(new WaptConfig());
        }
    }

    public void Save(WaptConfig config)
    {
        config = ApplyDefaults(config);
        var directoryPath = Path.GetDirectoryName(_configPath);

        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(_configPath, json);
    }

    private static WaptConfig ApplyDefaults(WaptConfig config)
    {
        config.PythonExecutablePath = string.IsNullOrWhiteSpace(config.PythonExecutablePath)
            ? ResolveDefaultPythonExecutablePath()
            : config.PythonExecutablePath;

        config.BridgeScriptPath = string.IsNullOrWhiteSpace(config.BridgeScriptPath)
            ? ResolveDefaultBridgeScriptPath()
            : config.BridgeScriptPath;

        return config;
    }

    private static string ResolveDefaultPythonExecutablePath()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(programFilesX86, "wapt", "waptpython.exe"),
            Path.Combine(programFilesX86, "wapt", "python.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "python";
    }

    private static string ResolveDefaultBridgeScriptPath()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "WaptBridge", "scripts", "wapt_packages_bridge.py");
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "WaptCenter.WaptBridge",
            "scripts",
            "wapt_packages_bridge.py"));

        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        if (File.Exists(sourcePath))
        {
            return sourcePath;
        }

        return outputPath;
    }
}