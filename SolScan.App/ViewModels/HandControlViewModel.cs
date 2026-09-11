using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SolScan.Core.Telescope;

namespace SolScan.App.ViewModels;

/// <summary>
/// Backs the pop-out, modeless Hand Control window (see Views/HandControlWindow.xaml) - manual
/// Up/Down/Left/Right jogging of the connected mount, a 1-8 speed slider, and Stop. Visually modelled
/// on GSServer's HandControlV/HandController (a compass of directional buttons around a central Stop,
/// plus a vertical 1-8 speed slider - see GSServer's Windows/HandControlV.xaml and
/// Controls/HandController.xaml), but GSServer *is* the ASCOM driver talking to a mount's motor
/// controller directly, whereas this drives the mount over ASCOM Alpaca's own standard hand-paddle
/// primitive (ITelescopeMount.MoveAxisAsync/AbortSlewAsync - see that interface's doc comments), not
/// a port of GSServer's internal motor-timing code.
/// </summary>
public partial class HandControlViewModel : ObservableObject
{
    /// <summary>GSServer's own hand-control speed table (SkyServer.SetSlewRates/_slewSpeedOne..
    /// SlewSpeedEight: <c>Math.Round(maxRate * 0.0034, 3)</c> etc.) - each of the 8 levels is this
    /// fraction of the mount's own maximum slew rate, from a gentle nudge (level 1) to full speed
    /// (level 8). GSServer gets "max rate" from a user-configurable setting (default 3.5°/s); this
    /// queries it live from the connected mount instead - see
    /// <see cref="ITelescopeMount.GetMaxSlewRateDegPerSecAsync"/>.</summary>
    private static readonly double[] SpeedLevelFractions = [0.0034, 0.0068, 0.047, 0.068, 0.2, 0.4, 0.8, 1.0];

    private readonly ITelescopeMount _mount;
    private readonly MountState _mountState;

    private double _maxRateDegPerSec = 3.5; // GSServer's own default, until GetMaxSlewRateDegPerSecAsync resolves

    /// <summary>1-8, matching GSServer's own HcSpeed range and default (Seven - 80% of max).</summary>
    [ObservableProperty]
    private int speedLevel = 7;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string statusText = "Ready.";

    /// <summary>The actual rate (deg/sec) <see cref="SpeedLevel"/> currently resolves to - shown in
    /// the UI and what's actually passed to <see cref="ITelescopeMount.MoveAxisAsync"/>.</summary>
    public double CurrentRateDegPerSec => Math.Round(_maxRateDegPerSec * SpeedLevelFractions[SpeedLevel - 1], 4);

    public HandControlViewModel(ITelescopeMount mount, MountState mountState)
    {
        _mount = mount;
        _mountState = mountState;

        IsConnected = _mountState.IsConnected;
        _mountState.PropertyChanged += MountStatePropertyChanged;

        _ = InitializeMaxRateAsync();
    }

    private void MountStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MountState.IsConnected))
        {
            IsConnected = _mountState.IsConnected;
        }
    }

    private async Task InitializeMaxRateAsync()
    {
        try
        {
            _maxRateDegPerSec = await _mount.GetMaxSlewRateDegPerSecAsync();
        }
        catch (Exception ex)
        {
            // GetMaxSlewRateDegPerSecAsync already falls back internally on failure - reaching here
            // would mean something else went wrong entirely; keep the constructor-time default and
            // just surface it rather than leaving the window looking like nothing happened.
            StatusText = $"Could not read the mount's max slew rate - using {_maxRateDegPerSec:0.0}°/s. ({ex.Message})";
        }

        OnPropertyChanged(nameof(CurrentRateDegPerSec));
    }

    partial void OnSpeedLevelChanged(int value) => OnPropertyChanged(nameof(CurrentRateDegPerSec));

    /// <summary>Starts continuous motion on <paramref name="axis"/> - <paramref name="sign"/> is +1
    /// or -1, multiplied by <see cref="CurrentRateDegPerSec"/> to get the signed rate MoveAxis wants.
    /// Called from HandControlWindow's mouse-down handlers - fire-and-forget from there, so failures
    /// are reported via <see cref="StatusText"/> rather than thrown.</summary>
    public async Task StartMoveAsync(TelescopeAxis axis, int sign)
    {
        try
        {
            await _mount.MoveAxisAsync(axis, sign * CurrentRateDegPerSec);
            StatusText = $"Moving {axis} at {CurrentRateDegPerSec:0.0000}°/s.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    /// <summary>Stops motion on just one axis (a directional button was released) - see
    /// <see cref="StopAllAsync"/> for the Stop button/close-safety-net equivalent.</summary>
    public async Task StopAxisAsync(TelescopeAxis axis)
    {
        try
        {
            await _mount.MoveAxisAsync(axis, 0);
            StatusText = "Ready.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error stopping: {ex.Message}";
        }
    }

    /// <summary>The Stop button, and the window's Closing safety net - belt-and-braces: AbortSlew
    /// plus explicitly zeroing both axes, each independently best-effort so one failing doesn't skip
    /// the others (a driver that doesn't implement AbortSlew shouldn't leave a MoveAxis still
    /// running).</summary>
    public async Task StopAllAsync()
    {
        var errors = new List<string>();

        try { await _mount.AbortSlewAsync(); }
        catch (Exception ex) { errors.Add(ex.Message); }

        try { await _mount.MoveAxisAsync(TelescopeAxis.Primary, 0); }
        catch (Exception ex) { errors.Add(ex.Message); }

        try { await _mount.MoveAxisAsync(TelescopeAxis.Secondary, 0); }
        catch (Exception ex) { errors.Add(ex.Message); }

        StatusText = errors.Count == 0 ? "Stopped." : $"Stopped (with errors: {string.Join("; ", errors)}).";
    }
}
