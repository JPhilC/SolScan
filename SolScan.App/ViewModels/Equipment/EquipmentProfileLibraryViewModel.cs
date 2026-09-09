using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Backs the Options view's "Telescopes &amp; Cameras" tab - SolScan's equivalent of JSolex's
/// SetupEditor.java, embedded directly in the Options view rather than a separate modal dialog.
/// </summary>
public partial class EquipmentProfileLibraryViewModel : ObservableObject
{
    private readonly IEquipmentLibrary _library;

    public ObservableCollection<EquipmentProfileEditor> Items { get; } = [];

    [ObservableProperty]
    private EquipmentProfileEditor? selectedItem;

    public EquipmentProfileLibraryViewModel(IEquipmentLibrary library)
    {
        _library = library;
        foreach (var profile in _library.LoadEquipmentProfiles())
        {
            Items.Add(new EquipmentProfileEditor(profile));
        }

        SelectedItem = Items.FirstOrDefault();
    }

    private bool HasSelection => SelectedItem is not null;

    [RelayCommand]
    private void Add()
    {
        var reference = SelectedItem?.ToModel();
        var copy = reference is null
            ? EquipmentProfile.CreateDefault($"My setup {Items.Count + 1}")
            : reference with { Id = Guid.NewGuid(), Label = reference.Label + " (Copy)" };
        var item = new EquipmentProfileEditor(copy);
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

    partial void OnSelectedItemChanged(EquipmentProfileEditor? value) =>
        RemoveCommand.NotifyCanExecuteChanged();

    public void Save() => _library.SaveEquipmentProfiles(Items.Select(i => i.ToModel()).ToList());
}
