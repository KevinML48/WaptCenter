namespace WaptCenter.Models;

public sealed class DashboardPackageSummary
{
    public string PackageId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public int MachineCount { get; set; }

    public int CompliantCount { get; set; }

    public int UnknownCount { get; set; }

    public int NonCompliantCount { get; set; }

    public int DistinctOuCount { get; set; }
}