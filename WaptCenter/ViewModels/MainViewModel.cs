using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WaptCenter.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsViewModel _settingsViewModel;
    private readonly PackagesViewModel _packagesViewModel;

    [ObservableProperty]
    private ObservableObject? currentViewModel;

    [ObservableProperty]
    private string currentSectionTitle = string.Empty;

    [ObservableProperty]
    private string currentSectionDescription = string.Empty;

    public MainViewModel(SettingsViewModel settingsViewModel, PackagesViewModel packagesViewModel)
    {
        _settingsViewModel = settingsViewModel;
        _packagesViewModel = packagesViewModel;
        ShowSettings();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        CurrentViewModel = _settingsViewModel;
        CurrentSectionTitle = "Configuration locale";
        CurrentSectionDescription = "Renseignez les chemins et parametres utilises par le bridge Python WAPT pour valider le flux reel de chargement.";
    }

    [RelayCommand]
    private void ShowPackages()
    {
        CurrentViewModel = _packagesViewModel;
        CurrentSectionTitle = "Paquets cd48";
        CurrentSectionDescription = "Chargez les paquets via le bridge WAPT puis conservez uniquement ceux dont le package_id contient cd48.";
    }
}