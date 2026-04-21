using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WaptCenter.Models;
using WaptCenter.Services;

namespace WaptCenter.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly WaptConnectionService _waptConnectionService;

    [ObservableProperty]
    private string serverUrl = string.Empty;

    [ObservableProperty]
    private string clientCertPath = string.Empty;

    [ObservableProperty]
    private string clientKeyPath = string.Empty;

    [ObservableProperty]
    private string pemPath = string.Empty;

    [ObservableProperty]
    private string pkcs12Path = string.Empty;

    [ObservableProperty]
    private string certPassword = string.Empty;

    [ObservableProperty]
    private string caCertPath = string.Empty;

    [ObservableProperty]
    private string pythonExecutablePath = string.Empty;

    [ObservableProperty]
    private string bridgeScriptPath = string.Empty;

    [ObservableProperty]
    private bool verifySsl = true;

    [ObservableProperty]
    private int timeoutSeconds = 30;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private string connectionTestMessage = string.Empty;

    [ObservableProperty]
    private string connectionTechnicalDetails = string.Empty;

    [ObservableProperty]
    private bool? isConnectionSuccessful;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool isTestingConnection;

    public SettingsViewModel(ConfigService configService, WaptConnectionService waptConnectionService)
    {
        _configService = configService;
        _waptConnectionService = waptConnectionService;
        LoadConfig();
    }

    [RelayCommand]
    private void Save()
    {
        var config = new WaptConfig
        {
            ServerUrl = ServerUrl,
            ClientCertPath = ClientCertPath,
            ClientKeyPath = ClientKeyPath,
            PemPath = PemPath,
            Pkcs12Path = Pkcs12Path,
            CertPassword = CertPassword,
            CaCertPath = CaCertPath,
            PythonExecutablePath = PythonExecutablePath,
            BridgeScriptPath = BridgeScriptPath,
            VerifySsl = VerifySsl,
            TimeoutSeconds = TimeoutSeconds <= 0 ? 30 : TimeoutSeconds
        };

        _configService.Save(config);
        StatusMessage = "Configuration enregistree";
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        ConnectionTestMessage = "Test .NET en cours...";
        ConnectionTechnicalDetails = string.Empty;
        IsConnectionSuccessful = null;

        try
        {
            var result = await _waptConnectionService.TestConnectionAsync(new WaptConfig
            {
                ServerUrl = ServerUrl,
            ClientCertPath = ClientCertPath,
            ClientKeyPath = ClientKeyPath,
            PemPath = PemPath,
                Pkcs12Path = Pkcs12Path,
                CertPassword = CertPassword,
                CaCertPath = CaCertPath,
                VerifySsl = VerifySsl,
                TimeoutSeconds = TimeoutSeconds <= 0 ? 30 : TimeoutSeconds
            });

            ConnectionTestMessage = result.Message;
            ConnectionTechnicalDetails = result.TechnicalDetails ?? string.Empty;
            IsConnectionSuccessful = result.Success;
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private void LoadConfig()
    {
        var config = _configService.Load();

        ServerUrl = config.ServerUrl;
        ClientCertPath = config.ClientCertPath;
        ClientKeyPath = config.ClientKeyPath;
        PemPath = config.PemPath;
        Pkcs12Path = config.Pkcs12Path;
        CertPassword = config.CertPassword;
        CaCertPath = config.CaCertPath;
        PythonExecutablePath = config.PythonExecutablePath;
        BridgeScriptPath = config.BridgeScriptPath;
        VerifySsl = config.VerifySsl;
        TimeoutSeconds = config.TimeoutSeconds <= 0 ? 30 : config.TimeoutSeconds;
        StatusMessage = string.Empty;
        ConnectionTestMessage = string.Empty;
        ConnectionTechnicalDetails = string.Empty;
        IsConnectionSuccessful = null;
    }

    private bool CanTestConnection()
    {
        return !IsTestingConnection;
    }
}