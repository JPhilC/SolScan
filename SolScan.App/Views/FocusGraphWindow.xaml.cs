using System.Windows;

namespace SolScan.App.Views;

/// <summary>
/// Interaction logic for FocusGraphWindow.xaml - a pure view over <see cref="ViewModels.CaptureViewModel"/>
/// (its DataContext, set by <c>CaptureViewModel.OpenFocusGraph</c>); no logic of its own.
/// </summary>
public partial class FocusGraphWindow : Window
{
    public FocusGraphWindow()
    {
        InitializeComponent();
    }
}
