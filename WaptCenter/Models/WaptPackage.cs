namespace WaptCenter.Models;

public sealed class WaptPackage
{
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Architecture { get; set; } = string.Empty;

    public string Maturity { get; set; } = string.Empty;
}
