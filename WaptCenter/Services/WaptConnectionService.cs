using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using WaptCenter.Models;

namespace WaptCenter.Services;

public sealed class WaptConnectionService
{
    public async Task<WaptConnectionTestResult> TestConnectionAsync(WaptConfig? config, CancellationToken cancellationToken = default)
    {
        if (config is null || string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            return new WaptConnectionTestResult
            {
                Success = false,
                Message = "L'URL du serveur WAPT est requise."
            };
        }

        WaptHttpClientContext? httpClientContext = null;

        try
        {
            httpClientContext = WaptHttpClientFactory.Create(config);
            var serverUri = new Uri(config.ServerUrl, UriKind.Absolute);
            httpClientContext.SetRequestTechnicalDetails(serverUri);
            using var response = await httpClientContext.Client.GetAsync(serverUri, cancellationToken);

            return response.IsSuccessStatusCode
                ? new WaptConnectionTestResult
                {
                    Success = true,
                    Message = $"Connexion .NET securisee OK ({(int)response.StatusCode}).",
                    TechnicalDetails = httpClientContext.SslValidationState.TechnicalDetails,
                    StatusCode = response.StatusCode
                }
                : new WaptConnectionTestResult
                {
                    Success = false,
                    Message = $"Le serveur WAPT a repondu avec le statut HTTP {(int)response.StatusCode}.",
                    TechnicalDetails = httpClientContext.SslValidationState.TechnicalDetails,
                    StatusCode = response.StatusCode
                };
        }
        catch (WaptClientCertificateException exception)
        {
            return new WaptConnectionTestResult
            {
                Success = false,
                Message = exception.Message,
                TechnicalDetails = exception.TechnicalDetails
            };
        }
        catch (CryptographicException exception)
        {
            return new WaptConnectionTestResult
            {
                Success = false,
                Message = $"Certificat client invalide: {exception.Message}",
                TechnicalDetails = exception.ToString()
            };
        }
        catch (FileNotFoundException exception)
        {
            return new WaptConnectionTestResult
            {
                Success = false,
                Message = $"Fichier introuvable: {exception.FileName}",
                TechnicalDetails = exception.ToString()
            };
        }
        catch (Exception exception)
        {
            return new WaptConnectionTestResult
            {
                Success = false,
                Message = $"Echec du test .NET: {exception.Message}",
                TechnicalDetails = exception.ToString()
            };
        }
        finally
        {
            httpClientContext?.Dispose();
        }
    }
}
