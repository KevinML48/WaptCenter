using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WaptCenter.Models;

namespace WaptCenter.Services;

public sealed class WaptPackageService
{
    private const int ResponseExcerptLength = 320;
    private static readonly string[] ClientCertificateRejectedMarkers =
    [
        "The SSL certificate error",
        "No required SSL certificate was sent"
    ];

    private enum PackageAttemptFailureKind
    {
        None,
        ClientCertificateRejected,
        HttpStatus,
        InvalidContent,
        InvalidJson,
        Network,
        Timeout
    }

    private sealed class PackageLoadAttempt
    {
        public string Strategy { get; init; } = string.Empty;

        public Uri Url { get; init; } = null!;

        public HttpStatusCode? StatusCode { get; init; }

        public string ResponseType { get; init; } = "unknown";

        public string ResponseExcerpt { get; init; } = string.Empty;

        public string FailureMessage { get; init; } = string.Empty;

        public PackageAttemptFailureKind FailureKind { get; init; }

        public List<WaptPackage> Packages { get; init; } = [];

        public bool Success => FailureKind == PackageAttemptFailureKind.None;
    }

    public string LastTechnicalDetails { get; private set; } = string.Empty;

    public async Task<List<WaptPackage>> GetCd48PackagesAsync(WaptConfig config)
    {
        ValidateConfig(config);
        LastTechnicalDetails = string.Empty;

        WaptHttpClientContext? httpClientContext = null;
        var attempts = new List<PackageLoadAttempt>();

        try
        {
            httpClientContext = WaptHttpClientFactory.Create(config);
            var serverUri = new Uri(config.ServerUrl, UriKind.Absolute);
            var packages = await LoadPackagesWithFallbackAsync(httpClientContext, serverUri, attempts);

            if (packages is null)
            {
                throw BuildFinalFailure(httpClientContext.SslValidationState.TechnicalDetails, attempts);
            }

            var filteredPackages = packages
                .Where(package =>
                    !string.IsNullOrWhiteSpace(package.PackageId) &&
                    package.PackageId.Contains("cd48", StringComparison.OrdinalIgnoreCase))
                .ToList();

            LastTechnicalDetails = BuildTechnicalDetails(httpClientContext.SslValidationState.TechnicalDetails, attempts);
            return filteredPackages;
        }
        catch (WaptClientCertificateException exception)
        {
            LastTechnicalDetails = BuildTechnicalDetails(exception.TechnicalDetails, attempts);
            throw new InvalidOperationException(exception.Message, exception);
        }
        catch (CryptographicException exception)
        {
            LastTechnicalDetails = BuildTechnicalDetails(httpClientContext?.SslValidationState.TechnicalDetails, attempts, exception.ToString());
            throw new InvalidOperationException(BuildInvalidClientCertificateMessage(config, exception), exception);
        }
        catch (FileNotFoundException exception)
        {
            LastTechnicalDetails = BuildTechnicalDetails(httpClientContext?.SslValidationState.TechnicalDetails, attempts, exception.ToString());
            throw new InvalidOperationException(BuildMissingFileMessage(config, exception.FileName), exception);
        }
        finally
        {
            httpClientContext?.Dispose();
        }
    }

    private async Task<List<WaptPackage>?> LoadPackagesWithFallbackAsync(
        WaptHttpClientContext httpClientContext,
        Uri serverUri,
        List<PackageLoadAttempt> attempts)
    {
        foreach (var candidate in GetRepositoryIndexCandidates(serverUri))
        {
            httpClientContext.SetRequestTechnicalDetails(candidate);
            var attempt = await LoadFromRepositoryIndexAsync(httpClientContext.Client, candidate);
            attempts.Add(attempt);

            if (attempt.Success)
            {
                return attempt.Packages;
            }

            if (attempt.FailureKind is PackageAttemptFailureKind.ClientCertificateRejected or PackageAttemptFailureKind.Network or PackageAttemptFailureKind.Timeout)
            {
                return null;
            }
        }

        foreach (var candidate in GetApiCandidates(serverUri))
        {
            httpClientContext.SetRequestTechnicalDetails(candidate);
            var attempt = await LoadFromApiAsync(httpClientContext.Client, candidate);
            attempts.Add(attempt);

            if (attempt.Success)
            {
                return attempt.Packages;
            }

            if (attempt.FailureKind is PackageAttemptFailureKind.ClientCertificateRejected or PackageAttemptFailureKind.Network or PackageAttemptFailureKind.Timeout)
            {
                return null;
            }
        }

        return null;
    }

    private static void ValidateConfig(WaptConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException("La configuration WAPT est absente.");
        }

        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            throw new InvalidOperationException("L'URL du serveur WAPT est vide.");
        }

        if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("L'URL du serveur WAPT est invalide.");
        }

        if (string.IsNullOrWhiteSpace(config.PemPath) && string.IsNullOrWhiteSpace(config.Pkcs12Path))
        {
            throw new InvalidOperationException("Le chemin du certificat client PKCS12 ou PEM est vide.");
        }

        if (string.IsNullOrWhiteSpace(config.CaCertPath) && config.VerifySsl)
        {
            throw new InvalidOperationException("Le chemin du certificat serveur attendu est vide.");
        }

        if (config.TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Le timeout doit etre superieur a 0.");
        }
    }

    private static string BuildInvalidClientCertificateMessage(WaptConfig config, CryptographicException exception)
    {
        if (LooksLikeInvalidPassword(exception))
        {
            return "Impossible de charger le certificat PKCS12. Mot de passe incorrect ou fichier invalide.";
        }

        return string.IsNullOrWhiteSpace(config.PemPath)
            ? "Impossible de charger le certificat PKCS12. Mot de passe incorrect ou fichier invalide."
            : "Impossible de charger le certificat client PEM configure. Fichier invalide.";
    }

    private static string BuildMissingFileMessage(WaptConfig config, string? fileName)
    {
        if (MatchesConfiguredPath(fileName, config.CaCertPath))
        {
            return "Le certificat serveur attendu est introuvable.";
        }

        if (MatchesConfiguredPath(fileName, config.Pkcs12Path))
        {
            return "Le certificat PKCS12 est introuvable.";
        }

        if (MatchesConfiguredPath(fileName, config.ClientCertPath))
        {
            return "Le certificat client PEM/.crt est introuvable.";
        }

        if (MatchesConfiguredPath(fileName, config.ClientKeyPath))
        {
            return "La cle privee PEM du certificat client est introuvable.";
        }

        if (MatchesConfiguredPath(fileName, config.PemPath))
        {
            return "Le fichier PEM du certificat client est introuvable.";
        }

        return "Le certificat client configure est introuvable.";
    }

    private static bool MatchesConfiguredPath(string? actualPath, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath) || string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(actualPath),
                Path.GetFullPath(configuredPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(actualPath, configuredPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool LooksLikeInvalidPassword(CryptographicException exception)
    {
        var message = exception.ToString();

        return message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("mot de passe", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("network password", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Uri> GetRepositoryIndexCandidates(Uri serverUri)
    {
        var candidates = new List<Uri>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originUri = BuildOriginUri(serverUri);
        var baseRepositoryUri = EnsureTrailingSlash(serverUri);

        AddCandidate(candidates, seenUrls, new Uri(originUri, "wapt/Packages"));
        AddCandidate(candidates, seenUrls, new Uri(originUri, "wapt/Packages.gz"));
        AddCandidate(candidates, seenUrls, new Uri(baseRepositoryUri, "Packages"));
        AddCandidate(candidates, seenUrls, new Uri(baseRepositoryUri, "Packages.gz"));

        return candidates;
    }

    private static IEnumerable<Uri> GetApiCandidates(Uri serverUri)
    {
        var originUri = BuildOriginUri(serverUri);
        return [new Uri(originUri, "api/v1/packages")];
    }

    private static async Task<PackageLoadAttempt> LoadFromRepositoryIndexAsync(HttpClient client, Uri requestUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var responseType = GetResponseType(response);
            var responseBytes = await response.Content.ReadAsByteArrayAsync();

            if (!response.IsSuccessStatusCode)
            {
                var responseExcerpt = BuildBinaryExcerpt(responseBytes, responseType);

                if (IsClientCertificateRejectedResponse(response.StatusCode, TryGetTextResponse(responseBytes, responseType), responseExcerpt))
                {
                    return CreateFailedAttempt(
                        "RepositoryIndex",
                        requestUri,
                        response.StatusCode,
                        responseType,
                        responseExcerpt,
                        PackageAttemptFailureKind.ClientCertificateRejected,
                        WaptHttpClientFactory.ClientCertificateRejectedByServerMessage);
                }

                return CreateFailedAttempt(
                    "RepositoryIndex",
                    requestUri,
                    response.StatusCode,
                    responseType,
                    responseExcerpt,
                    PackageAttemptFailureKind.HttpStatus,
                    BuildHttpFailureMessage(response.StatusCode, true));
            }

            if (!TryReadRepositoryIndexText(responseBytes, responseType, out var repositoryText, out var parsedResponseType, out var excerpt, out var parseError))
            {
                return CreateFailedAttempt(
                    "RepositoryIndex",
                    requestUri,
                    response.StatusCode,
                    parsedResponseType,
                    excerpt,
                    PackageAttemptFailureKind.InvalidContent,
                    parseError);
            }

            var packages = ParseRepositoryPackages(repositoryText);

            if (packages.Count == 0)
            {
                return CreateFailedAttempt(
                    "RepositoryIndex",
                    requestUri,
                    response.StatusCode,
                    parsedResponseType,
                    excerpt,
                    PackageAttemptFailureKind.InvalidContent,
                    "Le contenu de l'index du depot WAPT est invalide ou vide.");
            }

            return CreateSuccessfulAttempt(
                "RepositoryIndex",
                requestUri,
                response.StatusCode,
                parsedResponseType,
                excerpt,
                packages);
        }
        catch (TaskCanceledException exception)
        {
            return CreateFailedAttempt(
                "RepositoryIndex",
                requestUri,
                null,
                "timeout",
                exception.Message,
                PackageAttemptFailureKind.Timeout,
                "Le chargement de l'index du depot WAPT a expire.");
        }
        catch (HttpRequestException exception)
        {
            return CreateFailedAttempt(
                "RepositoryIndex",
                requestUri,
                exception.StatusCode,
                "network-error",
                exception.Message,
                PackageAttemptFailureKind.Network,
                "Le serveur WAPT est inaccessible pendant la lecture de l'index du depot.");
        }
    }

    private static async Task<PackageLoadAttempt> LoadFromApiAsync(HttpClient client, Uri requestUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var responseType = GetResponseType(response);
            var responseText = await response.Content.ReadAsStringAsync();
            var excerpt = BuildTextExcerpt(responseText);

            if (!response.IsSuccessStatusCode)
            {
                if (IsClientCertificateRejectedResponse(response.StatusCode, responseText, excerpt))
                {
                    return CreateFailedAttempt(
                        "ApiFallback",
                        requestUri,
                        response.StatusCode,
                        responseType,
                        excerpt,
                        PackageAttemptFailureKind.ClientCertificateRejected,
                        WaptHttpClientFactory.ClientCertificateRejectedByServerMessage);
                }

                return CreateFailedAttempt(
                    "ApiFallback",
                    requestUri,
                    response.StatusCode,
                    responseType,
                    excerpt,
                    PackageAttemptFailureKind.HttpStatus,
                    BuildHttpFailureMessage(response.StatusCode, false));
            }

            try
            {
                using var jsonDocument = JsonDocument.Parse(responseText);
                var packages = ParseJsonPackages(jsonDocument.RootElement);

                return CreateSuccessfulAttempt(
                    "ApiFallback",
                    requestUri,
                    response.StatusCode,
                    responseType,
                    excerpt,
                    packages);
            }
            catch (JsonException)
            {
                return CreateFailedAttempt(
                    "ApiFallback",
                    requestUri,
                    response.StatusCode,
                    responseType,
                    excerpt,
                    PackageAttemptFailureKind.InvalidJson,
                    "La reponse JSON de l'endpoint de secours est invalide.");
            }
        }
        catch (TaskCanceledException exception)
        {
            return CreateFailedAttempt(
                "ApiFallback",
                requestUri,
                null,
                "timeout",
                exception.Message,
                PackageAttemptFailureKind.Timeout,
                "Le chargement de l'endpoint de secours des paquets a expire.");
        }
        catch (HttpRequestException exception)
        {
            return CreateFailedAttempt(
                "ApiFallback",
                requestUri,
                exception.StatusCode,
                "network-error",
                exception.Message,
                PackageAttemptFailureKind.Network,
                "Le serveur WAPT est inaccessible pendant l'appel de l'endpoint de secours.");
        }
    }

    private static PackageLoadAttempt CreateSuccessfulAttempt(
        string strategy,
        Uri requestUri,
        HttpStatusCode statusCode,
        string responseType,
        string excerpt,
        List<WaptPackage> packages)
    {
        return new PackageLoadAttempt
        {
            Strategy = strategy,
            Url = requestUri,
            StatusCode = statusCode,
            ResponseType = responseType,
            ResponseExcerpt = excerpt,
            Packages = packages,
            FailureKind = PackageAttemptFailureKind.None
        };
    }

    private static PackageLoadAttempt CreateFailedAttempt(
        string strategy,
        Uri requestUri,
        HttpStatusCode? statusCode,
        string responseType,
        string excerpt,
        PackageAttemptFailureKind failureKind,
        string failureMessage)
    {
        return new PackageLoadAttempt
        {
            Strategy = strategy,
            Url = requestUri,
            StatusCode = statusCode,
            ResponseType = responseType,
            ResponseExcerpt = excerpt,
            FailureKind = failureKind,
            FailureMessage = failureMessage
        };
    }

    private static List<WaptPackage> ParseRepositoryPackages(string repositoryIndexText)
    {
        var packages = new List<WaptPackage>();
        var currentFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        var normalizedText = repositoryIndexText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        foreach (var rawLine in normalizedText.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                AppendPackage(currentFields, packages);
                currentFields.Clear();
                currentKey = null;
                continue;
            }

            if ((rawLine[0] == ' ' || rawLine[0] == '\t') && currentKey is not null)
            {
                currentFields[currentKey] = $"{currentFields[currentKey]}\n{rawLine.Trim()}";
                continue;
            }

            var separatorIndex = rawLine.IndexOf(':');

            if (separatorIndex <= 0)
            {
                continue;
            }

            currentKey = rawLine[..separatorIndex].Trim();
            currentFields[currentKey] = rawLine[(separatorIndex + 1)..].Trim();
        }

        AppendPackage(currentFields, packages);
        return packages;
    }

    private static void AppendPackage(
        Dictionary<string, string> currentFields,
        List<WaptPackage> packages)
    {
        if (currentFields.Count == 0)
        {
            return;
        }

        var packageName = GetString(currentFields, "package", "name");

        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        packages.Add(new WaptPackage
        {
            Name = packageName,
            Version = GetString(currentFields, "version") ?? string.Empty,
            Description = GetString(currentFields, "description", "summary") ?? string.Empty,
            Architecture = GetString(currentFields, "architecture", "arch") ?? string.Empty,
            Maturity = GetString(currentFields, "maturity") ?? string.Empty
        });
    }

    private static List<WaptPackage> ParseJsonPackages(JsonElement rootElement)
    {
        var packagesArray = ExtractPackagesArray(rootElement);
        var packages = new List<WaptPackage>();

        foreach (var packageElement in packagesArray.EnumerateArray())
        {
            if (packageElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var package = new WaptPackage
            {
                Name = GetString(packageElement, "name", "package") ?? string.Empty,
                Version = GetString(packageElement, "version") ?? string.Empty,
                Description = GetString(packageElement, "description", "locale_description", "summary") ?? string.Empty,
                Architecture = GetString(packageElement, "architecture", "arch") ?? string.Empty,
                Maturity = GetString(packageElement, "maturity") ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(package.Name))
            {
                packages.Add(package);
            }
        }

        return packages;
    }

    private static JsonElement ExtractPackagesArray(JsonElement rootElement)
    {
        if (rootElement.ValueKind == JsonValueKind.Array)
        {
            return rootElement;
        }

        if (rootElement.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPackagesArray(rootElement, out var packagesArray))
            {
                return packagesArray;
            }

            foreach (var propertyName in new[] { "result", "data" })
            {
                if (rootElement.TryGetProperty(propertyName, out var nestedElement))
                {
                    if (nestedElement.ValueKind == JsonValueKind.Array)
                    {
                        return nestedElement;
                    }

                    if (nestedElement.ValueKind == JsonValueKind.Object && TryGetPackagesArray(nestedElement, out packagesArray))
                    {
                        return packagesArray;
                    }
                }
            }
        }

        throw new JsonException("Unable to find a package array in the API response.");
    }

    private static bool TryGetPackagesArray(JsonElement objectElement, out JsonElement packagesArray)
    {
        foreach (var propertyName in new[] { "packages", "items", "rows", "result", "data" })
        {
            if (objectElement.TryGetProperty(propertyName, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
            {
                packagesArray = candidate;
                return true;
            }
        }

        packagesArray = default;
        return false;
    }

    private static string? GetString(JsonElement objectElement, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (objectElement.TryGetProperty(propertyName, out var valueElement) && valueElement.ValueKind == JsonValueKind.String)
            {
                return valueElement.GetString();
            }
        }

        return null;
    }

    private static string? GetString(Dictionary<string, string> currentFields, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (currentFields.TryGetValue(propertyName, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryReadRepositoryIndexText(
        byte[] responseBytes,
        string responseType,
        out string repositoryText,
        out string parsedResponseType,
        out string excerpt,
        out string parseError)
    {
        repositoryText = string.Empty;
        parsedResponseType = responseType;
        excerpt = BuildBinaryExcerpt(responseBytes, responseType);
        parseError = "Le contenu de l'index du depot WAPT est invalide.";

        try
        {
            if (IsGzip(responseBytes))
            {
                using var memoryStream = new MemoryStream(responseBytes);
                using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzipStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                repositoryText = reader.ReadToEnd();
                parsedResponseType = $"{responseType}; repository-index-gzip";
                excerpt = BuildTextExcerpt(repositoryText);
                return true;
            }

            if (IsZip(responseBytes))
            {
                using var memoryStream = new MemoryStream(responseBytes);
                using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read, leaveOpen: false);
                var packagesEntry = archive.Entries.FirstOrDefault(entry =>
                    entry.Name.Equals("Packages", StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(entry => entry.Length > 0);

                if (packagesEntry is null)
                {
                    parseError = "L'archive de l'index du depot WAPT ne contient aucune entree exploitable.";
                    return false;
                }

                using var entryStream = packagesEntry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                repositoryText = reader.ReadToEnd();
                parsedResponseType = $"{responseType}; repository-index-zip:{packagesEntry.FullName}";
                excerpt = BuildTextExcerpt(repositoryText);
                return true;
            }

            repositoryText = Encoding.UTF8.GetString(responseBytes);
            parsedResponseType = $"{responseType}; repository-index-text";
            excerpt = BuildTextExcerpt(repositoryText);
            return true;
        }
        catch (InvalidDataException exception)
        {
            parseError = $"Le contenu de l'index du depot WAPT est invalide: {exception.Message}";
            return false;
        }
    }

    private static bool IsGzip(byte[] content)
    {
        return content.Length >= 2 && content[0] == 0x1F && content[1] == 0x8B;
    }

    private static bool IsZip(byte[] content)
    {
        return content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B;
    }

    private static Uri BuildOriginUri(Uri serverUri)
    {
        var builder = new UriBuilder(serverUri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private static Uri EnsureTrailingSlash(Uri serverUri)
    {
        var builder = new UriBuilder(serverUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path = $"{builder.Path}/";
        }

        return builder.Uri;
    }

    private static void AddCandidate(
        List<Uri> candidates,
        HashSet<string> seenUrls,
        Uri candidate)
    {
        if (seenUrls.Add(candidate.AbsoluteUri))
        {
            candidates.Add(candidate);
        }
    }

    private static string GetResponseType(HttpResponseMessage response)
    {
        return response.Content.Headers.ContentType?.ToString() ?? "unknown";
    }

    private static string BuildTextExcerpt(string content)
    {
        var normalized = content.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();

        if (normalized.Length <= ResponseExcerptLength)
        {
            return normalized;
        }

        return $"{normalized[..ResponseExcerptLength]}...";
    }

    private static string BuildBinaryExcerpt(byte[] content, string responseType)
    {
        if (content.Length == 0)
        {
            return "<empty response>";
        }

        if (responseType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            responseType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
            responseType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
            responseType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return BuildTextExcerpt(Encoding.UTF8.GetString(content));
        }

        var previewLength = Math.Min(24, content.Length);
        return $"<binary {content.Length} bytes> {Convert.ToHexString(content[..previewLength])}";
    }

    private static string BuildHttpFailureMessage(HttpStatusCode statusCode, bool isRepositoryAttempt)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => isRepositoryAttempt
                ? "L'index du depot WAPT a retourne HTTP 400 BadRequest."
                : "L'endpoint JSON de secours a retourne HTTP 400 BadRequest.",
            HttpStatusCode.Unauthorized => "L'acces au depot WAPT a retourne HTTP 401 Unauthorized.",
            HttpStatusCode.Forbidden => "L'acces au depot WAPT a retourne HTTP 403 Forbidden.",
            HttpStatusCode.NotFound => isRepositoryAttempt
                ? "L'index du depot WAPT est introuvable (HTTP 404)."
                : "L'endpoint JSON de secours est introuvable (HTTP 404).",
            _ => $"Le serveur WAPT a retourne HTTP {(int)statusCode} {statusCode}."
        };
    }

    private InvalidOperationException BuildFinalFailure(
        string? sslTechnicalDetails,
        List<PackageLoadAttempt> attempts,
        string? extraDetails = null)
    {
        LastTechnicalDetails = BuildTechnicalDetails(sslTechnicalDetails, attempts, extraDetails);

        if (attempts.Any(attempt => attempt.FailureKind == PackageAttemptFailureKind.Timeout))
        {
            return new InvalidOperationException("Le chargement des paquets WAPT a expire.");
        }

        if (attempts.Any(attempt => attempt.FailureKind == PackageAttemptFailureKind.ClientCertificateRejected))
        {
            return new InvalidOperationException(WaptHttpClientFactory.ClientCertificateRejectedByServerMessage);
        }

        if (attempts.Any(attempt => attempt.FailureKind == PackageAttemptFailureKind.Network))
        {
            return new InvalidOperationException("Le serveur WAPT est inaccessible.");
        }

        var repositoryAttempts = attempts.Where(attempt => attempt.Strategy == "RepositoryIndex").ToList();

        if (repositoryAttempts.Count > 0 && repositoryAttempts.All(attempt => attempt.StatusCode == HttpStatusCode.NotFound))
        {
            return new InvalidOperationException("L'index du depot WAPT est introuvable.");
        }

        if (attempts.Any(attempt => attempt.FailureKind == PackageAttemptFailureKind.InvalidContent))
        {
            return new InvalidOperationException("Le contenu de l'index du depot WAPT est invalide.");
        }

        if (attempts.Any(attempt => attempt.FailureKind == PackageAttemptFailureKind.InvalidJson))
        {
            return new InvalidOperationException("La reponse JSON de l'endpoint de secours est invalide.");
        }

        if (attempts.Any(attempt => attempt.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
        {
            return new InvalidOperationException("Aucune source de paquets WAPT exploitable n'a pu etre lue.");
        }

        return new InvalidOperationException("Impossible de charger les paquets WAPT.");
    }

    private static string BuildTechnicalDetails(
        string? sslTechnicalDetails,
        IEnumerable<PackageLoadAttempt> attempts,
        string? extraDetails = null)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(sslTechnicalDetails))
        {
            builder.AppendLine("SSL details:");
            builder.AppendLine(sslTechnicalDetails.Trim());
        }

        foreach (var attempt in attempts)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("---");
            }

            builder.AppendLine($"Strategy: {attempt.Strategy}");
            builder.AppendLine($"URL: {attempt.Url}");
            builder.AppendLine($"Status code: {(attempt.StatusCode is null ? "<none>" : $"{(int)attempt.StatusCode} {attempt.StatusCode}")}");
            builder.AppendLine($"Response type: {attempt.ResponseType}");

            if (!string.IsNullOrWhiteSpace(attempt.ResponseExcerpt))
            {
                builder.AppendLine($"Excerpt: {attempt.ResponseExcerpt}");
            }

            if (!string.IsNullOrWhiteSpace(attempt.FailureMessage))
            {
                builder.AppendLine($"Result: {attempt.FailureMessage}");
            }
            else
            {
                builder.AppendLine($"Result: {attempt.Packages.Count} paquet(s) brut(s) charge(s) avant filtrage cd48.");
            }
        }

        if (!string.IsNullOrWhiteSpace(extraDetails))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("---");
            }

            builder.Append(extraDetails.Trim());
        }

        return builder.ToString().Trim();
    }

    private static bool IsClientCertificateRejectedResponse(
        HttpStatusCode statusCode,
        string? responseText,
        string responseExcerpt)
    {
        if (statusCode != HttpStatusCode.BadRequest)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(responseText) && ClientCertificateRejectedMarkers.Any(marker =>
                responseText.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return ClientCertificateRejectedMarkers.Any(marker =>
            responseExcerpt.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetTextResponse(byte[] content, string responseType)
    {
        if (responseType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            responseType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
            responseType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
            responseType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetString(content);
        }

        return null;
    }
}
