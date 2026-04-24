using System.Text.Json.Serialization;

namespace WaptCenter.Models;

public sealed class WaptMachine
{
    public const string ExactInstallMatchType = "exact_install";
    public const string DependsFallbackMatchType = "depends_fallback";
    public const string CompliantComplianceStatus = "compliant";
    public const string UnknownComplianceStatus = "unknown";
    public const string NonCompliantComplianceStatus = "non_compliant";
    public const string UnknownOuPath = "OU non renseignee";

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("fqdn")]
    public string Fqdn { get; set; } = string.Empty;

    [JsonPropertyName("package_id")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("installed_version")]
    public string InstalledVersion { get; set; } = string.Empty;

    [JsonPropertyName("match_type")]
    public string MatchType { get; set; } = string.Empty;

    [JsonPropertyName("is_exact_install")]
    public bool IsExactInstall { get; set; }

    [JsonPropertyName("compliance_status")]
    public string ComplianceStatus { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("last_seen")]
    public string LastSeen { get; set; } = string.Empty;

    [JsonPropertyName("organizational_unit")]
    public string OrganizationalUnit { get; set; } = string.Empty;

    [JsonPropertyName("ou_path")]
    public string OuPath { get; set; } = string.Empty;

    [JsonPropertyName("organization")]
    public string Organization { get; set; } = string.Empty;

    [JsonPropertyName("organization_display")]
    public string OrganizationDisplay { get; set; } = string.Empty;

    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = [];

    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsDependsFallback =>
        string.Equals(MatchType, DependsFallbackMatchType, StringComparison.OrdinalIgnoreCase) ||
        (!IsExactInstall && !string.Equals(MatchType, ExactInstallMatchType, StringComparison.OrdinalIgnoreCase));

    [JsonIgnore]
    public string MatchTypeDisplayLabel => IsExactInstall ? "Installation exacte" : "D\u00E9pendance / fallback";

    [JsonIgnore]
    public bool IsCompliant => string.Equals(ComplianceStatus, CompliantComplianceStatus, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsNonCompliant => string.Equals(ComplianceStatus, NonCompliantComplianceStatus, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsComplianceUnknown =>
        string.Equals(ComplianceStatus, UnknownComplianceStatus, StringComparison.OrdinalIgnoreCase) ||
        (!IsCompliant && !IsNonCompliant);

    [JsonIgnore]
    public string ComplianceStatusDisplayLabel =>
        IsCompliant
            ? "Conforme"
            : IsNonCompliant
                ? "Non conforme"
                : "Inconnu (fallback)";

    public void NormalizeMatchMetadata()
    {
        if (string.IsNullOrWhiteSpace(MatchType))
        {
            MatchType = IsExactInstall ? ExactInstallMatchType : DependsFallbackMatchType;
        }

        if (string.Equals(MatchType, ExactInstallMatchType, StringComparison.OrdinalIgnoreCase))
        {
            IsExactInstall = true;
            return;
        }

        if (string.Equals(MatchType, DependsFallbackMatchType, StringComparison.OrdinalIgnoreCase))
        {
            IsExactInstall = false;
            return;
        }

        MatchType = IsExactInstall ? ExactInstallMatchType : DependsFallbackMatchType;
    }

    public void NormalizeLocationMetadata()
    {
        Groups ??= [];

        if (string.IsNullOrWhiteSpace(OuPath))
        {
            OuPath = string.IsNullOrWhiteSpace(OrganizationalUnit) ? UnknownOuPath : OrganizationalUnit;
        }

        if (string.IsNullOrWhiteSpace(OrganizationalUnit) &&
            !string.Equals(OuPath, UnknownOuPath, StringComparison.OrdinalIgnoreCase))
        {
            OrganizationalUnit = OuPath;
        }

        if (!string.IsNullOrWhiteSpace(OrganizationDisplay))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Organization) &&
            !string.IsNullOrWhiteSpace(OuPath) &&
            !string.Equals(OuPath, UnknownOuPath, StringComparison.OrdinalIgnoreCase))
        {
            OrganizationDisplay = $"{Organization} | {OuPath}";
            return;
        }

        OrganizationDisplay = !string.IsNullOrWhiteSpace(Organization)
            ? Organization
            : OuPath;
    }

    public void NormalizeComplianceMetadata()
    {
        if (string.IsNullOrWhiteSpace(ComplianceStatus))
        {
            ComplianceStatus = ResolveComplianceStatusFromMatchType();
            return;
        }

        if (string.Equals(ComplianceStatus, CompliantComplianceStatus, StringComparison.OrdinalIgnoreCase))
        {
            ComplianceStatus = CompliantComplianceStatus;
            return;
        }

        if (string.Equals(ComplianceStatus, UnknownComplianceStatus, StringComparison.OrdinalIgnoreCase))
        {
            ComplianceStatus = UnknownComplianceStatus;
            return;
        }

        if (string.Equals(ComplianceStatus, NonCompliantComplianceStatus, StringComparison.OrdinalIgnoreCase))
        {
            ComplianceStatus = NonCompliantComplianceStatus;
            return;
        }

        ComplianceStatus = ResolveComplianceStatusFromMatchType();
    }

    private string ResolveComplianceStatusFromMatchType()
    {
        if (string.Equals(MatchType, ExactInstallMatchType, StringComparison.OrdinalIgnoreCase) || IsExactInstall)
        {
            return CompliantComplianceStatus;
        }

        if (string.Equals(MatchType, DependsFallbackMatchType, StringComparison.OrdinalIgnoreCase))
        {
            return UnknownComplianceStatus;
        }

        return UnknownComplianceStatus;
    }
}
