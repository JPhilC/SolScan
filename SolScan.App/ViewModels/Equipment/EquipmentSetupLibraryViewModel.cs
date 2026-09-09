using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Backs the Options view's "Setups" tab - a library of saved SHG+telescope+camera combinations,
/// each referencing one entry from the Spectrographs tab and one from the Telescopes &amp;
/// Cameras tab. New to SolScan: astro4j/JSolex has no equivalent, since SpectroHeliograph and
/// Setup are picked independently there (see EquipmentSetup's doc comment).
/// </summary>
public partial class EquipmentSetupLibraryViewModel : ObservableObject
{
    private readonly IEquipmentLibrary _library;

    public ObservableCollection<EquipmentSetupEditor> Items { get; } = [];

    /// <summary>The live Spectrographs-tab collection, shared so this tab's ComboBox always
    /// offers whatever spectrographs currently exist (including ones added but not yet saved).</summary>
    public ObservableCollection<SpectrographProfileEditor> AvailableSpectrographs { get; }

    /// <summary>The live Telescopes &amp; Cameras-tab collection - same sharing rationale as
    /// <see cref="AvailableSpectrographs"/>.</summary>
    public ObservableCollection<EquipmentProfileEditor> AvailableEquipmentProfiles { get; }

    [ObservableProperty]
    private EquipmentSetupEditor? selectedItem;

    public EquipmentSetupLibraryViewModel(
        IEquipmentLibrary library,
        ObservableCollection<SpectrographProfileEditor> availableSpectrographs,
        ObservableCollection<EquipmentProfileEditor> availableEquipmentProfiles)
    {
        _library = library;
        AvailableSpectrographs = availableSpectrographs;
        AvailableEquipmentProfiles = availableEquipmentProfiles;

        foreach (var setup in _library.LoadSetups())
        {
            Items.Add(new EquipmentSetupEditor(setup, AvailableSpectrographs, AvailableEquipmentProfiles));
        }

        SelectedItem = Items.FirstOrDefault();
    }

    private bool HasSelection => SelectedItem is not null;

    private bool CanAdd => AvailableSpectrographs.Count > 0 && AvailableEquipmentProfiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var item = new EquipmentSetupEditor(
            $"Setup {Items.Count + 1}",
            AvailableSpectrographs.FirstOrDefault(),
            AvailableEquipmentProfiles.FirstOrDefault());
        Items.Add(item);
        SelectedItem = item;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var index = Items.IndexOf(SelectedItem);
        Items.Remove(SelectedItem);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    partial void OnSelectedItemChanged(EquipmentSetupEditor? value) =>
        RemoveCommand.NotifyCanExecuteChanged();

    public void Save()
    {
        var models = Items
            .Select(i => i.ToModel())
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();
        _library.SaveSetups(models);
    }
}
