using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WaptCenter.Models;
using WaptCenter.Services;

namespace WaptCenter.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _configService;

    [ObservableProperty]
    private string serverUrl = string.Empty;

    [ObservableProperty]
    private string pkcs12Path = string.Empty;

    [ObservableProperty]
    private string certPassword = string.Empty;

    [ObservableProperty]
    private string caCertPath = string.Empty;

    [ObservableProperty]
    private bool verifySsl = true;

    [ObservableProperty]
    private int timeoutSeconds = 30;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    public SettingsViewModel(ConfigService configService)
    {
        _configService = configService;
        LoadConfig();
    }

    [RelayCommand]
    private void Save()
    {
        var config = new WaptConfig
        {
            ServerUrl = ServerUrl,
            Pkcs12Path = Pkcs12Path,
            CertPassword = CertPassword,
            CaCertPath = CaCertPath,
            VerifySsl = VerifySsl,
            TimeoutSeconds = TimeoutSeconds <= 0 ? 30 : TimeoutSeconds
        };

        _configService.Save(config);
        StatusMessage = "Configuration enregistree";
    }

    private void LoadConfig()
    {
        var config = _configService.Load();

        ServerUrl = config.ServerUrl;
        Pkcs12Path = config.Pkcs12Path;
        CertPassword = config.CertPassword;
        CaCertPath = config.CaCertPath;
        VerifySsl = config.VerifySsl;
        TimeoutSeconds = config.TimeoutSeconds <= 0 ? 30 : config.TimeoutSeconds;
        StatusMessage = string.Empty;
    }
}