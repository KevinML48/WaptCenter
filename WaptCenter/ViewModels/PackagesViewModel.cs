using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaptCenter.Models;
using WaptCenter.Services;

namespace WaptCenter.ViewModels;

public partial class PackagesViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly WaptBridgePackageService _waptBridgePackageService;

    public ObservableCollection<WaptPackage> Packages { get; } = [];

    [ObservableProperty]
    private string statusMessage = "Chargez les paquets exposes par le bridge Python WAPT.";

    [ObservableProperty]
    private string technicalDetails = string.Empty;

    [ObservableProperty]
    private bool isStatusError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPackagesCommand))]
    private bool isLoadingPackages;

    public PackagesViewModel(ConfigService configService, WaptBridgePackageService waptBridgePackageService)
    {
        _configService = configService;
        _waptBridgePackageService = waptBridgePackageService;
    }

    [RelayCommand(CanExecute = nameof(CanLoadPackages))]
    private async Task LoadPackagesAsync()
    {
        IsLoadingPackages = true;
        IsStatusError = false;
        TechnicalDetails = string.Empty;
        StatusMessage = "Chargement des paquets cd48 via le bridge Python en cours...";

        try
        {
            var packages = await _waptBridgePackageService.GetCd48PackagesAsync(_configService.Load());
            Packages.Clear();

            foreach (var package in packages)
            {
                Packages.Add(package);
            }

            TechnicalDetails = _waptBridgePackageService.LastTechnicalDetails;

            StatusMessage = packages.Count == 0
                ? "Aucun paquet cd48 trouve via le bridge Python."
                : $"{packages.Count} paquet(s) cd48 charge(s) via le bridge Python.";
        }
        catch (Exception exception)
        {
            IsStatusError = true;
            TechnicalDetails = _waptBridgePackageService.LastTechnicalDetails;
            StatusMessage = $"Chargement bridge impossible: {exception.Message}";
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
}
