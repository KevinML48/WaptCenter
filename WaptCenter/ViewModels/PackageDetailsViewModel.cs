using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Data;
using WaptCenter.Models;
using WaptCenter.Services;

namespace WaptCenter.ViewModels;

public partial class PackageDetailsViewModel : ObservableObject
{
    private const string AllOuFilterValue = "Toutes les OU";

    private readonly ConfigService _configService;
    private readonly WaptBridgeMachineService _waptBridgeMachineService;
    private CancellationTokenSource? _loadMachinesCancellationTokenSource;

    public PackageDetailsViewModel(
        ConfigService configService,
        WaptBridgeMachineService waptBridgeMachineService)
    {
        _configService = configService;
        _waptBridgeMachineService = waptBridgeMachineService;

        FilteredMachines = CollectionViewSource.GetDefaultView(Machines);
        FilteredMachines.Filter = FilterMachine;
        FilteredMachines.SortDescriptions.Add(new SortDescription(nameof(WaptMachine.OuPath), ListSortDirection.Ascending));
        FilteredMachines.SortDescriptions.Add(new SortDescription(nameof(WaptMachine.OrganizationDisplay), ListSortDirection.Ascending));
        FilteredMachines.SortDescriptions.Add(new SortDescription(nameof(WaptMachine.Hostname), ListSortDirection.Ascending));

        ClearSelection();
    }

    public ObservableCollection<WaptMachine> Machines { get; } = [];

    public ICollectionView FilteredMachines { get; }

    public ObservableCollection<string> AvailableOuFilters { get; } = [];

    public ObservableCollection<string> OuComplianceBreakdownLines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    private bool isLoadingMachines;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    private WaptPackage? selectedPackage;

    [ObservableProperty]
    private string selectedPackageSummary = "Selectionnez un paquet cd48 pour charger les machines associees.";

    [ObservableProperty]
    private string statusMessage = "Selectionnez un paquet cd48 pour charger les machines associees.";

    [ObservableProperty]
    private string technicalDetails = string.Empty;

    [ObservableProperty]
    private bool isStatusError;

    [ObservableProperty]
    private bool hasMachines;

    [ObservableProperty]
    private string matchTypeWarningMessage = string.Empty;

    [ObservableProperty]
    private bool hasOnlyDependsFallbackMachines;

    [ObservableProperty]
    private string matchTypeSummaryMessage = string.Empty;

    [ObservableProperty]
    private string complianceSummaryMessage = string.Empty;

    [ObservableProperty]
    private string ouSummaryMessage = string.Empty;

    [ObservableProperty]
    private string selectedOuFilter = AllOuFilterValue;

    [ObservableProperty]
    private bool hasOuComplianceBreakdown;

    [ObservableProperty]
    private int visibleMachineCount;

    [ObservableProperty]
    private int compliantMachineCount;

    [ObservableProperty]
    private int unknownMachineCount;

    [ObservableProperty]
    private int nonCompliantMachineCount;

    [ObservableProperty]
    private string machineLoadDurationMetric = "Duree machines : -";

    [ObservableProperty]
    private string machineCacheStatusMetric = "Cache machines : -";

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        await LoadForPackageAsync(SelectedPackage);
    }

    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private void ExportCsv()
    {
        var machinesToExport = GetVisibleMachines().ToList();
        if (SelectedPackage is null || machinesToExport.Count == 0)
        {
            const string noMachineMessage = "Aucune machine visible n'est disponible pour l'export CSV.";
            IsStatusError = true;
            StatusMessage = noMachineMessage;
            MessageBox.Show(noMachineMessage, "Exporter CSV", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exporter les machines affichees",
            Filter = "CSV UTF-8 (*.csv)|*.csv|Tous les fichiers (*.*)|*.*",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = BuildDefaultCsvFileName()
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, BuildCsvContent(machinesToExport), new UTF8Encoding(true));

            var successMessage =
                $"{machinesToExport.Count} machine(s) visible(s) exportee(s) dans '{dialog.FileName}'.";
            IsStatusError = false;
            StatusMessage = successMessage;
            MessageBox.Show(successMessage, "Exporter CSV", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            var errorMessage =
                $"L'export CSV des machines visibles a echoue : {exception.Message}";
            IsStatusError = true;
            StatusMessage = errorMessage;
            MessageBox.Show(errorMessage, "Exporter CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void ClearSelection()
    {
        CancelPendingLoad();
        ReplaceMachines([]);
        SelectedPackage = null;
        SelectedPackageSummary = "Selectionnez un paquet cd48 pour charger les machines associees.";
        StatusMessage = "Selectionnez un paquet cd48 pour charger les machines associees.";
        TechnicalDetails = string.Empty;
        IsStatusError = false;
        HasMachines = false;
        OuComplianceBreakdownLines.Clear();
        ResetOuFilters();
        MatchTypeWarningMessage = string.Empty;
        MatchTypeSummaryMessage = string.Empty;
        ComplianceSummaryMessage = string.Empty;
        OuSummaryMessage = string.Empty;
        HasOuComplianceBreakdown = false;
        VisibleMachineCount = 0;
        CompliantMachineCount = 0;
        UnknownMachineCount = 0;
        NonCompliantMachineCount = 0;
        MachineLoadDurationMetric = "Duree machines : -";
        MachineCacheStatusMetric = "Cache machines : -";
        HasOnlyDependsFallbackMachines = false;
        IsLoadingMachines = false;
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadForPackageAsync(WaptPackage? package)
    {
        CancelPendingLoad();
        SelectedPackage = package;
        ReplaceMachines([]);
        OuComplianceBreakdownLines.Clear();
        TechnicalDetails = string.Empty;
        IsStatusError = false;
        HasMachines = false;
        ResetOuFilters();
        MatchTypeWarningMessage = string.Empty;
        MatchTypeSummaryMessage = string.Empty;
        ComplianceSummaryMessage = string.Empty;
        OuSummaryMessage = string.Empty;
        HasOuComplianceBreakdown = false;
        VisibleMachineCount = 0;
        CompliantMachineCount = 0;
        UnknownMachineCount = 0;
        NonCompliantMachineCount = 0;
        MachineLoadDurationMetric = "Duree machines : -";
        MachineCacheStatusMetric = "Cache machines : -";
        HasOnlyDependsFallbackMachines = false;

        if (package is null || string.IsNullOrWhiteSpace(package.PackageId))
        {
            SelectedPackageSummary = "Selectionnez un paquet cd48 pour charger les machines associees.";
            StatusMessage = "Selectionnez un paquet cd48 pour charger les machines associees.";
            MatchTypeSummaryMessage = string.Empty;
            ComplianceSummaryMessage = string.Empty;
            OuSummaryMessage = string.Empty;
            ExportCsvCommand.NotifyCanExecuteChanged();
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _loadMachinesCancellationTokenSource = cancellationTokenSource;

        SelectedPackageSummary = BuildPackageSummary(package);
        StatusMessage = $"Chargement des machines pour '{package.PackageId}'...";
        IsLoadingMachines = true;

        try
        {
            var config = _configService.Load();
            var loadStopwatch = Stopwatch.StartNew();
            var machineResult = await _waptBridgeMachineService.GetMachineResultForPackageAsync(
                config,
                package.PackageId,
                cancellationTokenSource.Token);
            loadStopwatch.Stop();

            if (cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            var machines = machineResult.Machines;
            TechnicalDetails = machineResult.TechnicalDetails;
            MachineLoadDurationMetric = $"Duree chargement machines : {FormatDuration(loadStopwatch.Elapsed)}";
            MachineCacheStatusMetric = $"Cache machines : {FormatCacheStatus(ExtractTechnicalValue(TechnicalDetails, "Cache status:"))}";

            ReplaceMachines(machines);
            UpdateOuFilterOptions(machines);
            RefreshFilteredMachinesAndAggregates();
            StatusMessage = HasMachines
                ? $"{Machines.Count} machine(s) ont ete trouvees pour '{package.PackageId}'."
                : $"Aucune machine n'a ete trouvee pour '{package.PackageId}'.";
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException exception)
        {
            if (cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            IsStatusError = true;
            StatusMessage = exception.Message;
            MatchTypeSummaryMessage = string.Empty;
            ComplianceSummaryMessage = string.Empty;
            OuSummaryMessage = string.Empty;
            HasOuComplianceBreakdown = false;
            OuComplianceBreakdownLines.Clear();
            TechnicalDetails = string.IsNullOrWhiteSpace(_waptBridgeMachineService.LastTechnicalDetails)
                ? exception.ToString()
                : _waptBridgeMachineService.LastTechnicalDetails;
            MachineCacheStatusMetric = $"Cache machines : {FormatCacheStatus(ExtractTechnicalValue(TechnicalDetails, "Cache status:"))}";
        }
        catch (Exception exception)
        {
            if (cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            IsStatusError = true;
            StatusMessage = "Une erreur inattendue est survenue lors du chargement des machines.";
            MatchTypeSummaryMessage = string.Empty;
            ComplianceSummaryMessage = string.Empty;
            OuSummaryMessage = string.Empty;
            HasOuComplianceBreakdown = false;
            OuComplianceBreakdownLines.Clear();
            TechnicalDetails = string.IsNullOrWhiteSpace(_waptBridgeMachineService.LastTechnicalDetails)
                ? exception.ToString()
                : _waptBridgeMachineService.LastTechnicalDetails;
            MachineCacheStatusMetric = $"Cache machines : {FormatCacheStatus(ExtractTechnicalValue(TechnicalDetails, "Cache status:"))}";
        }
        finally
        {
            if (ReferenceEquals(_loadMachinesCancellationTokenSource, cancellationTokenSource))
            {
                _loadMachinesCancellationTokenSource = null;
                IsLoadingMachines = false;
            }

            cancellationTokenSource.Dispose();
        }
    }

    private bool CanReload()
    {
        return !IsLoadingMachines && SelectedPackage is not null;
    }

    private void CancelPendingLoad()
    {
        if (_loadMachinesCancellationTokenSource is null)
        {
            return;
        }

        _loadMachinesCancellationTokenSource.Cancel();
        _loadMachinesCancellationTokenSource.Dispose();
        _loadMachinesCancellationTokenSource = null;
    }

    private static string BuildPackageSummary(WaptPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Name))
        {
            return $"Package_id selectionne : {package.PackageId}";
        }

        return $"Package_id selectionne : {package.PackageId} ({package.Name})";
    }

    partial void OnSelectedOuFilterChanged(string value)
    {
        RefreshFilteredMachinesAndAggregates();
    }

    private void UpdateMatchTypeWarning(IReadOnlyCollection<WaptMachine> machines)
    {
        HasOnlyDependsFallbackMachines = machines.Count > 0 && machines.All(machine => machine.IsDependsFallback);
        MatchTypeWarningMessage = HasOnlyDependsFallbackMachines
            ? "Les machines affich\u00E9es sont actuellement d\u00E9duites via les d\u00E9pendances WAPT, car le serveur ne renvoie pas le d\u00E9tail d'installation versionn\u00E9."
            : string.Empty;
    }

    private void UpdateMatchTypeSummary(IReadOnlyCollection<WaptMachine> machines)
    {
        if (machines.Count == 0)
        {
            MatchTypeSummaryMessage = string.Empty;
            return;
        }

        var exactInstallCount = machines.Count(machine => machine.IsExactInstall);
        var dependsFallbackCount = machines.Count(machine => machine.IsDependsFallback);

        MatchTypeSummaryMessage =
            $"{FormatCount(machines.Count, "machine trouv\u00E9e", "machines trouv\u00E9es")}, " +
            $"{FormatCount(exactInstallCount, "installation exacte", "installations exactes")}, " +
            $"{dependsFallbackCount} via d\u00E9pendance / fallback.";
    }

    private void UpdateComplianceSummary(IReadOnlyCollection<WaptMachine> machines)
    {
        VisibleMachineCount = machines.Count;
        CompliantMachineCount = machines.Count(machine => machine.IsCompliant);
        UnknownMachineCount = machines.Count(machine => machine.IsComplianceUnknown);
        NonCompliantMachineCount = machines.Count(machine => machine.IsNonCompliant);

        if (VisibleMachineCount == 0)
        {
            ComplianceSummaryMessage = string.Empty;
            return;
        }

        var scopeLabel = IsAllOuFilterSelected()
            ? FormatCount(VisibleMachineCount, "machine", "machines")
            : FormatCount(VisibleMachineCount, "machine visible", "machines visibles");

        ComplianceSummaryMessage = $"{scopeLabel} : {BuildComplianceBreakdown(CompliantMachineCount, UnknownMachineCount, NonCompliantMachineCount)}.";
    }

    private void UpdateOuSummary(IReadOnlyCollection<WaptMachine> machines)
    {
        if (machines.Count == 0)
        {
            OuSummaryMessage = string.Empty;
            return;
        }

        var distinctOuCount = machines
            .Select(machine => machine.OuPath)
            .Where(ouPath => !string.IsNullOrWhiteSpace(ouPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (IsAllOuFilterSelected())
        {
            OuSummaryMessage = $"{FormatCount(distinctOuCount, "OU distincte trouv\u00E9e", "OU distinctes trouv\u00E9es")}.";
            return;
        }

        OuSummaryMessage = $"Filtre OU actif : {SelectedOuFilter}. {FormatCount(distinctOuCount, "OU distincte visible", "OU distinctes visibles")}.";
    }

    private void UpdateOuComplianceBreakdown(IReadOnlyCollection<WaptMachine> machines)
    {
        OuComplianceBreakdownLines.Clear();

        if (machines.Count == 0)
        {
            HasOuComplianceBreakdown = false;
            return;
        }

        var groupedLines = machines
            .GroupBy(machine => machine.OuPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var totalCount = group.Count();
                var compliantCount = group.Count(machine => machine.IsCompliant);
                var unknownCount = group.Count(machine => machine.IsComplianceUnknown);
                var nonCompliantCount = group.Count(machine => machine.IsNonCompliant);

                return $"{group.Key} -> {FormatCount(totalCount, "machine", "machines")} ({BuildComplianceBreakdown(compliantCount, unknownCount, nonCompliantCount)})";
            });

        foreach (var line in groupedLines)
        {
            OuComplianceBreakdownLines.Add(line);
        }

        HasOuComplianceBreakdown = OuComplianceBreakdownLines.Count > 0;
    }

    private void UpdateOuFilterOptions(IReadOnlyCollection<WaptMachine> machines)
    {
        var currentSelection = SelectedOuFilter;
        var distinctOuPaths = machines
            .Select(machine => machine.OuPath)
            .Where(ouPath => !string.IsNullOrWhiteSpace(ouPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ouPath => ouPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableOuFilters.Clear();
        AvailableOuFilters.Add(AllOuFilterValue);

        foreach (var ouPath in distinctOuPaths)
        {
            AvailableOuFilters.Add(ouPath);
        }

        if (!AvailableOuFilters.Any(filter => string.Equals(filter, currentSelection, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedOuFilter = AllOuFilterValue;
        }
    }

    private void ResetOuFilters()
    {
        AvailableOuFilters.Clear();
        AvailableOuFilters.Add(AllOuFilterValue);
        SelectedOuFilter = AllOuFilterValue;
    }

    private void ApplyOuFilter()
    {
        RefreshFilteredMachinesAndAggregates();
    }

    private void RefreshFilteredMachinesAndAggregates()
    {
        FilteredMachines.Refresh();

        var filteredMachines = GetVisibleMachines().ToList();

        HasMachines = filteredMachines.Count > 0;
        UpdateMatchTypeSummary(filteredMachines);
        UpdateComplianceSummary(filteredMachines);
        UpdateMatchTypeWarning(filteredMachines);
        UpdateOuSummary(filteredMachines);
        UpdateOuComplianceBreakdown(filteredMachines);
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    private bool CanExportCsv()
    {
        return !IsLoadingMachines && SelectedPackage is not null && VisibleMachineCount > 0;
    }

    private void ReplaceMachines(IEnumerable<WaptMachine> machines)
    {
        using (FilteredMachines.DeferRefresh())
        {
            Machines.Clear();
            foreach (var machine in machines)
            {
                Machines.Add(machine);
            }
        }
    }

    private bool FilterMachine(object item)
    {
        return item is WaptMachine machine &&
               (IsAllOuFilterSelected() || string.Equals(machine.OuPath, SelectedOuFilter, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<WaptMachine> GetVisibleMachines()
    {
        return FilteredMachines.Cast<WaptMachine>();
    }

    private string BuildDefaultCsvFileName()
    {
        var packageId = SanitizeFileNameSegment(SelectedPackage?.PackageId);
        var filterLabel = IsAllOuFilterSelected() ? "all-ou" : SanitizeFileNameSegment(SelectedOuFilter);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return $"wapt-machines-{packageId}-{filterLabel}-{timestamp}.csv";
    }

    private static string BuildCsvContent(IReadOnlyCollection<WaptMachine> machines)
    {
        var lines = new List<string>
        {
            string.Join(";", new[]
            {
                "PackageId",
                "Hostname",
                "FQDN",
                "OU / Organisation",
                "Version installee",
                "Type de correspondance",
                "Statut conformite",
                "Last seen",
                "Etat hote"
            })
        };

        foreach (var machine in machines)
        {
            lines.Add(string.Join(";", new[]
            {
                EscapeCsvValue(machine.PackageId),
                EscapeCsvValue(machine.Hostname),
                EscapeCsvValue(machine.Fqdn),
                EscapeCsvValue(machine.OrganizationDisplay),
                EscapeCsvValue(machine.InstalledVersion),
                EscapeCsvValue(machine.MatchTypeDisplayLabel),
                EscapeCsvValue(machine.ComplianceStatusDisplayLabel),
                EscapeCsvValue(machine.LastSeen),
                EscapeCsvValue(machine.Status)
            }));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string EscapeCsvValue(string? value)
    {
        var normalizedValue = value ?? string.Empty;
        var requiresQuotes = normalizedValue.Contains(';') ||
                             normalizedValue.Contains('"') ||
                             normalizedValue.Contains('\r') ||
                             normalizedValue.Contains('\n');

        if (!requiresQuotes)
        {
            return normalizedValue;
        }

        return $"\"{normalizedValue.Replace("\"", "\"\"")}\"";
    }

    private static string SanitizeFileNameSegment(string? value)
    {
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? "machines" : value.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(normalizedValue.Length);

        foreach (var character in normalizedValue)
        {
            builder.Append(invalidCharacters.Contains(character) || char.IsWhiteSpace(character) ? '-' : character);
        }

        var sanitizedValue = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitizedValue) ? "machines" : sanitizedValue;
    }

    private bool IsAllOuFilterSelected()
    {
        return string.IsNullOrWhiteSpace(SelectedOuFilter) ||
               string.Equals(SelectedOuFilter, AllOuFilterValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCount(int count, string singularLabel, string pluralLabel)
    {
        var label = count == 1 ? singularLabel : pluralLabel;
        return $"{count} {label}";
    }

    private static string BuildComplianceBreakdown(int compliantCount, int unknownCount, int nonCompliantCount)
    {
        var parts = new List<string>
        {
            FormatCount(compliantCount, "conforme", "conformes"),
            FormatCount(unknownCount, "inconnue", "inconnues")
        };

        if (nonCompliantCount > 0)
        {
            parts.Add(FormatCount(nonCompliantCount, "non conforme", "non conformes"));
        }

        return string.Join(", ", parts);
    }

    private static string? ExtractTechnicalValue(string? technicalDetails, string label)
    {
        if (string.IsNullOrWhiteSpace(technicalDetails))
        {
            return null;
        }

        foreach (var line in technicalDetails.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[label.Length..].Trim();
        }

        return null;
    }

    private static string FormatCacheStatus(string? cacheStatus)
    {
        return cacheStatus?.Trim().ToLowerInvariant() switch
        {
            "memory-hit" => "hit memoire",
            "memory-miss" => "miss memoire",
            "shared-inflight" => "requete partagee",
            null or "" => "non disponible",
            var value => value
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return $"{duration.TotalMilliseconds:0} ms";
    }
}
