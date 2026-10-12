using System.Linq;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FortnitePorting.Framework;
using FortnitePorting.ViewModels;

namespace FortnitePorting.Views;

public partial class CheckView : ViewBase<CheckViewModel>
{
    public CheckView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.Data.GetFiles();
        if (items is null) return;

        ViewModel.AddPaths(items.Select(item => item.TryGetLocalPath()).OfType<string>());
    }
}
