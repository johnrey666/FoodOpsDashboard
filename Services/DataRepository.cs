using System.Collections.ObjectModel;
using System.Globalization;
using FoodOpsDashboard.Data;
using FoodOpsDashboard.Models;

namespace FoodOpsDashboard.Services;

/// <summary>
/// Holds StoreList + multi-year DATA. Seeds from EmbeddedData as year 2026
/// on first run, then reads/writes LocalDataStore.
/// </summary>
public sealed class DataRepository
{
    public const int DefaultYear = 2026;

    public ObservableCollection<StoreInfo> Stores { get; } = new();
    public ObservableCollection<DataRecord> Records { get; } = new();

    public IReadOnlyList<string> Groups =>
        Stores.Select(s => s.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList();

    public static readonly string[] Months =
    {
        "Jan", "Feb", "Mar", "Apr", "May", "Jun",
        "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
    };

    public static readonly string[] Accounts =
    {
        "Sales",
        "Cost of Sales",
        "Salaries & Wages",
        "Officers Salaries",
        "Agency Services",
        "Security Services",
        "Directors Fees",
        "Delivery/Packaging/Handling",
        "Other Employee Fringe Benefits",
        "Breakage Allowance",
        "Transportation & Meals",
        "Customer Incentives",
        "Supplies & Materials Used",
        "Communications",
        "Electricity & Water Bills",
        "Rentals",
        "Gasoline/Fuel & Oil Used",
        "Leasing Expenses",
        "Marketing & Advertising",
        "Corporate Special Programs",
        "Insurance Expenses",
        "Taxes & Licenses",
        "Chattel Mortgage Fee",
        "Repairs & Maintenance",
        "Representation",
        "Donations & Contributions",
        "Bad Debts Expense",
        "Legal & Other Fees",
        "Professional Fees",
        "Commission Expenses",
        "Business Research & Dev",
        "Dues & Subscription",
        "Depreciation",
        "Amortization",
        "Royalties",
        "Other Expenses",
        "Miscellaneous",
        "HO"
    };

    public static readonly string[] TopExpenseAccounts =
    {
        "Salaries & Wages",
        "Agency Services",
        "Rentals",
        "Depreciation",
        "Other Expenses",
        "Electricity & Water Bills",
        "Taxes & Licenses",
        "Supplies & Materials Used",
        "Repairs & Maintenance",
        "Other Employee Fringe Benefits"
    };

    public DataRepository()
    {
        var loaded = LocalDataStore.TryLoad();
        if (loaded is { } data)
        {
            foreach (var s in data.Stores) Stores.Add(s);
            foreach (var r in data.Records) Records.Add(r);
        }
        else
        {
            foreach (var s in ParseStores(EmbeddedData.StoreListCsv)) Stores.Add(s);
            foreach (var r in ParseRecords(EmbeddedData.DataCsv, DataRepository.DefaultYear))
                Records.Add(r);
            // Seed prior year from legacy LY columns in the CSV.
            SeedPriorYearFromLegacyLy(EmbeddedData.DataCsv, DataRepository.DefaultYear);
            EnsureCompleteGrid(DataRepository.DefaultYear);
            EnsureCompleteGrid(DataRepository.DefaultYear - 1);
            Save();
        }

        foreach (var year in GetAvailableYears())
            EnsureCompleteGrid(year);
    }

    public IReadOnlyList<int> GetAvailableYears()
    {
        var years = Records.Select(r => r.Year).Distinct().OrderByDescending(y => y).ToList();
        if (years.Count == 0)
            years.Add(DataRepository.DefaultYear);
        return years;
    }

    /// <summary>Years shown in dropdowns, including room to pick an adjacent blank year.</summary>
    public IReadOnlyList<int> GetYearChoices()
    {
        var years = GetAvailableYears().ToList();
        int min = years.Min();
        int max = years.Max();
        if (!years.Contains(max + 1)) years.Insert(0, max + 1);
        if (!years.Contains(min - 1)) years.Add(min - 1);
        return years.OrderByDescending(y => y).ToList();
    }

    public IReadOnlyList<string> BuildStoreDropdown()
    {
        var list = new List<string> { "Conso" };
        list.AddRange(Groups.Select(g => $"{g} Conso"));
        list.AddRange(Stores.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name));
        return list;
    }

    public void Save() => LocalDataStore.Save(Stores, Records);

    public double GetPriorYearTy(int year, string month, string store, string account) =>
        Records.Where(r => r.Year == year - 1 && r.Month == month && r.Store == store && r.Account == account)
               .Select(r => r.TY)
               .DefaultIfEmpty(0)
               .Sum();

    public StoreInfo AddStore(string name, string group, IEnumerable<int> years)
    {
        name = name.Trim();
        group = group.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Store name is required.");
        if (Stores.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Store '{name}' already exists.");

        var store = new StoreInfo { Name = name, Group = group };
        Stores.Add(store);
        foreach (var year in years.Distinct())
            EnsureRowsForStore(year, name);
        return store;
    }

    public void RemoveStore(StoreInfo store)
    {
        Stores.Remove(store);
        for (int i = Records.Count - 1; i >= 0; i--)
        {
            if (Records[i].Store == store.Name)
                Records.RemoveAt(i);
        }
    }

    public void RenameStoreKey(string oldName, string newName)
    {
        if (oldName == newName) return;
        foreach (var r in Records.Where(r => r.Store == oldName).ToList())
        {
            Records.Remove(r);
            Records.Add(new DataRecord
            {
                Year = r.Year,
                Month = r.Month,
                Store = newName,
                Account = r.Account,
                TY = r.TY,
                TGT = r.TGT
            });
        }
    }

    public void EnsureCompleteGrid(int year)
    {
        var existing = new HashSet<string>(
            Records.Where(r => r.Year == year).Select(r => Key(r.Year, r.Month, r.Store, r.Account)),
            StringComparer.Ordinal);

        foreach (var store in Stores)
        {
            if (string.IsNullOrWhiteSpace(store.Name)) continue;
            foreach (var month in Months)
            {
                foreach (var account in Accounts)
                {
                    var key = Key(year, month, store.Name, account);
                    if (existing.Contains(key)) continue;
                    Records.Add(new DataRecord
                    {
                        Year = year,
                        Month = month,
                        Store = store.Name,
                        Account = account,
                        TY = 0,
                        TGT = 0
                    });
                    existing.Add(key);
                }
            }
        }
    }

    private void EnsureRowsForStore(int year, string storeName)
    {
        var existing = new HashSet<string>(
            Records.Where(r => r.Year == year && r.Store == storeName)
                   .Select(r => Key(r.Year, r.Month, r.Store, r.Account)),
            StringComparer.Ordinal);

        foreach (var month in Months)
        {
            foreach (var account in Accounts)
            {
                var key = Key(year, month, storeName, account);
                if (existing.Contains(key)) continue;
                Records.Add(new DataRecord
                {
                    Year = year,
                    Month = month,
                    Store = storeName,
                    Account = account,
                    TY = 0,
                    TGT = 0
                });
            }
        }
    }

    public IEnumerable<DataEntryRow> FilterEntryRows(int year, string? month, string? store)
    {
        EnsureCompleteGrid(year);

        IEnumerable<DataRecord> q = Records.Where(r => r.Year == year);
        if (!string.IsNullOrWhiteSpace(month) && month != "(All)")
            q = q.Where(r => r.Month == month);
        if (!string.IsNullOrWhiteSpace(store) && store != "(All)")
            q = q.Where(r => r.Store == store);

        var order = Accounts.Select((a, i) => (a, i)).ToDictionary(x => x.a, x => x.i);
        return q
            .OrderBy(r => Array.IndexOf(Months, r.Month))
            .ThenBy(r => r.Store)
            .ThenBy(r => order.TryGetValue(r.Account, out var i) ? i : 999)
            .ThenBy(r => r.Account)
            .Select(r => new DataEntryRow(r, () => GetPriorYearTy(r.Year, r.Month, r.Store, r.Account)));
    }

    private void SeedPriorYearFromLegacyLy(string csv, int year)
    {
        foreach (var line in SplitLines(csv))
        {
            var parts = line.Split(',');
            if (parts.Length < 6) continue;
            double ly = ParseDouble(parts[5]);
            if (ly == 0) continue;

            string month = parts[0].Trim();
            string store = parts[1].Trim();
            string account = parts[2].Trim();
            int prior = year - 1;

            var existing = Records.FirstOrDefault(r =>
                r.Year == prior && r.Month == month && r.Store == store && r.Account == account);
            if (existing is null)
            {
                Records.Add(new DataRecord
                {
                    Year = prior,
                    Month = month,
                    Store = store,
                    Account = account,
                    TY = ly,
                    TGT = 0
                });
            }
            else if (existing.TY == 0)
            {
                existing.TY = ly;
            }
        }
    }

    private static string Key(int year, string month, string store, string account) =>
        $"{year}|{month}|{store}|{account}";

    private static List<StoreInfo> ParseStores(string csv)
    {
        var result = new List<StoreInfo>();
        foreach (var line in SplitLines(csv))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            result.Add(new StoreInfo { Name = parts[0].Trim(), Group = parts[1].Trim() });
        }
        return result;
    }

    private static List<DataRecord> ParseRecords(string csv, int year)
    {
        var result = new List<DataRecord>();
        foreach (var line in SplitLines(csv))
        {
            var parts = line.Split(',');
            if (parts.Length < 6) continue;
            result.Add(new DataRecord
            {
                Year = year,
                Month = parts[0].Trim(),
                Store = parts[1].Trim(),
                Account = parts[2].Trim(),
                TY = ParseDouble(parts[3]),
                TGT = ParseDouble(parts[4])
            });
        }
        return result;
    }

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static IEnumerable<string> SplitLines(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
