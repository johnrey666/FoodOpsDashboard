using System.Globalization;
using System.IO.Compression;
using FoodOpsDashboard.Models;

namespace FoodOpsDashboard.Services;

/// <summary>
/// Imports FO MPT workbooks (e.g. "JUNE 2026_FO1 MPT per Format.xlsx"):
/// group sheets with store blocks, month columns (TY / LY / TGT), and P&amp;L account rows.
/// </summary>
public static class MptExcelImporter
{
    private static readonly (string Sheet, string Group)[] GroupSheets =
    {
        ("Food Garden", "FG"),
        ("FG Express", "FG Express"),
        ("Food to Go", "FTG"),
        ("bpu", "BPU"),
        ("CTK", "CTK"),
        ("Concourse", "Concourse"),
        ("Central Kitchen", "Central Kitchen"),
        ("Central Kitchen - SODA", "Central Kitchen - SODA")
    };

    private static readonly Dictionary<string, string> MonthFromHeader = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JANUARY"] = "Jan",
        ["FEBRUARY"] = "Feb",
        ["March"] = "Mar",
        ["MARCH"] = "Mar",
        ["April"] = "Apr",
        ["APRIL"] = "Apr",
        ["MAY"] = "May",
        ["JUNE"] = "Jun",
        ["JULY"] = "Jul",
        ["AUGUST"] = "Aug",
        ["September"] = "Sep",
        ["SEPTEMBER"] = "Sep",
        ["October"] = "Oct",
        ["OCTOBER"] = "Oct",
        ["November"] = "Nov",
        ["NOVEMBER"] = "Nov",
        ["December"] = "Dec",
        ["DECEMBER"] = "Dec"
    };

    public static (int Year, int Updated, int AddedStores) Import(string path, DataRepository repo)
    {
        using var zip = ZipFile.OpenRead(path);
        var sheets = ExcelExchange.ListSheetPaths(zip);
        int year = DataRepository.DefaultYear;
        int updated = 0;
        int addedStores = 0;

        var index = BuildIndex(repo);
        var touchedYears = new HashSet<int>();

        foreach (var (sheetName, group) in GroupSheets)
        {
            if (!sheets.TryGetValue(sheetName, out var sheetPath)) continue;
            var rows = ExcelExchange.ReadSheetRows(zip, sheetPath);
            if (rows.Count < 4) continue;

            year = ParseYear(rows) ?? year;
            var months = ParseMonthColumns(rows);
            if (months.Count == 0) continue;

            string? sectionStore = null;
            foreach (var row in rows)
            {
                string c0 = Get(row, 0).Trim();
                string c1 = Get(row, 1).Trim();
                string c2 = Get(row, 2).Trim();

                if (!string.IsNullOrWhiteSpace(c0) && c1 == "REVENUES")
                    sectionStore = c0;

                string? account = AccountCatalog.Normalize(c2);
                if (account is null) continue;

                string store = !string.IsNullOrWhiteSpace(c0) && c1 != "REVENUES" && AccountCatalog.IsDetailAccount(c2)
                    ? c0
                    : sectionStore ?? "";

                if (string.IsNullOrWhiteSpace(store)) continue;
                if (ShouldSkipStore(store, sheetName)) continue;

                addedStores += EnsureStore(repo, ref index, store, group, year);
                touchedYears.Add(year);
                touchedYears.Add(year - 1);

                foreach (var month in months)
                {
                    double ty = ParseNum(Get(row, month.TyCol));
                    double ly = ParseNum(Get(row, month.TyCol + 2));
                    double tgt = ParseNum(Get(row, month.TyCol + 6));

                    updated += Upsert(repo, ref index, year, month.Label, store, account, ty, tgt);
                    if (ly != 0)
                        updated += Upsert(repo, ref index, year - 1, month.Label, store, account, ly, 0);
                }
            }
        }

        foreach (var y in touchedYears)
            repo.EnsureCompleteGrid(y);

        return (year, updated, addedStores);
    }

    private static bool ShouldSkipStore(string store, string sheetName) =>
        store.Equals(sheetName, StringComparison.OrdinalIgnoreCase)
        || store.Equals("Total QSR", StringComparison.OrdinalIgnoreCase)
        || store.StartsWith("Total ", StringComparison.OrdinalIgnoreCase);

    private static int? ParseYear(List<List<string>> rows)
    {
        foreach (var row in rows.Take(6))
        {
            if (int.TryParse(Get(row, 0).Trim(), out var y) && y is >= 2000 and <= 2100)
                return y;
        }
        return null;
    }

    private sealed record MonthColumn(string Label, int TyCol);

    private static List<MonthColumn> ParseMonthColumns(List<List<string>> rows)
    {
        var result = new List<MonthColumn>();
        if (rows.Count <= 2) return result;
        var header = rows[2];
        for (int i = 0; i < header.Count; i++)
        {
            string raw = header[i].Trim();
            if (!MonthFromHeader.TryGetValue(raw, out var label)) continue;
            result.Add(new MonthColumn(label, i));
        }
        return result;
    }

    private static int EnsureStore(
        DataRepository repo,
        ref Dictionary<string, DataRecord> index,
        string store,
        string group,
        int year)
    {
        if (repo.Stores.Any(s => string.Equals(s.Name, store, StringComparison.OrdinalIgnoreCase)))
            return 0;

        repo.AddStore(store, group, new[] { year - 1, year, year + 1 });
        index = BuildIndex(repo);
        return 1;
    }

    private static int Upsert(
        DataRepository repo,
        ref Dictionary<string, DataRecord> index,
        int year,
        string month,
        string store,
        string account,
        double ty,
        double tgt)
    {
        var key = Key(year, month, store, account);
        if (!index.TryGetValue(key, out var rec))
        {
            rec = new DataRecord
            {
                Year = year,
                Month = month,
                Store = store,
                Account = account,
                TY = ty,
                TGT = tgt
            };
            repo.Records.Add(rec);
            index[key] = rec;
            return 1;
        }

        if (rec.TY == ty && rec.TGT == tgt) return 0;
        rec.TY = ty;
        rec.TGT = tgt;
        return 1;
    }

    private static Dictionary<string, DataRecord> BuildIndex(DataRepository repo) =>
        repo.Records.ToDictionary(
            r => Key(r.Year, r.Month, r.Store, r.Account),
            r => r,
            StringComparer.OrdinalIgnoreCase);

    private static string Key(int year, string month, string store, string account) =>
        $"{year}|{month}|{store}|{account}";

    private static string Get(IReadOnlyList<string> row, int i) =>
        i >= 0 && i < row.Count ? row[i] : "";

    private static double ParseNum(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim().Replace(",", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
