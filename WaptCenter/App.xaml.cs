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
		var settingsViewModel = new SettingsViewModel(configService);
		var mainViewModel = new MainViewModel(settingsViewModel);

		var mainWindow = new MainWindow
		{
			DataContext = mainViewModel
		};

		MainWindow = mainWindow;
		mainWindow.Show();
	}
}

