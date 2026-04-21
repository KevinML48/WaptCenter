using System.Windows;
using System.Windows.Controls;
using WaptCenter.ViewModels;

namespace WaptCenter.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += SettingsView_OnDataContextChanged;
    }

    private void SettingsView_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SettingsViewModel viewModel)
        {
            CertPasswordBox.Password = viewModel.CertPassword;
        }
    }

    private void CertPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && viewModel.CertPassword != CertPasswordBox.Password)
        {
            viewModel.CertPassword = CertPasswordBox.Password;
        }
    }
}