namespace WaptCenter.Models;

public sealed class WaptConfig
{
    public string ServerUrl { get; set; } = string.Empty;

    public string ClientCertPath { get; set; } = string.Empty;

    public string ClientKeyPath { get; set; } = string.Empty;

    public string PemPath { get; set; } = string.Empty;

    public string Pkcs12Path { get; set; } = string.Empty;

    public string CertPassword { get; set; } = string.Empty;

    public string CaCertPath { get; set; } = string.Empty;

    public bool VerifySsl { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 30;
}
