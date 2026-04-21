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
    private readonly WaptPackageService _waptPackageService;

    public ObservableCollection<WaptPackage> Packages { get; } = [];

    [ObservableProperty]
    private string statusMessage = "Chargez les paquets exposes par l'API WAPT.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadPackagesCommand))]
    private bool isLoadingPackages;

    public PackagesViewModel(ConfigService configService, WaptPackageService waptPackageService)
    {
        _configService = configService;
        _waptPackageService = waptPackageService;
    }

    [RelayCommand(CanExecute = nameof(CanLoadPackages))]
    private async Task LoadPackagesAsync()
    {
        IsLoadingPackages = true;
        StatusMessage = "Chargement des paquets cd48 en cours...";

        try
        {
            var packages = await _waptPackageService.GetCd48PackagesAsync(_configService.Load());
            Packages.Clear();

            foreach (var package in packages)
            {
                Packages.Add(package);
            }

            StatusMessage = packages.Count == 0
                ? "Aucun paquet cd48 trouve."
                : $"{packages.Count} paquet(s) cd48 charge(s).";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Chargement impossible: {exception.Message}";
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
