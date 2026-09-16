using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SolScan.App.ViewModels;

namespace SolScan.App.Views;

/// <summary>
/// Code-behind for the VS-tool-window-style pin/dock feature on the right-hand options panel (see
/// <see cref="UpdatePanelDockState"/>) - mirrors CaptureView.xaml.cs's own identical mechanism for
/// its own panel exactly.
/// </summary>
public partial class ProcessView : UserControl
{
    /// <summary>Min/max pixel width for the docked options panel column - see
    /// <see cref="UpdatePanelDockState"/>. Same 340px-ballpark default the floating drawer already
    /// uses sits comfortably inside this range.</summary>
    private const double MinDockedPanelWidth = 260;
    private const double MaxDockedPanelWidth = 700;

    private ProcessViewModel? _viewModel;

    /// <summary>Guards <see cref="UpdatePanelDockState"/>'s own writes to <c>OptionsPanelColumn.Width</c>
    /// from being misreported by <c>OptionsPanelHost.SizeChanged</c> as a user's GridSplitter drag.</summary>
    private bool _updatingPanelColumnFromViewModel;

    public ProcessView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        OptionsPanelHost.SizeChanged += OptionsPanelHost_SizeChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as ProcessViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdatePanelDockState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProcessViewModel.IsProcessOptionsPanelExpanded) or nameof(ProcessViewModel.IsProcessOptionsPanelPinned))
        {
            UpdatePanelDockState();
        }
    }

    /// <summary>Reports a user's GridSplitter drag (which resizes <c>OptionsPanelColumn</c>, and so
    /// this Border filling it) back into <see cref="ProcessViewModel.ProcessOptionsPanelWidth"/> for
    /// persistence - guarded so <see cref="UpdatePanelDockState"/>'s own programmatic width changes
    /// (docking/undocking) don't get misreported as a drag.</summary>
    private void OptionsPanelHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_updatingPanelColumnFromViewModel && _viewModel is not null && OptionsPanelHost.ActualWidth > 0)
        {
            _viewModel.ProcessOptionsPanelWidth = OptionsPanelHost.ActualWidth;
        }
    }

    /// <summary>Drives <c>OptionsPanelColumn</c> (the docked panel's own Grid column - see
    /// ProcessView.xaml) directly, since a GridSplitter-resizable column needs a real pixel
    /// <see cref="GridLength"/>, not something WPF makes bindable from a view model. Docked
    /// (<see cref="ProcessViewModel.IsProcessOptionsPanelDocked"/>): restores the last-known/saved
    /// width, clamped to <see cref="MinDockedPanelWidth"/>-<see cref="MaxDockedPanelWidth"/>. Not
    /// docked: collapses the column to 0 so the main content reclaims the space. Guarded
    /// (<see cref="_updatingPanelColumnFromViewModel"/>) so <see cref="OptionsPanelHost_SizeChanged"/>
    /// doesn't mistake this for a user drag. Mirrors CaptureView.xaml.cs's identical method exactly.</summary>
    private void UpdatePanelDockState()
    {
        if (_viewModel is null)
        {
            return;
        }

        _updatingPanelColumnFromViewModel = true;
        if (_viewModel.IsProcessOptionsPanelDocked)
        {
            OptionsPanelColumn.MinWidth = MinDockedPanelWidth;
            OptionsPanelColumn.MaxWidth = MaxDockedPanelWidth;
            OptionsPanelColumn.Width = new GridLength(Math.Clamp(_viewModel.ProcessOptionsPanelWidth, MinDockedPanelWidth, MaxDockedPanelWidth));
        }
        else
        {
            OptionsPanelColumn.MinWidth = 0;
            OptionsPanelColumn.MaxWidth = 0;
            OptionsPanelColumn.Width = new GridLength(0);
        }
        _updatingPanelColumnFromViewModel = false;
    }
}
