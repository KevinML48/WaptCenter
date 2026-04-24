using System.Windows;
using WaptCenter.Services;
using WaptCenter.ViewModels;
using WaptCenter.Views;

namespace WaptCenter;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		var configService = new ConfigService();
		var waptBridgePackageService = new WaptBridgePackageService();
		var waptBridgeMachineService = new WaptBridgeMachineService();
		var waptConnectionService = new WaptConnectionService(waptBridgePackageService);
		var settingsViewModel = new SettingsViewModel(configService, waptConnectionService);
		var packageDetailsViewModel = new PackageDetailsViewModel(configService, waptBridgeMachineService);
		var packagesViewModel = new PackagesViewModel(configService, waptBridgePackageService, packageDetailsViewModel);
		var mainViewModel = new MainViewModel(settingsViewModel, packagesViewModel);

		var mainWindow = new MainWindow
		{
			DataContext = mainViewModel
		};

		MainWindow = mainWindow;
		mainWindow.Show();
	}
}

