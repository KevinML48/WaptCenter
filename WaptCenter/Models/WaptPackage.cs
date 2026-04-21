using System.Text.Json.Serialization;

namespace WaptCenter.Models;

public sealed class WaptPackage
{
    [JsonPropertyName("package_id")]
    public string PackageId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Architecture { get; set; } = string.Empty;

    public string Maturity { get; set; } = string.Empty;
}