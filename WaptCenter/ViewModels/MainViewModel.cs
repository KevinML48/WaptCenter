using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WaptCenter.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DashboardViewModel _dashboardViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly PackagesViewModel _packagesViewModel;

    [ObservableProperty]
    private ObservableObject? currentViewModel;

    [ObservableProperty]
    private string currentSectionTitle = string.Empty;

    [ObservableProperty]
    private string currentSectionDescription = string.Empty;

    public MainViewModel(
        DashboardViewModel dashboardViewModel,
        SettingsViewModel settingsViewModel,
        PackagesViewModel packagesViewModel)
    {
        _dashboardViewModel = dashboardViewModel;
        _settingsViewModel = settingsViewModel;
        _packagesViewModel = packagesViewModel;
        ShowSettings();
    }

    [RelayCommand]
    private void ShowDashboard()
    {
        CurrentViewModel = _dashboardViewModel;
        CurrentSectionTitle = "Tableau de bord cd48";
        CurrentSectionDescription = "Chargez une synthese progressive des paquets cd48, des machines associees et des regroupements par OU sans quitter l'architecture bridge.";
    }

    [RelayCommand]
    private void ShowSettings()
    {
        _dashboardViewModel.CancelLoading();
        CurrentViewModel = _settingsViewModel;
        CurrentSectionTitle = "Configuration locale";
        CurrentSectionDescription = "Renseignez les chemins et parametres utilises par le bridge Python WAPT pour valider le flux reel de chargement.";
    }

    [RelayCommand]
    private void ShowPackages()
    {
        _dashboardViewModel.CancelLoading();
        CurrentViewModel = _packagesViewModel;
        CurrentSectionTitle = "Paquets cd48";
        CurrentSectionDescription = "Chargez les paquets via le bridge WAPT puis selectionnez un package_id cd48 pour voir les machines associees.";
    }
}