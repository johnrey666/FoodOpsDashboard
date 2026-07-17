using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FoodOpsDashboard.Models;

/// <summary>
/// One row of the original "StoreList" sheet.
/// </summary>
public sealed class StoreInfo : INotifyPropertyChanged
{
    private string _name = "";
    private string _group = "";

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value ?? "")) OnPropertyChanged(); }
    }

    public string Group
    {
        get => _group;
        set { if (Set(ref _group, value ?? "")) OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool Set(ref string field, string value)
    {
        if (field == value) return false;
        field = value;
        return true;
    }
}
