using System.Collections.Concurrent;
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

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);
    private static readonly ConcurrentDictionary<string, BridgeMachinesCacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task<BridgeMachinesCacheEntry>>> InFlightRequests = new(StringComparer.OrdinalIgnoreCase);

    private string _lastTechnicalDetails = string.Empty;

    public string LastTechnicalDetails
    {
        get => _lastTechnicalDetails;
        private set => _lastTechnicalDetails = value;
    }

    public async Task<List<WaptMachine>> GetMachinesForPackageAsync(
        WaptConfig config,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var result = await GetMachineResultForPackageAsync(config, packageId, cancellationToken).ConfigureAwait(false);
        return result.Machines;
    }

    public async Task<WaptBridgeMachinesResult> GetMachineResultForPackageAsync(
        WaptConfig config,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ValidateConfig(config, packageId);

        var cacheKey = BuildCacheKey(config, packageId);
        if (TryGetFreshCacheEntry(cacheKey, out var cacheEntry, out var cacheAge))
        {
            var technicalDetails = BuildCacheTechnicalDetails(
                cacheEntry.TechnicalDetails,
                "memory-hit",
                cacheAge,
                TimeSpan.Zero);
            LastTechnicalDetails = technicalDetails;

            return new WaptBridgeMachinesResult(CloneMachines(cacheEntry.Machines), technicalDetails);
        }

        var waitStopwatch = Stopwatch.StartNew();
        var createdRequest = new Lazy<Task<BridgeMachinesCacheEntry>>(
            () => ObserveFaults(ExecuteMachinesBridgeAsync(config, packageId, cacheKey)),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var activeRequest = InFlightRequests.GetOrAdd(cacheKey, createdRequest);
        var joinedInFlightRequest = !ReferenceEquals(activeRequest, createdRequest);

        try
        {
            var completedEntry = await activeRequest.Value.WaitAsync(cancellationToken);
            waitStopwatch.Stop();

            var technicalDetails = BuildCacheTechnicalDetails(
                completedEntry.TechnicalDetails,
                joinedInFlightRequest ? "shared-inflight" : "memory-miss",
                DateTimeOffset.UtcNow - completedEntry.CreatedAtUtc,
                waitStopwatch.Elapsed);
            LastTechnicalDetails = technicalDetails;

            return new WaptBridgeMachinesResult(CloneMachines(completedEntry.Machines), technicalDetails);
        }
        catch (BridgeMachinesRequestException exception)
        {
            LastTechnicalDetails = exception.TechnicalDetails;
            throw new WaptBridgeMachinesException(exception.Message, exception.TechnicalDetails, exception);
        }
        finally
        {
            if (ReferenceEquals(activeRequest, createdRequest))
            {
                InFlightRequests.TryRemove(cacheKey, out _);
            }
        }
    }

    private static async Task<BridgeMachinesCacheEntry> ExecuteMachinesBridgeAsync(
        WaptConfig config,
        string packageId,
        string cacheKey)
    {
        var pythonExecutablePath = ResolveExecutablePath(config.PythonExecutablePath);
        var bridgeScriptPath = ResolveMachineBridgeScriptPath(config);
        var executionStopwatch = Stopwatch.StartNew();

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

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var bridgeResponse = DeserializeBridgeResponse(stdout);

            executionStopwatch.Stop();

            if (bridgeResponse is not null)
            {
                bridgeResponse.Machines ??= [];
            }

            var technicalDetails = BuildTechnicalDetails(
                pythonExecutablePath,
                bridgeScriptPath,
                packageId,
                config.ServerUser,
                process.ExitCode,
                bridgeResponse?.Success,
                bridgeResponse?.Message,
                bridgeResponse?.Machines?.Count,
                bridgeResponse?.TechnicalDetails,
                stderr,
                stdout,
                bridgeResponse is null,
                executionStopwatch.Elapsed);

            if (bridgeResponse is null)
            {
                throw new BridgeMachinesRequestException(
                    "Le bridge machines Python WAPT a retourne un JSON invalide.",
                    technicalDetails);
            }

            NormalizeMachineMetadata(bridgeResponse.Machines);

            if (process.ExitCode != 0 || !bridgeResponse.Success)
            {
                throw new BridgeMachinesRequestException(
                    string.IsNullOrWhiteSpace(bridgeResponse.Message)
                        ? "Le bridge machines Python WAPT a echoue."
                        : bridgeResponse.Message,
                    technicalDetails);
            }

            var cacheEntry = new BridgeMachinesCacheEntry(
                CloneMachines(bridgeResponse.Machines),
                technicalDetails,
                DateTimeOffset.UtcNow);

            Cache[cacheKey] = cacheEntry;
            return cacheEntry;
        }
        catch (BridgeMachinesRequestException)
        {
            throw;
        }
        catch (Exception exception)
        {
            executionStopwatch.Stop();

            var technicalDetails = BuildTechnicalDetails(
                pythonExecutablePath,
                bridgeScriptPath,
                packageId,
                config.ServerUser,
                GetExitCodeOrDefault(process),
                null,
                null,
                null,
                null,
                string.Empty,
                exception.ToString(),
                includeRawStdout: true,
                executionStopwatch.Elapsed);

            throw new BridgeMachinesRequestException(
                string.IsNullOrWhiteSpace(exception.Message)
                    ? "Le bridge machines Python WAPT a echoue."
                    : exception.Message,
                technicalDetails,
                exception);
        }
    }

    private static bool TryGetFreshCacheEntry(
        string cacheKey,
        out BridgeMachinesCacheEntry cacheEntry,
        out TimeSpan cacheAge)
    {
        if (Cache.TryGetValue(cacheKey, out var existingEntry) && existingEntry is not null)
        {
            cacheEntry = existingEntry;
            cacheAge = DateTimeOffset.UtcNow - cacheEntry.CreatedAtUtc;
            if (cacheAge <= CacheTtl)
            {
                return true;
            }

            Cache.TryRemove(cacheKey, out _);
        }

        cacheEntry = default!;
        cacheAge = TimeSpan.Zero;
        return false;
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
        bool? bridgeSuccess,
        string? bridgeMessage,
        int? machineCount,
        string? bridgeTechnicalDetails,
        string stderr,
        string stdout,
        bool includeRawStdout,
        TimeSpan executionDuration)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Bridge strategy: .NET -> Python script -> native WAPT hosts inventory");
        builder.AppendLine($"Python executable: {pythonExecutablePath}");
        builder.AppendLine($"Bridge script: {bridgeScriptPath}");
        builder.AppendLine($"Package_id requested: {packageId}");
        builder.AppendLine($"Server inventory user: {serverUser}");
        builder.AppendLine($"Process exit code: {exitCode}");
        builder.AppendLine($"Bridge execution duration: {FormatDuration(executionDuration)}");
        builder.AppendLine($"Machine bridge response success: {FormatNullableBoolean(bridgeSuccess)}");
        builder.AppendLine($"Machine bridge response message: {FormatValueOrPlaceholder(bridgeMessage)}");
        builder.AppendLine($"Machine bridge returned machine count: {machineCount?.ToString() ?? "<unknown>"}");

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

    private static string BuildCacheTechnicalDetails(
        string technicalDetails,
        string cacheStatus,
        TimeSpan cacheAge,
        TimeSpan waitDuration)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Cache TTL: {FormatDuration(CacheTtl)}");
        builder.AppendLine($"Cache status: {cacheStatus}");

        if (cacheAge > TimeSpan.Zero)
        {
            builder.AppendLine($"Cached response age: {FormatDuration(cacheAge)}");
        }

        if (waitDuration > TimeSpan.Zero)
        {
            builder.AppendLine($"Request wait duration: {FormatDuration(waitDuration)}");
        }

        if (!string.IsNullOrWhiteSpace(technicalDetails))
        {
            builder.AppendLine();
            builder.AppendLine(technicalDetails.Trim());
        }

        return builder.ToString().Trim();
    }

    private static string BuildCacheKey(WaptConfig config, string packageId)
    {
        return string.Join("|", new[]
        {
            NormalizeKeyPart(packageId),
            NormalizeKeyPart(config.ServerUrl),
            NormalizeKeyPart(config.ServerUser),
            NormalizeKeyPart(config.ClientCertPath),
            NormalizeKeyPart(config.ClientKeyPath),
            NormalizeKeyPart(config.PemPath),
            NormalizeKeyPart(config.Pkcs12Path),
            NormalizeKeyPart(config.CaCertPath),
            NormalizeKeyPart(config.PythonExecutablePath),
            NormalizeKeyPart(config.BridgeScriptPath),
            config.VerifySsl ? "ssl-on" : "ssl-off",
            Math.Max(1, config.TimeoutSeconds).ToString()
        });
    }

    private static string NormalizeKeyPart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static List<WaptMachine> CloneMachines(IEnumerable<WaptMachine> machines)
    {
        return machines.Select(machine => new WaptMachine
        {
            Hostname = machine.Hostname,
            Fqdn = machine.Fqdn,
            PackageId = machine.PackageId,
            InstalledVersion = machine.InstalledVersion,
            MatchType = machine.MatchType,
            IsExactInstall = machine.IsExactInstall,
            ComplianceStatus = machine.ComplianceStatus,
            Status = machine.Status,
            LastSeen = machine.LastSeen,
            OrganizationalUnit = machine.OrganizationalUnit,
            OuPath = machine.OuPath,
            Organization = machine.Organization,
            OrganizationDisplay = machine.OrganizationDisplay,
            Groups = [.. machine.Groups],
            Uuid = machine.Uuid
        }).ToList();
    }

    private static Task<T> ObserveFaults<T>(Task<T> task)
    {
        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return $"{duration.TotalMilliseconds:0} ms";
    }

    private static string FormatNullableBoolean(bool? value)
    {
        return value.HasValue ? value.Value.ToString().ToLowerInvariant() : "<unknown>";
    }

    private static string FormatValueOrPlaceholder(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
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

    private sealed record BridgeMachinesCacheEntry(
        List<WaptMachine> Machines,
        string TechnicalDetails,
        DateTimeOffset CreatedAtUtc);

    public sealed record WaptBridgeMachinesResult(
        List<WaptMachine> Machines,
        string TechnicalDetails);

    public sealed class WaptBridgeMachinesException : InvalidOperationException
    {
        public WaptBridgeMachinesException(string message, string technicalDetails, Exception? innerException = null)
            : base(message, innerException)
        {
            TechnicalDetails = technicalDetails;
        }

        public string TechnicalDetails { get; }
    }

    private sealed class BridgeMachinesRequestException : Exception
    {
        public BridgeMachinesRequestException(string message, string technicalDetails, Exception? innerException = null)
            : base(message, innerException)
        {
            TechnicalDetails = technicalDetails;
        }

        public string TechnicalDetails { get; }
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