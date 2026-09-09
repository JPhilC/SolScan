using System.Windows;
using SolScan.App.ViewModels;

namespace SolScan.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(NavigationViewModel navigationViewModel)
    {
        InitializeComponent();
        DataContext = navigationViewModel;
        navigationViewModel.NavigateTo<PrepareViewModel>();
    }
}
