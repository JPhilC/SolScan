using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Backs the Options view's "Cameras" tab. Most entries here come from CaptureViewModel's camera
/// auto-add rather than being typed in by hand (see CameraProfile's doc comment), but this tab still
/// allows adding/editing/removing one by hand too, same as SHGs/telescopes.
/// </summary>
public partial class CameraLibraryViewModel : ObservableObject
{
    private readonly IEquipmentLibrary _library;

    public ObservableCollection<CameraProfileEditor> Items { get; } = [];

    [ObservableProperty]
    private CameraProfileEditor? selectedItem;

    public CameraLibraryViewModel(IEquipmentLibrary library)
    {
        _library = library;
        foreach (var profile in _library.LoadCameras())
        {
            Items.Add(new CameraProfileEditor(profile));
        }

        SelectedItem = Items.FirstOrDefault();
    }

    private bool HasSelection => SelectedItem is not null;

    [RelayCommand]
    private void Add()
    {
        var reference = SelectedItem?.ToModel();
        var copy = reference is null
            ? CameraProfile.CreateDefault($"My camera {Items.Count + 1}")
            : reference with { Id = Guid.NewGuid(), Label = reference.Label + " (Copy)" };
        var item = new CameraProfileEditor(copy);
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

    partial void OnSelectedItemChanged(CameraProfileEditor? value) =>
        RemoveCommand.NotifyCanExecuteChanged();

    public void Save() => _library.SaveCameras(Items.Select(i => i.ToModel()).ToList());
}
