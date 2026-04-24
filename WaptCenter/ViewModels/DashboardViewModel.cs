using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Windows;
using WaptCenter.Models;
using WaptCenter.Services;

namespace WaptCenter.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private const string AllOuFilterValue = "Toutes les OU";
    private const string AllComplianceFilterValue = "Tous";
    private const string CompliantFilterValue = "Conforme";
    private const string UnknownFallbackFilterValue = "Inconnu / fallback";
    private const string NonCompliantFilterValue = "Non conforme";

    private readonly ConfigService _configService;
    private readonly WaptBridgePackageService _waptBridgePackageService;
    private readonly WaptBridgeMachineService _waptBridgeMachineService;
    private readonly List<PackageAnalysis> _allPackageAnalyses = [];
    private readonly Dictionary<string, MachineAggregate> _machineAggregates = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _technicalDetailsBuilder = new();

    private CancellationTokenSource? _loadDashboardCancellationTokenSource;

    public DashboardViewModel(
        ConfigService configService,
        WaptBridgePackageService waptBridgePackageService,
        WaptBridgeMachineService waptBridgeMachineService)
    {
        _configService = configService;
        _waptBridgePackageService = waptBridgePackageService;
        _waptBridgeMachineService = waptBridgeMachineService;

        ComplianceFilters.Add(AllComplianceFilterValue);
        ComplianceFilters.Add(CompliantFilterValue);
        ComplianceFilters.Add(UnknownFallbackFilterValue);
        ComplianceFilters.Add(NonCompliantFilterValue);

        ResetDashboardState(preserveFilters: false);
    }

    public ObservableCollection<DashboardPackageSummary> PackageSummaries { get; } = [];

    public ObservableCollection<DashboardOuSummary> OuSummaries { get; } = [];

    public ObservableCollection<string> AvailableOuFilters { get; } = [];

    public ObservableCollection<string> ComplianceFilters { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshDashboardCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelLoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportPackageSummaryCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportOuSummaryCsvCommand))]
    private bool isLoadingDashboard;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string technicalDetails = string.Empty;

    [ObservableProperty]
    private bool isStatusError;

    [ObservableProperty]
    private string currentPackageLabel = string.Empty;

    [ObservableProperty]
    private int totalPackageCount;

    [ObservableProperty]
    private int analyzedPackageCount;

    [ObservableProperty]
    private int failedPackageCount;

    [ObservableProperty]
    private int totalMachineCount;

    [ObservableProperty]
    private int totalDistinctOuCount;

    [ObservableProperty]
    private int compliantMachineCount;

    [ObservableProperty]
    private int unknownMachineCount;

    [ObservableProperty]
    private int nonCompliantMachineCount;

    [ObservableProperty]
    private bool hasPackageSummaries;

    [ObservableProperty]
    private bool hasOuSummaries;

    [ObservableProperty]
    private string packageSearchText = string.Empty;

    [ObservableProperty]
    private string selectedOuFilter = AllOuFilterValue;

    [ObservableProperty]
    private string selectedComplianceFilter = AllComplianceFilterValue;

    [RelayCommand(CanExecute = nameof(CanRefreshDashboard))]
    private async Task RefreshDashboardAsync()
    {
        CancelPendingLoad();
        ResetDashboardState();

        var cancellationTokenSource = new CancellationTokenSource();
        _loadDashboardCancellationTokenSource = cancellationTokenSource;
        IsLoadingDashboard = true;
        StatusMessage = "Chargement des paquets cd48 pour le tableau de bord...";
        CurrentPackageLabel = "Lecture de la liste des paquets cd48...";

        try
        {
            var config = _configService.Load();
            var packages = await _waptBridgePackageService.GetCd48PackagesAsync(config, cancellationTokenSource.Token);
            var orderedPackages = packages
                .OrderBy(packageItem => packageItem.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            TotalPackageCount = orderedPackages.Count;
            AppendTechnicalDetailsBlock("Packages bridge diagnostics", _waptBridgePackageService.LastTechnicalDetails);
            AppendTechnicalDetailsLine($"Packages cd48 returned: {TotalPackageCount}");
            ApplyDashboardFilters();

            if (orderedPackages.Count == 0)
            {
                StatusMessage = "Aucun paquet cd48 n'a ete trouve pour alimenter le tableau de bord.";
                CurrentPackageLabel = "Aucun paquet a analyser.";
                return;
            }

            foreach (var packageItem in orderedPackages)
            {
                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                CurrentPackageLabel = packageItem.PackageId;
                StatusMessage =
                    $"Analyse du paquet {AnalyzedPackageCount + 1}/{TotalPackageCount} : '{packageItem.PackageId}'...";

                await AnalysePackageAsync(config, packageItem, cancellationTokenSource.Token);

                AnalyzedPackageCount++;
                UpdateOuFilterOptions();
                ApplyDashboardFilters();
                RefreshGlobalCounters();
                HasPackageSummaries = PackageSummaries.Count > 0;
                HasOuSummaries = OuSummaries.Count > 0;
            }

            CurrentPackageLabel = FailedPackageCount > 0
                ? "Analyse terminee avec erreurs partielles."
                : "Analyse terminee.";
            StatusMessage = FailedPackageCount > 0
                ? $"{AnalyzedPackageCount} paquet(s) cd48 analyses, dont {FailedPackageCount} avec erreur de chargement machines."
                : $"{AnalyzedPackageCount} paquet(s) cd48 analyses pour le tableau de bord.";
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            IsStatusError = false;
            CurrentPackageLabel = "Analyse annulee.";
            StatusMessage =
                $"Chargement annule apres {AnalyzedPackageCount}/{TotalPackageCount} paquet(s) traites.";
            AppendTechnicalDetailsLine("Dashboard load cancelled by user.");
        }
        catch (InvalidOperationException exception)
        {
            IsStatusError = true;
            CurrentPackageLabel = "Analyse interrompue.";
            StatusMessage = exception.Message;
            AppendTechnicalDetailsBlock(
                "Dashboard fatal diagnostics",
                ResolveBestTechnicalDetails(exception));
        }
        catch (Exception exception)
        {
            IsStatusError = true;
            CurrentPackageLabel = "Analyse interrompue.";
            StatusMessage = "Une erreur inattendue est survenue lors du chargement du tableau de bord.";
            AppendTechnicalDetailsBlock(
                "Dashboard unexpected diagnostics",
                ResolveBestTechnicalDetails(exception));
        }
        finally
        {
            if (ReferenceEquals(_loadDashboardCancellationTokenSource, cancellationTokenSource))
            {
                _loadDashboardCancellationTokenSource = null;
                IsLoadingDashboard = false;
            }

            cancellationTokenSource.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelLoad))]
    private void CancelLoad()
    {
        CancelPendingLoad();
    }

    public void CancelLoading()
    {
        CancelPendingLoad();
    }

    private bool CanRefreshDashboard()
    {
        return !IsLoadingDashboard;
    }

    private bool CanCancelLoad()
    {
        return IsLoadingDashboard;
    }

    [RelayCommand(CanExecute = nameof(CanExportPackageSummaryCsv))]
    private void ExportPackageSummaryCsv()
    {
        ExportCsv(
            PackageSummaries.ToList(),
            "Exporter la synthese paquets",
            BuildPackageSummaryCsvContent,
            BuildExportFileName("dashboard-paquets"),
            "synthese paquets");
    }

    [RelayCommand(CanExecute = nameof(CanExportOuSummaryCsv))]
    private void ExportOuSummaryCsv()
    {
        ExportCsv(
            OuSummaries.ToList(),
            "Exporter la synthese OU",
            BuildOuSummaryCsvContent,
            BuildExportFileName("dashboard-ou"),
            "synthese OU");
    }

    partial void OnPackageSearchTextChanged(string value)
    {
        ApplyDashboardFilters();
    }

    partial void OnSelectedOuFilterChanged(string value)
    {
        ApplyDashboardFilters();
    }

    partial void OnSelectedComplianceFilterChanged(string value)
    {
        ApplyDashboardFilters();
    }

    private async Task AnalysePackageAsync(
        WaptConfig config,
        WaptPackage packageItem,
        CancellationToken cancellationToken)
    {
        try
        {
            var machines = await _waptBridgeMachineService.GetMachinesForPackageAsync(
                config,
                packageItem.PackageId,
                cancellationToken);

            var uniqueMachines = DeduplicateMachines(machines).ToList();
            _allPackageAnalyses.Add(new PackageAnalysis(packageItem, uniqueMachines));

            var packageSummary = BuildPackageSummary(packageItem, uniqueMachines);
            UpdateMachineAggregates(packageItem.PackageId, uniqueMachines);

            AppendTechnicalDetailsLine(
                $"[{packageItem.PackageId}] {packageSummary.MachineCount} machine(s), " +
                $"{packageSummary.CompliantCount} conformes, " +
                $"{packageSummary.UnknownCount} inconnues, " +
                $"{packageSummary.NonCompliantCount} non conformes, " +
                $"{packageSummary.DistinctOuCount} OU distinctes.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            FailedPackageCount++;
            _allPackageAnalyses.Add(new PackageAnalysis(packageItem, []));
            AppendTechnicalDetailsLine($"[{packageItem.PackageId}] erreur: {exception.Message}");
            AppendTechnicalDetailsBlock(
                $"Machine bridge diagnostics - {packageItem.PackageId}",
                string.IsNullOrWhiteSpace(_waptBridgeMachineService.LastTechnicalDetails)
                    ? exception.ToString()
                    : _waptBridgeMachineService.LastTechnicalDetails);
        }
    }

    private static DashboardPackageSummary BuildPackageSummary(
        WaptPackage packageItem,
        IReadOnlyCollection<WaptMachine> machines)
    {
        var uniqueMachines = DeduplicateMachines(machines).ToList();

        return new DashboardPackageSummary
        {
            PackageId = packageItem.PackageId,
            Name = packageItem.Name,
            Version = packageItem.Version,
            MachineCount = uniqueMachines.Count,
            CompliantCount = uniqueMachines.Count(machine => machine.IsCompliant),
            UnknownCount = uniqueMachines.Count(machine => machine.IsComplianceUnknown),
            NonCompliantCount = uniqueMachines.Count(machine => machine.IsNonCompliant),
            DistinctOuCount = uniqueMachines
                .Select(ResolveOuDisplay)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
        };
    }

    private static IEnumerable<WaptMachine> DeduplicateMachines(IEnumerable<WaptMachine> machines)
    {
        var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var machine in machines)
        {
            var machineIdentity = BuildMachineIdentity(machine);
            if (seenIdentities.Add(machineIdentity))
            {
                yield return machine;
            }
        }
    }

    private void ApplyDashboardFilters()
    {
        var filteredPackageAnalyses = _allPackageAnalyses
            .Where(PackageMatchesSearch)
            .Select(packageAnalysis => new FilteredPackageAnalysis(
                packageAnalysis,
                FilterMachines(packageAnalysis.Machines).ToList()))
            .Where(ShouldIncludePackageAnalysis)
            .OrderBy(result => result.Analysis.Package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var visiblePackageSummaries = filteredPackageAnalyses
            .Select(result => BuildPackageSummary(result.Analysis.Package, result.FilteredMachines))
            .ToList();

        var visibleOuSummaries = BuildOuSummaries(filteredPackageAnalyses);

        ReplaceCollection(PackageSummaries, visiblePackageSummaries);
        ReplaceCollection(OuSummaries, visibleOuSummaries);

        HasPackageSummaries = PackageSummaries.Count > 0;
        HasOuSummaries = OuSummaries.Count > 0;
        ExportPackageSummaryCsvCommand.NotifyCanExecuteChanged();
        ExportOuSummaryCsvCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<WaptMachine> FilterMachines(IEnumerable<WaptMachine> machines)
    {
        return machines.Where(machine =>
            MatchesOuFilter(machine) &&
            MatchesComplianceFilter(machine));
    }

    private bool PackageMatchesSearch(PackageAnalysis packageAnalysis)
    {
        if (string.IsNullOrWhiteSpace(PackageSearchText))
        {
            return true;
        }

        return packageAnalysis.Package.PackageId.Contains(PackageSearchText, StringComparison.OrdinalIgnoreCase) ||
               packageAnalysis.Package.Name.Contains(PackageSearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldIncludePackageAnalysis(FilteredPackageAnalysis filteredAnalysis)
    {
        if (HasMachineLevelFilters())
        {
            return filteredAnalysis.FilteredMachines.Count > 0;
        }

        return true;
    }

    private bool MatchesOuFilter(WaptMachine machine)
    {
        return IsAllOuFilterSelected() ||
               string.Equals(ResolveOuDisplay(machine), SelectedOuFilter, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesComplianceFilter(WaptMachine machine)
    {
        return SelectedComplianceFilter switch
        {
            CompliantFilterValue => machine.IsCompliant,
            UnknownFallbackFilterValue => machine.IsComplianceUnknown,
            NonCompliantFilterValue => machine.IsNonCompliant,
            _ => true
        };
    }

    private bool IsAllOuFilterSelected()
    {
        return string.IsNullOrWhiteSpace(SelectedOuFilter) ||
               string.Equals(SelectedOuFilter, AllOuFilterValue, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasMachineLevelFilters()
    {
        return !IsAllOuFilterSelected() ||
               !string.Equals(SelectedComplianceFilter, AllComplianceFilterValue, StringComparison.OrdinalIgnoreCase);
    }

    private List<DashboardOuSummary> BuildOuSummaries(IReadOnlyCollection<FilteredPackageAnalysis> filteredPackageAnalyses)
    {
        var filteredEntries = filteredPackageAnalyses
            .SelectMany(filteredAnalysis => filteredAnalysis.FilteredMachines.Select(machine =>
                new FilteredMachineEntry(filteredAnalysis.Analysis.Package.PackageId, machine)))
            .ToList();

        return filteredEntries
            .GroupBy(entry => ResolveOuDisplay(entry.Machine), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var distinctMachines = group
                    .GroupBy(entry => BuildMachineIdentity(entry.Machine), StringComparer.OrdinalIgnoreCase)
                    .Select(machineGroup => machineGroup.Max(entry => ResolveComplianceRank(entry.Machine)))
                    .ToList();

                return new DashboardOuSummary
                {
                    OrganizationDisplay = group.Key,
                    MachineCount = distinctMachines.Count,
                    PackageCount = group.Select(entry => entry.PackageId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    CompliantCount = distinctMachines.Count(rank => rank == 1),
                    UnknownCount = distinctMachines.Count(rank => rank == 0),
                    NonCompliantCount = distinctMachines.Count(rank => rank == 2)
                };
            })
            .OrderByDescending(summary => summary.MachineCount)
            .ThenBy(summary => summary.OrganizationDisplay, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateMachineAggregates(string packageId, IEnumerable<WaptMachine> machines)
    {
        foreach (var machine in DeduplicateMachines(machines))
        {
            var machineIdentity = BuildMachineIdentity(machine);
            if (!_machineAggregates.TryGetValue(machineIdentity, out var aggregate))
            {
                aggregate = new MachineAggregate();
                _machineAggregates[machineIdentity] = aggregate;
            }

            aggregate.Apply(machine, packageId);
        }
    }

    private void RefreshGlobalCounters()
    {
        TotalMachineCount = _machineAggregates.Count;
        TotalDistinctOuCount = _machineAggregates.Values
            .Select(machineAggregate => machineAggregate.OrganizationDisplay)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        CompliantMachineCount = _machineAggregates.Values.Count(machineAggregate =>
            string.Equals(
                machineAggregate.ComplianceStatus,
                WaptMachine.CompliantComplianceStatus,
                StringComparison.OrdinalIgnoreCase));
        UnknownMachineCount = _machineAggregates.Values.Count(machineAggregate =>
            string.Equals(
                machineAggregate.ComplianceStatus,
                WaptMachine.UnknownComplianceStatus,
                StringComparison.OrdinalIgnoreCase));
        NonCompliantMachineCount = _machineAggregates.Values.Count(machineAggregate =>
            string.Equals(
                machineAggregate.ComplianceStatus,
                WaptMachine.NonCompliantComplianceStatus,
                StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateOuFilterOptions()
    {
        var currentSelection = SelectedOuFilter;
        var distinctOuPaths = _allPackageAnalyses
            .SelectMany(packageAnalysis => packageAnalysis.Machines)
            .Select(ResolveOuDisplay)
            .Where(ouDisplay => !string.IsNullOrWhiteSpace(ouDisplay))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ouDisplay => ouDisplay, StringComparer.OrdinalIgnoreCase)
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

    private void ResetDashboardState(bool preserveFilters = true)
    {
        PackageSummaries.Clear();
        OuSummaries.Clear();
        _allPackageAnalyses.Clear();
        _machineAggregates.Clear();
        _technicalDetailsBuilder.Clear();

        StatusMessage = "Utilisez 'Actualiser le tableau de bord' pour charger une synthese globale des paquets cd48.";
        TechnicalDetails = string.Empty;
        IsStatusError = false;
        CurrentPackageLabel = "Aucun paquet en cours d'analyse.";
        TotalPackageCount = 0;
        AnalyzedPackageCount = 0;
        FailedPackageCount = 0;
        TotalMachineCount = 0;
        TotalDistinctOuCount = 0;
        CompliantMachineCount = 0;
        UnknownMachineCount = 0;
        NonCompliantMachineCount = 0;
        HasPackageSummaries = false;
        HasOuSummaries = false;

        AvailableOuFilters.Clear();
        AvailableOuFilters.Add(AllOuFilterValue);

        if (!preserveFilters)
        {
            PackageSearchText = string.Empty;
            SelectedOuFilter = AllOuFilterValue;
            SelectedComplianceFilter = AllComplianceFilterValue;
        }

        ExportPackageSummaryCsvCommand.NotifyCanExecuteChanged();
        ExportOuSummaryCsvCommand.NotifyCanExecuteChanged();
    }

    private void CancelPendingLoad()
    {
        if (_loadDashboardCancellationTokenSource is null)
        {
            return;
        }

        _loadDashboardCancellationTokenSource.Cancel();
        _loadDashboardCancellationTokenSource.Dispose();
        _loadDashboardCancellationTokenSource = null;
    }

    private void AppendTechnicalDetailsLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (_technicalDetailsBuilder.Length > 0)
        {
            _technicalDetailsBuilder.AppendLine();
        }

        _technicalDetailsBuilder.Append(line.Trim());
        TechnicalDetails = _technicalDetailsBuilder.ToString();
    }

    private void AppendTechnicalDetailsBlock(string title, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        if (_technicalDetailsBuilder.Length > 0)
        {
            _technicalDetailsBuilder.AppendLine();
            _technicalDetailsBuilder.AppendLine();
        }

        _technicalDetailsBuilder.AppendLine(title.Trim());
        _technicalDetailsBuilder.AppendLine(content.Trim());
        TechnicalDetails = _technicalDetailsBuilder.ToString().TrimEnd();
    }

    private string ResolveBestTechnicalDetails(Exception exception)
    {
        if (!string.IsNullOrWhiteSpace(_waptBridgeMachineService.LastTechnicalDetails))
        {
            return _waptBridgeMachineService.LastTechnicalDetails;
        }

        if (!string.IsNullOrWhiteSpace(_waptBridgePackageService.LastTechnicalDetails))
        {
            return _waptBridgePackageService.LastTechnicalDetails;
        }

        return exception.ToString();
    }

    private static string ResolveOuDisplay(WaptMachine machine)
    {
        return FirstNonEmpty(
            machine.OrganizationDisplay,
            machine.OuPath,
            machine.OrganizationalUnit,
            WaptMachine.UnknownOuPath);
    }

    private static string BuildMachineIdentity(WaptMachine machine)
    {
        return FirstNonEmpty(
            machine.Uuid?.ToLowerInvariant(),
            machine.Fqdn?.ToLowerInvariant(),
            machine.Hostname?.ToLowerInvariant(),
            $"{ResolveOuDisplay(machine).ToLowerInvariant()}|{machine.PackageId.ToLowerInvariant()}|{machine.InstalledVersion.ToLowerInvariant()}");
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private bool CanExportPackageSummaryCsv()
    {
        return !IsLoadingDashboard && PackageSummaries.Count > 0;
    }

    private bool CanExportOuSummaryCsv()
    {
        return !IsLoadingDashboard && OuSummaries.Count > 0;
    }

    private void ExportCsv<T>(
        IReadOnlyCollection<T> rows,
        string dialogTitle,
        Func<IReadOnlyCollection<T>, string> contentFactory,
        string fileName,
        string exportLabel)
    {
        if (rows.Count == 0)
        {
            var noDataMessage = $"Aucune ligne visible n'est disponible pour l'export de la {exportLabel}.";
            IsStatusError = true;
            StatusMessage = noDataMessage;
            MessageBox.Show(noDataMessage, "Exporter CSV", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = dialogTitle,
            Filter = "CSV UTF-8 (*.csv)|*.csv|Tous les fichiers (*.*)|*.*",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = fileName
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, contentFactory(rows), new UTF8Encoding(true));

            var successMessage = $"{rows.Count} ligne(s) visible(s) exportee(s) dans '{dialog.FileName}'.";
            IsStatusError = false;
            StatusMessage = successMessage;
            MessageBox.Show(successMessage, "Exporter CSV", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            var errorMessage = $"L'export CSV de la {exportLabel} a echoue : {exception.Message}";
            IsStatusError = true;
            StatusMessage = errorMessage;
            MessageBox.Show(errorMessage, "Exporter CSV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string BuildExportFileName(string prefix)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var searchSegment = string.IsNullOrWhiteSpace(PackageSearchText)
            ? "all-packages"
            : SanitizeFileNameSegment(PackageSearchText);
        var ouSegment = IsAllOuFilterSelected()
            ? "all-ou"
            : SanitizeFileNameSegment(SelectedOuFilter);
        var complianceSegment = string.Equals(SelectedComplianceFilter, AllComplianceFilterValue, StringComparison.OrdinalIgnoreCase)
            ? "all-status"
            : SanitizeFileNameSegment(SelectedComplianceFilter);
        return $"{prefix}-{searchSegment}-{ouSegment}-{complianceSegment}-{timestamp}.csv";
    }

    private static string BuildPackageSummaryCsvContent(IReadOnlyCollection<DashboardPackageSummary> summaries)
    {
        var lines = new List<string>
        {
            "PackageId;Nom;Version;Machines;Conformes;Inconnues;Non conformes;OU distinctes"
        };

        foreach (var summary in summaries)
        {
            lines.Add(string.Join(";", new[]
            {
                EscapeCsvValue(summary.PackageId),
                EscapeCsvValue(summary.Name),
                EscapeCsvValue(summary.Version),
                EscapeCsvValue(summary.MachineCount.ToString()),
                EscapeCsvValue(summary.CompliantCount.ToString()),
                EscapeCsvValue(summary.UnknownCount.ToString()),
                EscapeCsvValue(summary.NonCompliantCount.ToString()),
                EscapeCsvValue(summary.DistinctOuCount.ToString())
            }));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildOuSummaryCsvContent(IReadOnlyCollection<DashboardOuSummary> summaries)
    {
        var lines = new List<string>
        {
            "OU / Organisation;Machines;Paquets concernes;Conformes;Inconnues;Non conformes"
        };

        foreach (var summary in summaries)
        {
            lines.Add(string.Join(";", new[]
            {
                EscapeCsvValue(summary.OrganizationDisplay),
                EscapeCsvValue(summary.MachineCount.ToString()),
                EscapeCsvValue(summary.PackageCount.ToString()),
                EscapeCsvValue(summary.CompliantCount.ToString()),
                EscapeCsvValue(summary.UnknownCount.ToString()),
                EscapeCsvValue(summary.NonCompliantCount.ToString())
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
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? "dashboard" : value.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(normalizedValue.Length);

        foreach (var character in normalizedValue)
        {
            builder.Append(invalidCharacters.Contains(character) || char.IsWhiteSpace(character) ? '-' : character);
        }

        var sanitizedValue = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitizedValue) ? "dashboard" : sanitizedValue;
    }

    private static int ResolveComplianceRank(WaptMachine machine)
    {
        if (machine.IsNonCompliant)
        {
            return 2;
        }

        if (machine.IsCompliant)
        {
            return 1;
        }

        return 0;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IReadOnlyCollection<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private sealed class MachineAggregate
    {
        private int _complianceRank;

        public string Hostname { get; private set; } = string.Empty;

        public string Fqdn { get; private set; } = string.Empty;

        public string OrganizationDisplay { get; private set; } = WaptMachine.UnknownOuPath;

        public HashSet<string> PackageIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string ComplianceStatus => _complianceRank switch
        {
            2 => WaptMachine.NonCompliantComplianceStatus,
            1 => WaptMachine.CompliantComplianceStatus,
            _ => WaptMachine.UnknownComplianceStatus
        };

        public void Apply(WaptMachine machine, string packageId)
        {
            Hostname = FirstNonEmpty(Hostname, machine.Hostname);
            Fqdn = FirstNonEmpty(Fqdn, machine.Fqdn);

            var resolvedDisplay = ResolveOuDisplay(machine);
            if (string.Equals(OrganizationDisplay, WaptMachine.UnknownOuPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(resolvedDisplay, WaptMachine.UnknownOuPath, StringComparison.OrdinalIgnoreCase))
            {
                OrganizationDisplay = resolvedDisplay;
            }
            else if (string.IsNullOrWhiteSpace(OrganizationDisplay))
            {
                OrganizationDisplay = resolvedDisplay;
            }

            PackageIds.Add(packageId);
            _complianceRank = Math.Max(_complianceRank, ResolveComplianceRank(machine));
        }

        private static int ResolveComplianceRank(WaptMachine machine)
        {
            if (machine.IsNonCompliant)
            {
                return 2;
            }

            if (machine.IsCompliant)
            {
                return 1;
            }

            return 0;
        }
    }

    private sealed record PackageAnalysis(WaptPackage Package, IReadOnlyList<WaptMachine> Machines);

    private sealed record FilteredPackageAnalysis(PackageAnalysis Analysis, IReadOnlyList<WaptMachine> FilteredMachines);

    private sealed record FilteredMachineEntry(string PackageId, WaptMachine Machine);
}