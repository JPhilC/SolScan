using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SolScan.App.ViewModels;
using SolScan.Core.Telescope;

namespace SolScan.App.Views;

/// <summary>
/// Interaction logic for HandControlWindow.xaml - directional buttons jog the mount only while
/// held (mouse down starts continuous motion, mouse up stops it), matching a hand paddle's actual
/// behaviour. Mouse.Capture on press/release (rather than relying on the button's own Preview
/// events alone) so dragging off the button before releasing still delivers the Up event - without
/// it, a drag-off would leave that axis slewing with no way to stop it short of the Stop button.
/// </summary>
public partial class HandControlWindow : Window
{
    private readonly HandControlViewModel _viewModel;
    private TelescopeAxis? _heldAxis;

    public HandControlWindow(HandControlViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Safety net: closing the window (even via Alt+F4/the system close button mid-press) must
        // never leave a MoveAxis command still running.
        Closing += (_, _) => _ = _viewModel.StopAllAsync();
    }

    private void OnUpDown(object sender, MouseButtonEventArgs e) => BeginMove((Button)sender, TelescopeAxis.Secondary, +1);
    private void OnDownDown(object sender, MouseButtonEventArgs e) => BeginMove((Button)sender, TelescopeAxis.Secondary, -1);
    private void OnRightDown(object sender, MouseButtonEventArgs e) => BeginMove((Button)sender, TelescopeAxis.Primary, +1);
    private void OnLeftDown(object sender, MouseButtonEventArgs e) => BeginMove((Button)sender, TelescopeAxis.Primary, -1);

    private void BeginMove(Button button, TelescopeAxis axis, int sign)
    {
        _heldAxis = axis;
        Mouse.Capture(button);
        _ = _viewModel.StartMoveAsync(axis, sign);
    }

    private void OnDirectionUp(object sender, MouseButtonEventArgs e)
    {
        Mouse.Capture(null);
        if (_heldAxis is { } axis)
        {
            _heldAxis = null;
            _ = _viewModel.StopAxisAsync(axis);
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _heldAxis = null;
        Mouse.Capture(null);
        _ = _viewModel.StopAllAsync();
    }
}
