using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Backs the Options view's "Spectrographs" tab - SolScan's equivalent of JSolex's
/// SpectroHeliographEditor.java, embedded directly in the Options view rather than a separate
/// modal dialog launched from an Equipment menu.
/// </summary>
public partial class SpectrographLibraryViewModel : ObservableObject
{
    private readonly IEquipmentLibrary _library;

    public ObservableCollection<SpectrographProfileEditor> Items { get; } = [];

    [ObservableProperty]
    private SpectrographProfileEditor? selectedItem;

    public SpectrographLibraryViewModel(IEquipmentLibrary library)
    {
        _library = library;
        foreach (var profile in _library.LoadSpectrographs())
        {
            Items.Add(new SpectrographProfileEditor(profile));
        }

        SelectedItem = Items.FirstOrDefault();
    }

    private bool HasSelection => SelectedItem is not null;

    [RelayCommand]
    private void Add()
    {
        var reference = SelectedItem?.ToModel() ?? SpectrographProfile.CreateSolEx();
        var copy = reference with { Id = Guid.NewGuid(), Label = reference.Label + " (Copy)" };
        var item = new SpectrographProfileEditor(copy);
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

    partial void OnSelectedItemChanged(SpectrographProfileEditor? value) =>
        RemoveCommand.NotifyCanExecuteChanged();

    public void Save() => _library.SaveSpectrographs(Items.Select(i => i.ToModel()).ToList());
}
