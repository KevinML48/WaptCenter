namespace WaptCenter.Models;

public sealed class DashboardOuSummary
{
    public string OrganizationDisplay { get; set; } = string.Empty;

    public int MachineCount { get; set; }

    public int PackageCount { get; set; }

    public int CompliantCount { get; set; }

    public int UnknownCount { get; set; }

    public int NonCompliantCount { get; set; }
}