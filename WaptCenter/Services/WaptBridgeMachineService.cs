using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WaptCenter.Models;

namespace WaptCenter.Services;

public sealed class WaptBridgeMachineService
{
    private const string MachinesBridgeScriptFileName = "wapt_package_machines_bridge.py";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string LastTechnicalDetails { get; private set; } = string.Empty;

    public async Task<List<WaptMachine>> GetMachinesForPackageAsync(
        WaptConfig config,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ValidateConfig(config, packageId);

        var pythonExecutablePath = ResolveExecutablePath(config.PythonExecutablePath);
        var bridgeScriptPath = ResolveMachineBridgeScriptPath(config);
        LastTechnicalDetails = string.Empty;

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(bridgeScriptPath) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(bridgeScriptPath);
        startInfo.ArgumentList.Add("--server-url");
        startInfo.ArgumentList.Add(config.ServerUrl);
        startInfo.ArgumentList.Add("--server-user");
        startInfo.ArgumentList.Add(config.ServerUser ?? string.Empty);
        startInfo.ArgumentList.Add("--server-password");
        startInfo.ArgumentList.Add(config.ServerPassword ?? string.Empty);
        startInfo.ArgumentList.Add("--client-cert");
        startInfo.ArgumentList.Add(UsePemClientCertificate(config)
            ? ResolveOptionalPath(ResolvePemCertificatePath(config))
            : string.Empty);
        startInfo.ArgumentList.Add("--client-key");
        startInfo.ArgumentList.Add(UsePemClientCertificate(config)
            ? ResolveOptionalPath(ResolvePemPrivateKeyPath(config))
            : string.Empty);
        startInfo.ArgumentList.Add("--client-pkcs12");
        startInfo.ArgumentList.Add(ResolvePath(config.Pkcs12Path));
        startInfo.ArgumentList.Add("--password");
        startInfo.ArgumentList.Add(config.CertPassword ?? string.Empty);
        startInfo.ArgumentList.Add("--ca-cert");
        startInfo.ArgumentList.Add(ResolveOptionalPath(config.CaCertPath));
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add(Math.Max(1, config.TimeoutSeconds).ToString());
        startInfo.ArgumentList.Add("--package-id");
        startInfo.ArgumentList.Add(packageId);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Impossible de demarrer le bridge machines Python WAPT.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var bridgeResponse = DeserializeBridgeResponse(stdout);

            LastTechnicalDetails = BuildTechnicalDetails(
                pythonExecutablePath,
                bridgeScriptPath,
                packageId,
                config.ServerUser,
                process.ExitCode,
                bridgeResponse?.TechnicalDetails,
                stderr,
                stdout,
                bridgeResponse is null);

            if (bridgeResponse is null)
            {
                throw new InvalidOperationException("Le bridge machines Python WAPT a retourne un JSON invalide.");
            }

            NormalizeMachineMetadata(bridgeResponse.Machines);

            if (process.ExitCode != 0 || !bridgeResponse.Success)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(bridgeResponse.Message)
                        ? "Le bridge machines Python WAPT a echoue."
                        : bridgeResponse.Message);
            }

            return bridgeResponse.Machines;
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
        catch (Exception exception)
        {
            if (string.IsNullOrWhiteSpace(LastTechnicalDetails))
            {
                LastTechnicalDetails = BuildTechnicalDetails(
                    pythonExecutablePath,
                    bridgeScriptPath,
                    packageId,
                    config.ServerUser,
                    GetExitCodeOrDefault(process),
                    null,
                    string.Empty,
                    exception.ToString(),
                    includeRawStdout: true);
            }

            throw;
        }
    }

    private static void ValidateConfig(WaptConfig config, string packageId)
    {
        if (config is null)
        {
            throw new InvalidOperationException("La configuration WAPT est absente.");
        }

        if (string.IsNullOrWhiteSpace(config.ServerUrl) ||
            !Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("L'URL du serveur WAPT est invalide.");
        }

        if (string.IsNullOrWhiteSpace(config.ServerUser) || string.IsNullOrWhiteSpace(config.ServerPassword))
        {
            throw new InvalidOperationException(
                "L'utilisateur et le mot de passe du serveur WAPT sont requis pour charger les machines d'un paquet.");
        }

        if (string.IsNullOrWhiteSpace(config.Pkcs12Path))
        {
            if (!UsePemClientCertificate(config))
            {
                throw new InvalidOperationException("Le chemin du certificat client PKCS12 ou PEM du bridge Python est vide.");
            }
        }

        if (UsePemClientCertificate(config))
        {
            if (!File.Exists(ResolveOptionalPath(ResolvePemCertificatePath(config))))
            {
                throw new InvalidOperationException("Le certificat client PEM/.crt du bridge Python est introuvable.");
            }

            if (!File.Exists(ResolveOptionalPath(ResolvePemPrivateKeyPath(config))))
            {
                throw new InvalidOperationException("La cle privee PEM du bridge Python est introuvable.");
            }
        }
        else if (!File.Exists(ResolvePath(config.Pkcs12Path)))
        {
            throw new InvalidOperationException("Le certificat PKCS12 du bridge Python est introuvable.");
        }

        if (config.VerifySsl)
        {
            if (string.IsNullOrWhiteSpace(config.CaCertPath))
            {
                throw new InvalidOperationException("Le chemin du certificat serveur attendu est vide.");
            }

            if (!File.Exists(ResolvePath(config.CaCertPath)))
            {
                throw new InvalidOperationException("Le certificat serveur attendu est introuvable.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.PythonExecutablePath))
        {
            throw new InvalidOperationException("Le chemin de l'executable Python du bridge est vide.");
        }

        var pythonExecutablePath = ResolveExecutablePath(config.PythonExecutablePath);
        if (Path.IsPathRooted(pythonExecutablePath) && !File.Exists(pythonExecutablePath))
        {
            throw new InvalidOperationException("L'executable Python du bridge est introuvable.");
        }

        if (!File.Exists(ResolveMachineBridgeScriptPath(config)))
        {
            throw new InvalidOperationException("Le script bridge Python des machines est introuvable.");
        }

        if (config.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Le timeout doit etre superieur a 0.");
        }

        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new InvalidOperationException("Le package_id a analyser est vide.");
        }
    }

    private static string ResolveMachineBridgeScriptPath(WaptConfig config)
    {
        var packageBridgeScriptPath = ResolvePath(config.BridgeScriptPath);
        var scriptDirectory = Path.GetDirectoryName(packageBridgeScriptPath);

        if (string.IsNullOrWhiteSpace(scriptDirectory))
        {
            return Path.Combine(AppContext.BaseDirectory, "WaptBridge", "scripts", MachinesBridgeScriptFileName);
        }

        return Path.Combine(scriptDirectory, MachinesBridgeScriptFileName);
    }

    private static string ResolvePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
    }

    private static string ResolveExecutablePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return configuredPath.Contains(Path.DirectorySeparatorChar) || configuredPath.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath))
            : configuredPath;
    }

    private static string ResolveOptionalPath(string configuredPath)
    {
        return string.IsNullOrWhiteSpace(configuredPath) ? string.Empty : ResolvePath(configuredPath);
    }

    private static bool UsePemClientCertificate(WaptConfig config)
    {
        return !string.IsNullOrWhiteSpace(config.PemPath);
    }

    private static string ResolvePemCertificatePath(WaptConfig config)
    {
        return string.IsNullOrWhiteSpace(config.ClientCertPath)
            ? config.PemPath
            : config.ClientCertPath;
    }

    private static string ResolvePemPrivateKeyPath(WaptConfig config)
    {
        return string.IsNullOrWhiteSpace(config.ClientKeyPath)
            ? config.PemPath
            : config.ClientKeyPath;
    }

    private static BridgeMachinesResponse? DeserializeBridgeResponse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BridgeMachinesResponse>(stdout, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void NormalizeMachineMetadata(IEnumerable<WaptMachine> machines)
    {
        foreach (var machine in machines)
        {
            machine.NormalizeMatchMetadata();
            machine.NormalizeComplianceMetadata();
            machine.NormalizeLocationMetadata();
        }
    }

    private static string BuildTechnicalDetails(
        string pythonExecutablePath,
        string bridgeScriptPath,
        string packageId,
        string? serverUser,
        int exitCode,
        string? bridgeTechnicalDetails,
        string stderr,
        string stdout,
        bool includeRawStdout)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Bridge strategy: .NET -> Python script -> native WAPT hosts inventory");
        builder.AppendLine($"Python executable: {pythonExecutablePath}");
        builder.AppendLine($"Bridge script: {bridgeScriptPath}");
        builder.AppendLine($"Package_id requested: {packageId}");
        builder.AppendLine($"Server inventory user: {serverUser}");
        builder.AppendLine($"Process exit code: {exitCode}");

        if (!string.IsNullOrWhiteSpace(bridgeTechnicalDetails))
        {
            builder.AppendLine();
            builder.AppendLine("Bridge technical details:");
            builder.AppendLine(bridgeTechnicalDetails.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            builder.AppendLine();
            builder.AppendLine("Bridge stderr:");
            builder.AppendLine(stderr.Trim());
        }

        if (includeRawStdout && !string.IsNullOrWhiteSpace(stdout))
        {
            builder.AppendLine();
            builder.AppendLine("Bridge stdout:");
            builder.AppendLine(stdout.Trim());
        }

        return builder.ToString().Trim();
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private static int GetExitCodeOrDefault(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }

    private sealed class BridgeMachinesResponse
    {
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public string TechnicalDetails { get; set; } = string.Empty;

        [JsonPropertyName("machines")]
        public List<WaptMachine> Machines { get; set; } = [];
    }
}
