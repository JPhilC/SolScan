using System.Windows;
using SolScan.App.ViewModels;

namespace SolScan.App.Views;

/// <summary>
/// Interaction logic for SerCropWindow.xaml.
/// </summary>
public partial class SerCropWindow : Window
{
    private readonly SerCropViewModel _viewModel;

    public SerCropWindow(SerCropViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Safety net: closing the window (even via Alt+F4/the system close button) while a crop is
        // running must not leave it writing to a file this window no longer offers any way to check
        // on - same "always cancel, never abandon" reasoning as HandControlWindow's own Closing
        // handler, just for a background file operation instead of a live mount jog.
        Closing += (_, _) => _viewModel.CancelCropCommand.Execute(null);
    }
}
