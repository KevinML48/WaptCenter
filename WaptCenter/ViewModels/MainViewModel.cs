using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WaptCenter.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty]
    private ObservableObject? currentViewModel;

    [ObservableProperty]
    private string currentSectionTitle = string.Empty;

    [ObservableProperty]
    private string currentSectionDescription = string.Empty;

    public MainViewModel(SettingsViewModel settingsViewModel)
    {
        _settingsViewModel = settingsViewModel;
        ShowSettings();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        CurrentViewModel = _settingsViewModel;
        CurrentSectionTitle = "Configuration locale";
        CurrentSectionDescription = "Renseignez et sauvegardez les parametres de connexion WAPT sur ce poste.";
    }
}