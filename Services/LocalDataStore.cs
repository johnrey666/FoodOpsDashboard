using System.IO;
using System.Text.Json;
using FoodOpsDashboard.Models;

namespace FoodOpsDashboard.Services;

/// <summary>
/// Persists StoreList + DATA as JSON under LocalAppData.
/// Supports migration from the older Month/Store/Account/TY/TGT/LY format
/// (no Year) into Year-scoped rows where LY becomes prior-year TY.
/// </summary>
internal static class LocalDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public const int DefaultSeedYear = 2026;

    public static string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FoodOpsDashboard");

    public static string StoresPath => Path.Combine(FolderPath, "stores.json");
    public static string DataPath => Path.Combine(FolderPath, "data.json");

    public static bool Exists => File.Exists(StoresPath) && File.Exists(DataPath);

    public static void Save(IEnumerable<StoreInfo> stores, IEnumerable<DataRecord> records)
    {
        Directory.CreateDirectory(FolderPath);

        var storeDtos = stores.Select(s => new StoreDto { Name = s.Name, Group = s.Group }).ToList();
        var dataDtos = records.Select(r => new DataDto
        {
            Year = r.Year,
            Month = r.Month,
            Store = r.Store,
            Account = r.Account,
            TY = r.TY,
            TGT = r.TGT
        }).ToList();

        File.WriteAllText(StoresPath, JsonSerializer.Serialize(storeDtos, JsonOptions));
        File.WriteAllText(DataPath, JsonSerializer.Serialize(dataDtos, JsonOptions));
    }

    public static (List<StoreInfo> Stores, List<DataRecord> Records)? TryLoad()
    {
        if (!Exists) return null;

        var storeDtos = JsonSerializer.Deserialize<List<StoreDto>>(File.ReadAllText(StoresPath), JsonOptions);
        var dataDtos = JsonSerializer.Deserialize<List<DataDto>>(File.ReadAllText(DataPath), JsonOptions);
        if (storeDtos is null || dataDtos is null) return null;

        var stores = storeDtos.Select(s => new StoreInfo { Name = s.Name, Group = s.Group }).ToList();
        var records = new List<DataRecord>();
        var index = new Dictionary<string, DataRecord>(StringComparer.Ordinal);

        foreach (var dto in dataDtos)
        {
            int year = dto.Year != 0 ? dto.Year : DefaultSeedYear;
            var key = Key(year, dto.Month, dto.Store, dto.Account);
            if (!index.TryGetValue(key, out var row))
            {
                row = new DataRecord
                {
                    Year = year,
                    Month = dto.Month,
                    Store = dto.Store,
                    Account = dto.Account,
                    TY = dto.TY,
                    TGT = dto.TGT
                };
                index[key] = row;
                records.Add(row);
            }
            else
            {
                // Prefer non-zero values if duplicates appear during migration.
                if (row.TY == 0 && dto.TY != 0) row.TY = dto.TY;
                if (row.TGT == 0 && dto.TGT != 0) row.TGT = dto.TGT;
            }

            // Legacy: stored LY becomes prior-year TY when that cell is empty.
            if (dto.LY != 0)
            {
                int prior = year - 1;
                var priorKey = Key(prior, dto.Month, dto.Store, dto.Account);
                if (!index.TryGetValue(priorKey, out var priorRow))
                {
                    priorRow = new DataRecord
                    {
                        Year = prior,
                        Month = dto.Month,
                        Store = dto.Store,
                        Account = dto.Account,
                        TY = dto.LY,
                        TGT = 0
                    };
                    index[priorKey] = priorRow;
                    records.Add(priorRow);
                }
                else if (priorRow.TY == 0)
                {
                    priorRow.TY = dto.LY;
                }
            }
        }

        return (stores, records);
    }

    public static void Reset()
    {
        if (File.Exists(StoresPath)) File.Delete(StoresPath);
        if (File.Exists(DataPath)) File.Delete(DataPath);
    }

    private static string Key(int year, string month, string store, string account) =>
        $"{year}|{month}|{store}|{account}";

    private sealed class StoreDto
    {
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
    }

    private sealed class DataDto
    {
        public int Year { get; set; }
        public string Month { get; set; } = "";
        public string Store { get; set; } = "";
        public string Account { get; set; } = "";
        public double TY { get; set; }
        public double TGT { get; set; }
        /// <summary>Legacy field — migrated into prior-year TY when present.</summary>
        public double LY { get; set; }
    }
}
