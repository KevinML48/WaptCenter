using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WaptCenter.Models;

namespace WaptCenter.Services;

internal sealed class WaptSslValidationState
{
    public string ClientTechnicalDetails { get; set; } = string.Empty;

    public string RequestTechnicalDetails { get; set; } = string.Empty;

    public string ServerTechnicalDetails { get; set; } = string.Empty;

    public string TechnicalDetails => CombineTechnicalDetails(
        ClientTechnicalDetails,
        RequestTechnicalDetails,
        ServerTechnicalDetails);

    public string? FailureMessage { get; set; }

    private static string CombineTechnicalDetails(params string?[] detailSections)
    {
        var normalizedSections = detailSections
            .Select(section => (section ?? string.Empty).Trim())
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToList();

        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            normalizedSections);
    }
}

internal sealed class WaptClientCertificateException : Exception
{
    public WaptClientCertificateException(string message, string technicalDetails)
        : base(message)
    {
        TechnicalDetails = technicalDetails;
    }

    public string TechnicalDetails { get; }
}

internal sealed class WaptLoadedClientCertificate
{
    public WaptLoadedClientCertificate(
        X509Certificate2 certificate,
        string loadMode,
        string certificatePath,
        string privateKeyPath)
    {
        Certificate = certificate;
        LoadMode = loadMode;
        CertificatePath = certificatePath;
        PrivateKeyPath = privateKeyPath;
    }

    public X509Certificate2 Certificate { get; }

    public string LoadMode { get; }

    public string CertificatePath { get; }

    public string PrivateKeyPath { get; }
}

internal sealed class WaptHttpClientContext : IDisposable
{
    private readonly HttpClientHandler _handler;
    private readonly X509Certificate2 _clientCertificate;
    private readonly X509Certificate2? _expectedServerCertificate;

    public WaptHttpClientContext(
        HttpClient client,
        HttpClientHandler handler,
        X509Certificate2 clientCertificate,
        X509Certificate2? expectedServerCertificate,
        WaptSslValidationState sslValidationState)
    {
        Client = client;
        _handler = handler;
        _clientCertificate = clientCertificate;
        _expectedServerCertificate = expectedServerCertificate;
        SslValidationState = sslValidationState;
    }

    public HttpClient Client { get; }

    public WaptSslValidationState SslValidationState { get; }

    public void SetRequestTechnicalDetails(Uri requestUri)
    {
        SslValidationState.RequestTechnicalDetails = $"Server URL called: {requestUri}";
    }

    public void Dispose()
    {
        Client.Dispose();
        _handler.Dispose();
        _clientCertificate.Dispose();
        _expectedServerCertificate?.Dispose();
    }
}

internal static class WaptHttpClientFactory
{
    internal const string ClientCertificateMissingPrivateKeyMessage = "Le certificat client charge ne contient pas de cle privee exploitable.";
    internal const string ClientCertificateMissingClientAuthenticationMessage = "Le certificat client charge ne declare pas l'usage Client Authentication requis.";
    internal const string ClientCertificateRejectedByServerMessage = "Le serveur WAPT refuse le certificat client ou celui-ci n'a pas ete transmis correctement.";

    public static WaptHttpClientContext Create(WaptConfig config)
    {
        var loadedClientCertificate = LoadClientCertificate(config);
        var clientCertificate = loadedClientCertificate.Certificate;
        var expectedServerCertificate = LoadExpectedServerCertificate(config);
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };
        HttpClient? client = null;

        try
        {
            handler.ClientCertificates.Add(clientCertificate);

            var sslValidationState = new WaptSslValidationState
            {
                ClientTechnicalDetails = BuildClientCertificateTechnicalDetails(
                    clientCertificate,
                    handler.ClientCertificates,
                    loadedClientCertificate.LoadMode,
                    loadedClientCertificate.CertificatePath,
                    loadedClientCertificate.PrivateKeyPath)
            };

            EnsureClientCertificateCanBeUsed(clientCertificate, sslValidationState.ClientTechnicalDetails);

            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
            {
                if (!config.VerifySsl)
                {
                    sslValidationState.ServerTechnicalDetails = "Verification SSL desactivee.";
                    sslValidationState.FailureMessage = null;
                    return true;
                }

                if (certificate is null)
                {
                    sslValidationState.ServerTechnicalDetails = "SSL errors: RemoteCertificateNotAvailable";
                    sslValidationState.FailureMessage = "Erreur SSL lors de la validation du serveur.";
                    return false;
                }

                if (expectedServerCertificate is null)
                {
                    sslValidationState.ServerTechnicalDetails = "Le certificat serveur attendu n'a pas ete charge.";
                    sslValidationState.FailureMessage = "Erreur SSL lors de la validation du serveur.";
                    return false;
                }

                try
                {
                    using var serverCertificate = new X509Certificate2(certificate);
                    var serverThumbprint = NormalizeThumbprint(serverCertificate.Thumbprint);
                    var expectedThumbprint = NormalizeThumbprint(expectedServerCertificate.Thumbprint);

                    sslValidationState.ServerTechnicalDetails = BuildServerSslTechnicalDetails(
                        sslPolicyErrors,
                        serverCertificate,
                        expectedServerCertificate);

                    if (string.Equals(serverThumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        sslValidationState.FailureMessage = null;
                        return true;
                    }

                    sslValidationState.FailureMessage = "Le certificat serveur est different de celui attendu.";
                    return false;
                }
                catch (Exception exception)
                {
                    sslValidationState.ServerTechnicalDetails = $"Erreur pendant la validation SSL personnalisee : {exception}";
                    sslValidationState.FailureMessage = "Erreur SSL lors de la validation du serveur.";
                    return false;
                }
            };

            client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };

            return new WaptHttpClientContext(
                client,
                handler,
                clientCertificate,
                expectedServerCertificate,
                sslValidationState);
        }
        catch
        {
            client?.Dispose();
            handler.Dispose();
            clientCertificate.Dispose();
            expectedServerCertificate?.Dispose();
            throw;
        }
    }

    private static WaptLoadedClientCertificate LoadClientCertificate(WaptConfig config)
    {
        if (UsePemClientCertificate(config))
        {
            return LoadClientCertificateFromPem(config);
        }

        return LoadClientCertificateFromPkcs12(config);
    }

    private static WaptLoadedClientCertificate LoadClientCertificateFromPkcs12(WaptConfig config)
    {
        if (!File.Exists(config.Pkcs12Path))
        {
            throw new FileNotFoundException("PKCS12 certificate file missing.", config.Pkcs12Path);
        }

        return new WaptLoadedClientCertificate(
            new X509Certificate2(
                config.Pkcs12Path,
                config.CertPassword,
                X509KeyStorageFlags.DefaultKeySet |
                X509KeyStorageFlags.Exportable |
                X509KeyStorageFlags.PersistKeySet),
            "P12",
            config.Pkcs12Path,
            string.Empty);
    }

    private static WaptLoadedClientCertificate LoadClientCertificateFromPem(WaptConfig config)
    {
        var certificatePath = ResolvePemCertificatePath(config);
        var privateKeyPath = ResolvePemPrivateKeyPath(config);

        if (!File.Exists(certificatePath))
        {
            throw new FileNotFoundException("PEM certificate file missing.", certificatePath);
        }

        if (!File.Exists(privateKeyPath))
        {
            throw new FileNotFoundException("PEM private key file missing.", privateKeyPath);
        }

        var certificateWithPrivateKey = CreateCertificateFromPemFiles(certificatePath, privateKeyPath);
        var persistedCertificate = PersistPemCertificate(certificateWithPrivateKey);

        return new WaptLoadedClientCertificate(
            persistedCertificate,
            "PEM",
            certificatePath,
            privateKeyPath);
    }

    private static void EnsureClientCertificateCanBeUsed(
        X509Certificate2 clientCertificate,
        string clientTechnicalDetails)
    {
        if (!clientCertificate.HasPrivateKey)
        {
            throw new WaptClientCertificateException(
                ClientCertificateMissingPrivateKeyMessage,
                clientTechnicalDetails);
        }

        if (HasEnhancedKeyUsageExtension(clientCertificate, out var hasClientAuthenticationEku) && !hasClientAuthenticationEku)
        {
            throw new WaptClientCertificateException(
                ClientCertificateMissingClientAuthenticationMessage,
                clientTechnicalDetails);
        }
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

    private static X509Certificate2 CreateCertificateFromPemFiles(string certificatePath, string privateKeyPath)
    {
        var certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);

        if (certificate.HasPrivateKey)
        {
            return certificate;
        }

        certificate.Dispose();

        using var certificateWithoutPrivateKey = X509Certificate2.CreateFromPemFile(certificatePath);
        return CopyPrivateKeyFromPem(certificateWithoutPrivateKey, privateKeyPath);
    }

    private static X509Certificate2 CopyPrivateKeyFromPem(X509Certificate2 certificate, string privateKeyPath)
    {
        var privateKeyPem = File.ReadAllText(privateKeyPath);
        var publicKeyOid = certificate.PublicKey.Oid?.Value;

        if (string.Equals(publicKeyOid, "1.2.840.113549.1.1.1", StringComparison.Ordinal))
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            return certificate.CopyWithPrivateKey(rsa);
        }

        if (string.Equals(publicKeyOid, "1.2.840.10045.2.1", StringComparison.Ordinal))
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(privateKeyPem);
            return certificate.CopyWithPrivateKey(ecdsa);
        }

        if (string.Equals(publicKeyOid, "1.2.840.10040.4.1", StringComparison.Ordinal))
        {
            using var dsa = DSA.Create();
            dsa.ImportFromPem(privateKeyPem);
            return certificate.CopyWithPrivateKey(dsa);
        }

        throw new CryptographicException($"Unsupported PEM private key algorithm: {publicKeyOid ?? "unknown"}.");
    }

    private static X509Certificate2 PersistPemCertificate(X509Certificate2 certificate)
    {
        var exportedCertificate = certificate.Export(X509ContentType.Pkcs12);

        try
        {
            return new X509Certificate2(
                exportedCertificate,
                string.Empty,
                X509KeyStorageFlags.DefaultKeySet |
                X509KeyStorageFlags.Exportable |
                X509KeyStorageFlags.PersistKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exportedCertificate);
            certificate.Dispose();
        }
    }

    private static X509Certificate2? LoadExpectedServerCertificate(WaptConfig config)
    {
        if (!config.VerifySsl)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(config.CaCertPath))
        {
            return null;
        }

        if (!File.Exists(config.CaCertPath))
        {
            throw new FileNotFoundException("Expected server certificate file missing.", config.CaCertPath);
        }

        return new X509Certificate2(config.CaCertPath);
    }

    private static string NormalizeThumbprint(string? thumbprint)
    {
        return (thumbprint ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }

    private static bool HasEnhancedKeyUsageExtension(
        X509Certificate2 clientCertificate,
        out bool hasClientAuthenticationEku)
    {
        hasClientAuthenticationEku = false;
        var hasEkuExtension = false;

        foreach (var extension in clientCertificate.Extensions)
        {
            if (extension is not X509EnhancedKeyUsageExtension ekuExtension)
            {
                continue;
            }

            hasEkuExtension = true;

            foreach (var usage in ekuExtension.EnhancedKeyUsages)
            {
                if (string.Equals(usage.Value, "1.3.6.1.5.5.7.3.2", StringComparison.Ordinal))
                {
                    hasClientAuthenticationEku = true;
                    return true;
                }
            }
        }

        return hasEkuExtension;
    }

    private static string BuildClientCertificateTechnicalDetails(
        X509Certificate2 clientCertificate,
        X509CertificateCollection handlerClientCertificates,
        string loadMode,
        string certificatePath,
        string privateKeyPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Client Load Mode: {loadMode}");
        builder.AppendLine($"Client Certificate Source: {certificatePath}");

        if (!string.IsNullOrWhiteSpace(privateKeyPath))
        {
            builder.AppendLine($"Client Private Key Source: {privateKeyPath}");
        }

        builder.AppendLine($"Client Subject: {clientCertificate.Subject}");
        builder.AppendLine($"Client Issuer: {clientCertificate.Issuer}");
        builder.AppendLine($"Client Thumbprint: {NormalizeThumbprint(clientCertificate.Thumbprint)}");
        builder.AppendLine($"Client HasPrivateKey: {clientCertificate.HasPrivateKey}");
        builder.AppendLine($"Client EKU list: {BuildClientEnhancedKeyUsageList(clientCertificate)}");
        builder.AppendLine($"Handler ClientCertificates Count: {handlerClientCertificates.Count}");
        builder.Append(BuildHandlerClientCertificatesDetails(handlerClientCertificates));

        return builder.ToString();
    }

    private static string BuildClientEnhancedKeyUsageList(X509Certificate2 clientCertificate)
    {
        var usages = new List<string>();

        foreach (var extension in clientCertificate.Extensions)
        {
            if (extension is not X509EnhancedKeyUsageExtension ekuExtension)
            {
                continue;
            }

            foreach (var usage in ekuExtension.EnhancedKeyUsages)
            {
                var friendlyName = string.IsNullOrWhiteSpace(usage.FriendlyName)
                    ? usage.Value
                    : usage.FriendlyName;

                usages.Add($"{friendlyName} ({usage.Value})");
            }
        }

        return usages.Count == 0
            ? "<none>"
            : string.Join("; ", usages.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildHandlerClientCertificatesDetails(X509CertificateCollection handlerClientCertificates)
    {
        if (handlerClientCertificates.Count == 0)
        {
            return "Handler ClientCertificates: <empty>";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Handler ClientCertificates:");

        for (var index = 0; index < handlerClientCertificates.Count; index++)
        {
            using var certificate = new X509Certificate2(handlerClientCertificates[index]);
            builder.AppendLine($"  [{index}] Subject: {certificate.Subject}");
            builder.AppendLine($"  [{index}] Issuer: {certificate.Issuer}");
            builder.AppendLine($"  [{index}] Thumbprint: {NormalizeThumbprint(certificate.Thumbprint)}");
            builder.AppendLine($"  [{index}] HasPrivateKey: {certificate.HasPrivateKey}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildServerSslTechnicalDetails(
        SslPolicyErrors sslPolicyErrors,
        X509Certificate2 serverCertificate,
        X509Certificate2 expectedServerCertificate)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"SSL errors: {sslPolicyErrors}");
        builder.AppendLine($"Server Subject: {serverCertificate.Subject}");
        builder.AppendLine($"Server Issuer: {serverCertificate.Issuer}");
        builder.AppendLine($"Server Thumbprint: {NormalizeThumbprint(serverCertificate.Thumbprint)}");
        builder.AppendLine($"Expected Subject: {expectedServerCertificate.Subject}");
        builder.AppendLine($"Expected Issuer: {expectedServerCertificate.Issuer}");
        builder.Append($"Expected Thumbprint: {NormalizeThumbprint(expectedServerCertificate.Thumbprint)}");

        return builder.ToString();
    }
}