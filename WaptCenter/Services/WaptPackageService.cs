using System.Text.Json;
using WaptCenter.Models;

namespace WaptCenter.Services;

public sealed class WaptPackageService
{
    public async Task<List<WaptPackage>> GetCd48PackagesAsync(WaptConfig config, CancellationToken cancellationToken = default)
    {
        using var httpClientContext = WaptHttpClientFactory.Create(config);
        var baseUri = new Uri(config.ServerUrl, UriKind.Absolute);
        var requestUri = new Uri(baseUri, "api/v1/packages");
        httpClientContext.SetRequestTechnicalDetails(requestUri);

        using var response = await httpClientContext.Client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<PackagesApiEnvelope>(stream, cancellationToken: cancellationToken);

        return payload?.Packages?
            .Where(package => !string.IsNullOrWhiteSpace(package.Name) && package.Name.StartsWith("cd48", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
    }

    private sealed class PackagesApiEnvelope
    {
        public List<WaptPackage>? Packages { get; set; }
    }
}
