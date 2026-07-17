using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FoodOpsDashboard.Models;

/// <summary>
/// One DATA row for a Year / Month / Store / Account.
/// Only TY and TGT are entered; LY is derived from the prior year's TY.
/// </summary>
public sealed class DataRecord : INotifyPropertyChanged
{
    private double _ty;
    private double _tgt;

    public required int Year { get; init; }
    public required string Month { get; init; }
    public required string Store { get; init; }
    public required string Account { get; init; }

    public double TY
    {
        get => _ty;
        set { if (Set(ref _ty, value)) OnPropertyChanged(); }
    }

    public double TGT
    {
        get => _tgt;
        set { if (Set(ref _tgt, value)) OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool Set(ref double field, double value)
    {
        if (field.Equals(value)) return false;
        field = value;
        return true;
    }
}

/// <summary>
/// Data-entry grid row: editable TY/TGT with read-only LY from prior year.
/// </summary>
public sealed class DataEntryRow : INotifyPropertyChanged
{
    private readonly Func<double> _lyLookup;

    public DataEntryRow(DataRecord record, Func<double> lyLookup)
    {
        Record = record;
        _lyLookup = lyLookup;
        Record.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DataRecord.TY) or nameof(DataRecord.TGT))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
        };
    }

    public DataRecord Record { get; }
    public int Year => Record.Year;
    public string Month => Record.Month;
    public string Store => Record.Store;
    public string Account => Record.Account;

    public double TY
    {
        get => Record.TY;
        set => Record.TY = value;
    }

    public double TGT
    {
        get => Record.TGT;
        set => Record.TGT = value;
    }

    /// <summary>Prior year's TY for the same Month / Store / Account.</summary>
    public double LY => _lyLookup();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyLyChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LY)));
}
