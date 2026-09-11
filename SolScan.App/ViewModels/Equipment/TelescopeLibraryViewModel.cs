using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolScan.Core.Equipment;

namespace SolScan.App.ViewModels.Equipment;

/// <summary>
/// Backs the Options view's "Telescopes" tab - SolScan's equivalent of JSolex's SetupEditor.java,
/// scoped down to just the telescope side (see TelescopeProfile's doc comment for why camera/mount/
/// site fields aren't here).
/// </summary>
public partial class TelescopeLibraryViewModel : ObservableObject
{
    private readonly IEquipmentLibrary _library;

    public ObservableCollection<TelescopeProfileEditor> Items { get; } = [];

    [ObservableProperty]
    private TelescopeProfileEditor? selectedItem;

    public TelescopeLibraryViewModel(IEquipmentLibrary library)
    {
        _library = library;
        foreach (var profile in _library.LoadTelescopes())
        {
            Items.Add(new TelescopeProfileEditor(profile));
        }

        SelectedItem = Items.FirstOrDefault();
    }

    private bool HasSelection => SelectedItem is not null;

    [RelayCommand]
    private void Add()
    {
        var reference = SelectedItem?.ToModel();
        var copy = reference is null
            ? TelescopeProfile.CreateDefault($"My telescope {Items.Count + 1}")
            : reference with { Id = Guid.NewGuid(), Label = reference.Label + " (Copy)" };
        var item = new TelescopeProfileEditor(copy);
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

    partial void OnSelectedItemChanged(TelescopeProfileEditor? value) =>
        RemoveCommand.NotifyCanExecuteChanged();

    public void Save() => _library.SaveTelescopes(Items.Select(i => i.ToModel()).ToList());
}
