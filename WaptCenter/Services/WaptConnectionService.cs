using System.Net.Http;
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

        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds))
            };

            using var response = await httpClient.GetAsync(config.ServerUrl, cancellationToken);

            return response.IsSuccessStatusCode
                ? new WaptConnectionTestResult
                {
                    Success = true,
                    Message = $"Connexion .NET OK ({(int)response.StatusCode})."
                }
                : new WaptConnectionTestResult
                {
                    Success = false,
                    Message = $"Le serveur WAPT a repondu avec le statut HTTP {(int)response.StatusCode}."
                };
        }
        catch (Exception exception)
        {
            return new WaptConnectionTestResult
            {
                Success = false,
                Message = $"Echec du test .NET: {exception.Message}"
            };
        }
    }
}
