using CommunityToolkit.Mvvm.ComponentModel;

namespace WaptCenter.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private string title = "Preparation locale WAPT";

    [ObservableProperty]
    private string summary = "Le shell desktop est en place. La configuration et les flux WAPT arrivent dans les prochains commits.";
}