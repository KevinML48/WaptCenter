using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaptCenter.Models;
using WaptCenter.Services;

namespace WaptCenter.ViewModels;

public partial class PackagesViewModel : ObservableObject
{
    private const string FixedPackageFilterValue = "cd48";

    private readonly ConfigService _configService;
    private readonly WaptBridgePackageService _waptBridgePackageService;
    public PackageDetailsViewModel PackageDetails { get; }

    public PackagesViewModel(
        ConfigService configService,
        WaptBridgePackageService waptBridgePackageService,
        PackageDetailsViewModel packageDetails)
    {
        _configService = configService;
        _waptBridgePackageService = waptBridgePackageService;
        PackageDetails = packageDetails;
    }

    public ObservableCollection<WaptPackage> Packages { get; } = [];

    [ObservableProperty]
    private WaptPackage? selectedPackage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPackagesCommand))]
    private bool isLoadingPackages;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string technicalDetails = string.Empty;

    [ObservableProperty]
    private bool isStatusError;

    [ObservableProperty]
    private bool hasPackages;

    [RelayCommand(CanExecute = nameof(CanLoadPackages))]
    private async Task LoadPackagesAsync()
    {
        IsLoadingPackages = true;
        IsStatusError = false;
        StatusMessage = string.Empty;
        TechnicalDetails = string.Empty;
        HasPackages = false;
        SelectedPackage = null;
        PackageDetails.ClearSelection();
        Packages.Clear();

        try
        {
            var config = _configService.Load();
            var packages = await _waptBridgePackageService.GetCd48PackagesAsync(config);
            TechnicalDetails = _waptBridgePackageService.LastTechnicalDetails;

            foreach (var package in packages)
            {
                Packages.Add(package);
            }

            HasPackages = Packages.Count > 0;

            if (!HasPackages)
            {
                StatusMessage = $"Aucun paquet dont package_id contient '{FixedPackageFilterValue}' n'a ete trouve.";
                return;
            }

            StatusMessage = $"{Packages.Count} paquet(s) dont package_id contient '{FixedPackageFilterValue}' charge(s).";
        }
        catch (InvalidOperationException exception)
        {
            IsStatusError = true;
            StatusMessage = exception.Message;
            TechnicalDetails = string.IsNullOrWhiteSpace(_waptBridgePackageService.LastTechnicalDetails)
                ? exception.ToString()
                : _waptBridgePackageService.LastTechnicalDetails;
        }
        catch (Exception exception)
        {
            IsStatusError = true;
            StatusMessage = "Une erreur inattendue est survenue lors du chargement des paquets.";
            TechnicalDetails = string.IsNullOrWhiteSpace(_waptBridgePackageService.LastTechnicalDetails)
                ? exception.ToString()
                : _waptBridgePackageService.LastTechnicalDetails;
        }
        finally
        {
            IsLoadingPackages = false;
        }
    }

    private bool CanLoadPackages()
    {
        return !IsLoadingPackages;
    }

    partial void OnSelectedPackageChanged(WaptPackage? value)
    {
        _ = PackageDetails.LoadForPackageAsync(value);
    }
}