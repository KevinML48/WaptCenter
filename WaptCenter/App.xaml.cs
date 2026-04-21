using System.Windows;
using WaptCenter.ViewModels;
using WaptCenter.Views;

namespace WaptCenter;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		var settingsViewModel = new SettingsViewModel();
		var mainViewModel = new MainViewModel(settingsViewModel);

		var mainWindow = new MainWindow
		{
			DataContext = mainViewModel
		};

		MainWindow = mainWindow;
		mainWindow.Show();
	}
}

