using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private const string FullAnalysisModeValue = "Analyse complete";
    private const string QuickAnalysisModeValue = "Analyse rapide (10 paquets)";
    private const int QuickAnalysisPackageLimit = 10;
    private const int MaxParallelPackageAnalyses = 2;
    private const int RefreshBatchSize = 2;

    private readonly ConfigService _configService;
    private readonly WaptBridgePackageService _waptBridgePackageService;
    private readonly WaptBridgeMachineService _waptBridgeMachineService;
    private readonly List<PackageAnalysis> _allPackageAnalyses = [];
    private readonly Dictionary<string, MachineAggregate> _machineAggregates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _machineCacheStatusCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _technicalDetailsBuilder = new();

    private CancellationTokenSource? _loadDashboardCancellationTokenSource;
    private bool _resetDashboardOnCancellation;
    private int _pendingVisibleRefreshCount;
    private int _successfulMachineLoadCount;
    private int _emptySuccessfulMachineLoadCount;
    private int _totalRawMachineRowsReturned;

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
        AnalysisModes.Add(FullAnalysisModeValue);
        AnalysisModes.Add(QuickAnalysisModeValue);

        ResetDashboardState(preserveFilters: false);
    }

    public ObservableCollection<DashboardPackageSummary> PackageSummaries { get; } = [];

    public ObservableCollection<DashboardOuSummary> OuSummaries { get; } = [];

    public ObservableCollection<string> AvailableOuFilters { get; } = [];

    public ObservableCollection<string> ComplianceFilters { get; } = [];

    public ObservableCollection<string> AnalysisModes { get; } = [];

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
    private int availablePackageCount;

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

    [ObservableProperty]
    private string selectedAnalysisMode = FullAnalysisModeValue;

    [ObservableProperty]
    private string dashboardPackageLoadDurationMetric = "Duree paquets : -";

    [ObservableProperty]
    private string dashboardAnalysisDurationMetric = "Duree analyse dashboard : -";

    [ObservableProperty]
    private string dashboardPackageCacheStatusMetric = "Cache paquets : -";

    [ObservableProperty]
    private string dashboardMachineCacheStatusMetric = "Cache machines : -";

    [ObservableProperty]
    private string dashboardMachineCallStatusMetric = "Appels machines : -";

    [ObservableProperty]
    private string dashboardMachineReturnedRowsMetric = "Machines retournees : -";

    [RelayCommand(CanExecute = nameof(CanRefreshDashboard))]
    private async Task RefreshDashboardAsync()
    {
        CancelPendingLoad();
        ResetDashboardState();
        _resetDashboardOnCancellation = false;

        var totalStopwatch = Stopwatch.StartNew();
        var cancellationTokenSource = new CancellationTokenSource();
        _loadDashboardCancellationTokenSource = cancellationTokenSource;
        IsLoadingDashboard = true;
        StatusMessage = "Chargement des paquets cd48 pour le tableau de bord...";
        CurrentPackageLabel = "Lecture de la liste des paquets cd48...";

        try
        {
            var config = _configService.Load();
            var packagesBridgeStopwatch = Stopwatch.StartNew();
            var packages = await _waptBridgePackageService.GetCd48PackagesAsync(config, cancellationTokenSource.Token);
            packagesBridgeStopwatch.Stop();
            DashboardPackageLoadDurationMetric = $"Duree chargement paquets : {FormatDuration(packagesBridgeStopwatch.Elapsed)}";
            DashboardPackageCacheStatusMetric = $"Cache paquets : {FormatCacheStatus(ExtractTechnicalValue(_waptBridgePackageService.LastTechnicalDetails, "Cache status:"))}";

            var orderedPackages = packages
                .OrderBy(packageItem => packageItem.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var packagesToAnalyze = SelectPackagesForAnalysis(orderedPackages);

            AvailablePackageCount = orderedPackages.Count;
            TotalPackageCount = packagesToAnalyze.Count;
            AppendTechnicalDetailsBlock("Packages bridge diagnostics", _waptBridgePackageService.LastTechnicalDetails);
            AppendTechnicalDetailsLine($"Packages cd48 returned: {AvailablePackageCount}");
            AppendTechnicalDetailsLine($"Dashboard analysis mode: {SelectedAnalysisMode}");
            AppendTechnicalDetailsLine($"Packages scheduled for analysis: {TotalPackageCount}");
            AppendTechnicalDetailsLine($"Dashboard packages bridge duration: {FormatDuration(packagesBridgeStopwatch.Elapsed)}");
            AppendTechnicalDetailsLine($"Dashboard max parallel analyses: {MaxParallelPackageAnalyses}");
            AppendTechnicalDetailsLine($"Dashboard refresh batch size: {RefreshBatchSize}");
            AppendTechnicalDetailsLine($"Dashboard machine service instance: {_waptBridgeMachineService.GetHashCode()}");

            if (TotalPackageCount != AvailablePackageCount)
            {
                AppendTechnicalDetailsLine(
                    $"Quick analysis limit applied: {TotalPackageCount}/{AvailablePackageCount} paquet(s) programmes.");
            }

            if (packagesToAnalyze.Count == 0)
            {
                StatusMessage = "Aucun paquet cd48 n'a ete trouve pour alimenter le tableau de bord.";
                CurrentPackageLabel = "Aucun paquet a analyser.";
                return;
            }

            var throttler = new SemaphoreSlim(MaxParallelPackageAnalyses, MaxParallelPackageAnalyses);
            var pendingAnalyses = packagesToAnalyze
                .Select(packageItem => AnalysePackageAsync(config, packageItem, throttler, cancellationTokenSource.Token))
                .ToList();

            while (pendingAnalyses.Count > 0)
            {
                var completedAnalysisTask = await Task.WhenAny(pendingAnalyses);
                pendingAnalyses.Remove(completedAnalysisTask);

                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                var analysisResult = await completedAnalysisTask;
                RegisterPackageAnalysisResult(analysisResult);

                if (_pendingVisibleRefreshCount >= RefreshBatchSize || pendingAnalyses.Count == 0)
                {
                    FlushVisibleDashboardRefresh();
                }
            }

            totalStopwatch.Stop();
            DashboardAnalysisDurationMetric = $"Duree analyse dashboard : {FormatDuration(totalStopwatch.Elapsed)}";
            AppendTechnicalDetailsLine($"Dashboard total duration: {FormatDuration(totalStopwatch.Elapsed)}");

            CurrentPackageLabel = FailedPackageCount > 0
                ? "Analyse terminee avec erreurs partielles."
                : "Analyse terminee.";
            StatusMessage = BuildDashboardCompletionMessage();
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            totalStopwatch.Stop();
            DashboardAnalysisDurationMetric = $"Duree analyse dashboard : {FormatDuration(totalStopwatch.Elapsed)}";

            if (_resetDashboardOnCancellation)
            {
                ResetDashboardState();
            }
            else
            {
                FlushVisibleDashboardRefresh();
                IsStatusError = false;
                CurrentPackageLabel = "Analyse annulee.";
                StatusMessage =
                    $"Chargement annule apres {AnalyzedPackageCount}/{TotalPackageCount} paquet(s) programmes.";
                AppendTechnicalDetailsLine($"Dashboard load cancelled by user after {FormatDuration(totalStopwatch.Elapsed)}.");
            }
        }
        catch (InvalidOperationException exception)
        {
            totalStopwatch.Stop();
            DashboardAnalysisDurationMetric = $"Duree analyse dashboard : {FormatDuration(totalStopwatch.Elapsed)}";
            FlushVisibleDashboardRefresh();
            IsStatusError = true;
            CurrentPackageLabel = "Analyse interrompue.";
            StatusMessage = exception.Message;
            AppendTechnicalDetailsBlock(
                "Dashboard fatal diagnostics",
                ResolveBestTechnicalDetails(exception));
        }
        catch (Exception exception)
        {
            totalStopwatch.Stop();
            DashboardAnalysisDurationMetric = $"Duree analyse dashboard : {FormatDuration(totalStopwatch.Elapsed)}";
            FlushVisibleDashboardRefresh();
            IsStatusError = true;
            CurrentPackageLabel = "Analyse interrompue.";
            StatusMessage = "Une erreur inattendue est survenue lors du chargement du tableau de bord.";
            AppendTechnicalDetailsBlock(
                "Dashboard unexpected diagnostics",
                ResolveBestTechnicalDetails(exception));
        }
        finally
        {
            _pendingVisibleRefreshCount = 0;

            if (ReferenceEquals(_loadDashboardCancellationTokenSource, cancellationTokenSource))
            {
                _loadDashboardCancellationTokenSource = null;
                IsLoadingDashboard = false;
            }

            _resetDashboardOnCancellation = false;

            cancellationTokenSource.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelLoad))]
    private void CancelLoad()
    {
        if (_loadDashboardCancellationTokenSource is null)
        {
            return;
        }

        _resetDashboardOnCancellation = true;
        ResetDashboardState();
        StatusMessage = "Annulation du tableau de bord en cours...";
        CurrentPackageLabel = "Remise a zero en cours...";
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

    private async Task<PackageAnalysisResult> AnalysePackageAsync(
        WaptConfig config,
        WaptPackage packageItem,
        SemaphoreSlim throttler,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();

        await throttler.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var requestedPackageId = packageItem.PackageId;
            var bridgeStopwatch = Stopwatch.StartNew();
            var machineResult = await _waptBridgeMachineService.GetMachineResultForPackageAsync(
                config,
                requestedPackageId,
                cancellationToken).ConfigureAwait(false);
            bridgeStopwatch.Stop();

            var machines = machineResult.Machines;
            var technicalDetails = machineResult.TechnicalDetails;
            var rawMachineCount = machines.Count;
            var machineLoadMessage = FirstNonEmpty(
                ExtractTechnicalValue(technicalDetails, "Machine bridge response message:"),
                rawMachineCount == 0
                    ? "Bridge machines termine avec succes et liste machines vide."
                    : $"Bridge machines termine avec succes, {rawMachineCount} machine(s) retournee(s).");
            var machineStrategy = ResolveMachineStrategy(technicalDetails, machineLoadMessage);

            var processingStopwatch = Stopwatch.StartNew();
            var uniqueMachines = DeduplicateMachines(machines).ToList();
            var packageSummary = BuildPackageSummaryFromUniqueMachines(
                packageItem,
                uniqueMachines,
                "OK",
                machineStrategy,
                machineLoadMessage);
            processingStopwatch.Stop();
            totalStopwatch.Stop();

            return new PackageAnalysisResult(
                packageItem,
                uniqueMachines,
                packageSummary,
                technicalDetails,
                rawMachineCount,
                true,
                machineStrategy,
                machineLoadMessage,
                bridgeStopwatch.Elapsed,
                processingStopwatch.Elapsed,
                totalStopwatch.Elapsed,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            totalStopwatch.Stop();
            var technicalDetails = exception is WaptBridgeMachineService.WaptBridgeMachinesException bridgeException &&
                                   !string.IsNullOrWhiteSpace(bridgeException.TechnicalDetails)
                ? bridgeException.TechnicalDetails
                : string.IsNullOrWhiteSpace(_waptBridgeMachineService.LastTechnicalDetails)
                ? exception.ToString()
                : _waptBridgeMachineService.LastTechnicalDetails;

            return new PackageAnalysisResult(
                packageItem,
                [],
                null,
                technicalDetails,
                0,
                false,
                ResolveMachineStrategy(technicalDetails, exception.Message),
                exception.Message,
                TimeSpan.Zero,
                TimeSpan.Zero,
                totalStopwatch.Elapsed,
                exception.Message);
        }
        finally
        {
            throttler.Release();
        }
    }

    private static DashboardPackageSummary BuildPackageSummary(
        WaptPackage packageItem,
        IReadOnlyCollection<WaptMachine> machines)
    {
        var uniqueMachines = DeduplicateMachines(machines).ToList();

        return BuildPackageSummaryFromUniqueMachines(packageItem, uniqueMachines);
    }

    private static DashboardPackageSummary BuildPackageSummaryFromUniqueMachines(
        WaptPackage packageItem,
        IReadOnlyCollection<WaptMachine> uniqueMachines,
        string machineLoadStatus = "OK",
        string machineStrategy = "",
        string machineLoadMessage = "")
    {
        return new DashboardPackageSummary
        {
            PackageId = packageItem.PackageId,
            Name = packageItem.Name,
            Version = packageItem.Version,
            MachineCount = uniqueMachines.Count,
            CompliantCount = uniqueMachines.Count(machine => string.Equals(machine.ComplianceStatus, WaptMachine.CompliantComplianceStatus, StringComparison.OrdinalIgnoreCase)),
            UnknownCount = uniqueMachines.Count(machine => string.Equals(machine.ComplianceStatus, WaptMachine.UnknownComplianceStatus, StringComparison.OrdinalIgnoreCase) || (!machine.IsCompliant && !machine.IsNonCompliant)),
            NonCompliantCount = uniqueMachines.Count(machine => string.Equals(machine.ComplianceStatus, WaptMachine.NonCompliantComplianceStatus, StringComparison.OrdinalIgnoreCase)),
            DistinctOuCount = uniqueMachines
                .Select(ResolveOuDisplay)
                .Where(ouDisplay => !string.IsNullOrWhiteSpace(ouDisplay))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            MachineLoadStatus = machineLoadStatus,
            MachineStrategy = machineStrategy,
            MachineLoadMessage = machineLoadMessage
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
        var hasMachineLevelFilters = HasMachineLevelFilters();
        var filteredPackageAnalyses = _allPackageAnalyses
            .Where(PackageMatchesSearch)
            .Select(packageAnalysis => new FilteredPackageAnalysis(
                packageAnalysis,
                hasMachineLevelFilters
                    ? FilterMachines(packageAnalysis.Machines).ToList()
                    : packageAnalysis.Machines))
            .Where(ShouldIncludePackageAnalysis)
            .OrderBy(result => result.Analysis.Package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var visiblePackageSummaries = filteredPackageAnalyses
            .Select(result => BuildPackageSummaryFromUniqueMachines(
                result.Analysis.Package,
                result.FilteredMachines,
                result.Analysis.MachineLoadStatus,
                result.Analysis.MachineStrategy,
                result.Analysis.MachineLoadMessage))
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
        foreach (var machine in machines)
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
        _machineCacheStatusCounts.Clear();
        _technicalDetailsBuilder.Clear();
        _pendingVisibleRefreshCount = 0;
        _successfulMachineLoadCount = 0;
        _emptySuccessfulMachineLoadCount = 0;
        _totalRawMachineRowsReturned = 0;

        StatusMessage = "Utilisez 'Actualiser le tableau de bord' pour charger une synthese globale des paquets cd48.";
        TechnicalDetails = string.Empty;
        IsStatusError = false;
        CurrentPackageLabel = "Aucun paquet en cours d'analyse.";
        TotalPackageCount = 0;
        AvailablePackageCount = 0;
        AnalyzedPackageCount = 0;
        FailedPackageCount = 0;
        TotalMachineCount = 0;
        TotalDistinctOuCount = 0;
        CompliantMachineCount = 0;
        UnknownMachineCount = 0;
        NonCompliantMachineCount = 0;
        HasPackageSummaries = false;
        HasOuSummaries = false;
        DashboardPackageLoadDurationMetric = "Duree paquets : -";
        DashboardAnalysisDurationMetric = "Duree analyse dashboard : -";
        DashboardPackageCacheStatusMetric = "Cache paquets : -";
        DashboardMachineCacheStatusMetric = "Cache machines : -";
        DashboardMachineCallStatusMetric = "Appels machines : -";
        DashboardMachineReturnedRowsMetric = "Machines retournees : -";

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
    }

    private List<WaptPackage> SelectPackagesForAnalysis(IReadOnlyList<WaptPackage> orderedPackages)
    {
        if (string.Equals(SelectedAnalysisMode, QuickAnalysisModeValue, StringComparison.OrdinalIgnoreCase))
        {
            return orderedPackages.Take(QuickAnalysisPackageLimit).ToList();
        }

        return orderedPackages.ToList();
    }

    private void RegisterPackageAnalysisResult(PackageAnalysisResult analysisResult)
    {
        AnalyzedPackageCount++;
        CurrentPackageLabel = analysisResult.Package.PackageId;

        if (analysisResult.IsSuccess)
        {
            _successfulMachineLoadCount++;
            _totalRawMachineRowsReturned += analysisResult.RawMachineCount;
            if (analysisResult.RawMachineCount == 0)
            {
                _emptySuccessfulMachineLoadCount++;
            }

            _allPackageAnalyses.Add(new PackageAnalysis(
                analysisResult.Package,
                analysisResult.Machines,
                analysisResult.MachineLoadSucceeded,
                "OK",
                analysisResult.MachineStrategy,
                analysisResult.MachineLoadMessage));
            UpdateMachineAggregates(analysisResult.Package.PackageId, analysisResult.Machines);

            var packageSummary = analysisResult.Summary!;
            var cacheStatus = NormalizeCacheStatus(ExtractTechnicalValue(analysisResult.TechnicalDetails, "Cache status:"));
            RegisterMachineCacheStatus(cacheStatus);

            AppendTechnicalDetailsLine(
                $"[{analysisResult.Package.PackageId}] machine_call_success=true, package_id={analysisResult.Package.PackageId}, " +
                $"raw_machines={analysisResult.RawMachineCount}, unique_machines={packageSummary.MachineCount}, " +
                $"{packageSummary.CompliantCount} conformes, " +
                $"{packageSummary.UnknownCount} inconnues, " +
                $"{packageSummary.NonCompliantCount} non conformes, " +
                $"{packageSummary.DistinctOuCount} OU distinctes, " +
                $"strategy={FormatValueOrPlaceholder(analysisResult.MachineStrategy)}, " +
                $"message={FormatValueOrPlaceholder(analysisResult.MachineLoadMessage)}, " +
                $"cache={cacheStatus}, " +
                $"bridge={FormatDuration(analysisResult.BridgeDuration)}, " +
                $"traitement={FormatDuration(analysisResult.ProcessingDuration)}, " +
                $"total={FormatDuration(analysisResult.TotalDuration)}.");
        }
        else
        {
            RegisterMachineCacheStatus(NormalizeCacheStatus(ExtractTechnicalValue(analysisResult.TechnicalDetails, "Cache status:")));
            FailedPackageCount++;
            _allPackageAnalyses.Add(new PackageAnalysis(
                analysisResult.Package,
                [],
                analysisResult.MachineLoadSucceeded,
                "Erreur",
                analysisResult.MachineStrategy,
                analysisResult.ErrorMessage ?? "Erreur machines sans message detaille."));
            AppendTechnicalDetailsLine(
                $"[{analysisResult.Package.PackageId}] machine_call_success=false, package_id={analysisResult.Package.PackageId}, " +
                $"raw_machines=0, unique_machines=0, strategy={FormatValueOrPlaceholder(analysisResult.MachineStrategy)}, " +
                $"erreur apres {FormatDuration(analysisResult.TotalDuration)}: {analysisResult.ErrorMessage}");
            AppendTechnicalDetailsBlock(
                $"Machine bridge diagnostics - {analysisResult.Package.PackageId}",
                analysisResult.TechnicalDetails);
        }

        UpdateMachineCallMetrics();

        _pendingVisibleRefreshCount++;
        UpdateProgressStatus();
    }

    private void UpdateMachineCallMetrics()
    {
        DashboardMachineCallStatusMetric =
            $"Appels machines : {_successfulMachineLoadCount} succes, {FailedPackageCount} erreur(s), {_emptySuccessfulMachineLoadCount} succes vide(s)";
        DashboardMachineReturnedRowsMetric = $"Machines retournees brutes : {_totalRawMachineRowsReturned}";
    }

    private void RegisterMachineCacheStatus(string cacheStatus)
    {
        if (string.IsNullOrWhiteSpace(cacheStatus) || string.Equals(cacheStatus, "non disponible", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_machineCacheStatusCounts.TryAdd(cacheStatus, 1))
        {
            _machineCacheStatusCounts[cacheStatus]++;
        }

        DashboardMachineCacheStatusMetric = "Cache machines : " + string.Join(
            ", ",
            _machineCacheStatusCounts
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => $"{FormatCacheStatus(entry.Key)}={entry.Value}"));
    }

    private void FlushVisibleDashboardRefresh()
    {
        if (_pendingVisibleRefreshCount == 0)
        {
            return;
        }

        var refreshStopwatch = Stopwatch.StartNew();
        RefreshGlobalCounters();
        UpdateOuFilterOptions();
        ApplyDashboardFilters();
        refreshStopwatch.Stop();

        AppendTechnicalDetailsLine(
            $"Dashboard UI refresh after {AnalyzedPackageCount}/{TotalPackageCount} paquet(s): {FormatDuration(refreshStopwatch.Elapsed)}.");

        _pendingVisibleRefreshCount = 0;
    }

    private void UpdateProgressStatus()
    {
        StatusMessage = FailedPackageCount > 0
            ? $"{AnalyzedPackageCount}/{TotalPackageCount} paquet(s) programmes traites, {FailedPackageCount} erreur(s)."
            : $"{AnalyzedPackageCount}/{TotalPackageCount} paquet(s) programmes traites.";
    }

    private string BuildDashboardCompletionMessage()
    {
        var scopeMessage = TotalPackageCount == AvailablePackageCount
            ? $"{AnalyzedPackageCount} paquet(s) cd48 analyses pour le tableau de bord."
            : $"{AnalyzedPackageCount} paquet(s) analyses en mode rapide sur {AvailablePackageCount} paquet(s) cd48 disponibles.";

        return FailedPackageCount > 0
            ? $"{scopeMessage} {FailedPackageCount} chargement(s) machines en erreur."
            : scopeMessage;
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

    private static string ResolveMachineStrategy(string? technicalDetails, string? machineLoadMessage)
    {
        var selectedStrategy = ExtractTechnicalValue(technicalDetails, "Selected strategy:");
        if (!string.IsNullOrWhiteSpace(selectedStrategy))
        {
            return selectedStrategy;
        }

        var bridgeStrategy = ExtractTechnicalValue(technicalDetails, "Bridge strategy:");
        var strategyLine = string.IsNullOrWhiteSpace(technicalDetails)
            ? string.Empty
            : technicalDetails
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("Strategy [", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        var derivedStrategy = DeriveMachineStrategyFromMessage(machineLoadMessage);

        return FirstNonEmpty(derivedStrategy, strategyLine, bridgeStrategy);
    }

    private static string DeriveMachineStrategyFromMessage(string? machineLoadMessage)
    {
        if (string.IsNullOrWhiteSpace(machineLoadMessage))
        {
            return string.Empty;
        }

        if (machineLoadMessage.Contains("hosts_for_package", StringComparison.OrdinalIgnoreCase))
        {
            return "WaptServerHostsForPackageFallback";
        }

        if (machineLoadMessage.Contains("depends", StringComparison.OrdinalIgnoreCase))
        {
            return "WaptServerHostsDependsFallback";
        }

        if (machineLoadMessage.Contains("host_data", StringComparison.OrdinalIgnoreCase))
        {
            return "WaptServerHostDataInstalledPackagesFallback";
        }

        return string.Empty;
    }

    private static string NormalizeCacheStatus(string? cacheStatus)
    {
        return string.IsNullOrWhiteSpace(cacheStatus)
            ? "non disponible"
            : cacheStatus.Trim().ToLowerInvariant();
    }

    private static string FormatCacheStatus(string? cacheStatus)
    {
        return NormalizeCacheStatus(cacheStatus) switch
        {
            "memory-hit" => "hit memoire",
            "memory-miss" => "miss memoire",
            "shared-inflight" => "requete partagee",
            "non disponible" => "non disponible",
            var value => value
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return $"{duration.TotalMilliseconds:0} ms";
    }

    private static string FormatValueOrPlaceholder(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
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
            "PackageId;Nom;Version;Machines;Conformes;Inconnues;Non conformes;OU distinctes;Statut machines;Strategie machines;Message machines"
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
                EscapeCsvValue(summary.DistinctOuCount.ToString()),
                EscapeCsvValue(summary.MachineLoadStatus),
                EscapeCsvValue(summary.MachineStrategy),
                EscapeCsvValue(summary.MachineLoadMessage)
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

    private sealed record PackageAnalysis(
        WaptPackage Package,
        IReadOnlyList<WaptMachine> Machines,
        bool MachineLoadSucceeded,
        string MachineLoadStatus,
        string MachineStrategy,
        string MachineLoadMessage);

    private sealed record PackageAnalysisResult(
        WaptPackage Package,
        IReadOnlyList<WaptMachine> Machines,
        DashboardPackageSummary? Summary,
        string TechnicalDetails,
        int RawMachineCount,
        bool MachineLoadSucceeded,
        string MachineStrategy,
        string MachineLoadMessage,
        TimeSpan BridgeDuration,
        TimeSpan ProcessingDuration,
        TimeSpan TotalDuration,
        string? ErrorMessage)
    {
        public bool IsSuccess => ErrorMessage is null;
    }

    private sealed record FilteredPackageAnalysis(PackageAnalysis Analysis, IReadOnlyList<WaptMachine> FilteredMachines);

    private sealed record FilteredMachineEntry(string PackageId, WaptMachine Machine);
}