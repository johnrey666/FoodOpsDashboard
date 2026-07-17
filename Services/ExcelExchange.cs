using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FoodOpsDashboard.Models;

namespace FoodOpsDashboard.Services;

/// <summary>
/// Import/export year DATA as .xlsx (Office Open XML) with no NuGet dependency.
/// Sheet "DATA": Year, Month, Store, Account, TY, TGT
/// Sheet "Stores": StoreName, Group (for sharing store list between devices)
/// </summary>
public static class ExcelExchange
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static void ExportYear(string path, int year, IEnumerable<DataRecord> records, IEnumerable<StoreInfo> stores)
    {
        var yearRows = records
            .Where(r => r.Year == year)
            .OrderBy(r => Array.IndexOf(DataRepository.Months, r.Month))
            .ThenBy(r => r.Store, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dataSheet = BuildSheet(
            new[] { "Year", "Month", "Store", "Account", "TY", "TGT" },
            yearRows.Select(r => new[]
            {
                r.Year.ToString(CultureInfo.InvariantCulture),
                r.Month,
                r.Store,
                r.Account,
                FormatNum(r.TY),
                FormatNum(r.TGT)
            }));

        var storeList = stores
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var storeSheet = BuildSheet(
            new[] { "StoreName", "Group" },
            storeList.Select(s => new[] { s.Name, s.Group }));

        WriteWorkbook(path, new (string Name, XDocument Sheet)[]
        {
            ("DATA", dataSheet),
            ("Stores", storeSheet)
        });
    }

    public static (int Year, int Updated, int AddedStores) Import(string path, DataRepository repo)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = ReadSharedStrings(zip);
        var sheets = ListSheets(zip);

        if (!sheets.TryGetValue("DATA", out var dataPath) && !sheets.TryGetValue("Data", out dataPath))
            throw new InvalidOperationException("Workbook must contain a sheet named DATA.");

        var dataRows = ReadSheetRows(zip, dataPath, shared);
        if (dataRows.Count == 0)
            throw new InvalidOperationException("DATA sheet is empty.");

        // Header map
        var header = dataRows[0].Select(h => h.Trim()).ToList();
        int Col(params string[] names)
        {
            for (int i = 0; i < header.Count; i++)
            {
                foreach (var n in names)
                    if (string.Equals(header[i], n, StringComparison.OrdinalIgnoreCase))
                        return i;
            }
            return -1;
        }

        int cYear = Col("Year");
        int cMonth = Col("Month");
        int cStore = Col("Store");
        int cAccount = Col("Account");
        int cTy = Col("TY", "This Year");
        int cTgt = Col("TGT", "Target");

        if (cMonth < 0 || cStore < 0 || cAccount < 0 || cTy < 0 || cTgt < 0)
            throw new InvalidOperationException("DATA sheet needs columns: Month, Store, Account, TY, TGT (Year optional).");

        // Optional Stores sheet
        int addedStores = 0;
        if (sheets.TryGetValue("Stores", out var storesPath) || sheets.TryGetValue("StoreList", out storesPath))
        {
            var storeRows = ReadSheetRows(zip, storesPath, shared);
            if (storeRows.Count > 1)
            {
                var sh = storeRows[0];
                int nIdx = IndexOf(sh, "StoreName", "Store", "Name");
                int gIdx = IndexOf(sh, "Group");
                if (nIdx >= 0)
                {
                    for (int i = 1; i < storeRows.Count; i++)
                    {
                        var row = storeRows[i];
                        string name = Get(row, nIdx).Trim();
                        string group = gIdx >= 0 ? Get(row, gIdx).Trim() : "";
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (!repo.Stores.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            repo.AddStore(name, group, repo.GetYearChoices());
                            addedStores++;
                        }
                    }
                }
            }
        }

        int? importYear = null;
        int updated = 0;
        var ensuredYears = new HashSet<int>();
        var index = repo.Records.ToDictionary(
            r => Key(r.Year, r.Month, r.Store, r.Account),
            r => r,
            StringComparer.OrdinalIgnoreCase);

        void RebuildIndex()
        {
            index = repo.Records.ToDictionary(
                r => Key(r.Year, r.Month, r.Store, r.Account),
                r => r,
                StringComparer.OrdinalIgnoreCase);
        }

        for (int i = 1; i < dataRows.Count; i++)
        {
            var row = dataRows[i];
            string month = Get(row, cMonth).Trim();
            string store = Get(row, cStore).Trim();
            string account = Get(row, cAccount).Trim();
            if (string.IsNullOrWhiteSpace(month) || string.IsNullOrWhiteSpace(store) || string.IsNullOrWhiteSpace(account))
                continue;

            int year = cYear >= 0 && int.TryParse(Get(row, cYear), out var y)
                ? y
                : (importYear ?? DataRepository.DefaultYear);

            importYear ??= year;
            if (cYear < 0) year = importYear.Value;

            if (!repo.Stores.Any(s => string.Equals(s.Name, store, StringComparison.OrdinalIgnoreCase)))
            {
                repo.AddStore(store, "", new[] { year - 1, year, year + 1 });
                addedStores++;
                RebuildIndex();
                ensuredYears.UnionWith(new[] { year - 1, year, year + 1 });
            }

            if (ensuredYears.Add(year))
            {
                repo.EnsureCompleteGrid(year);
                RebuildIndex();
            }

            double ty = ParseNum(Get(row, cTy));
            double tgt = ParseNum(Get(row, cTgt));
            var key = Key(year, month, store, account);

            if (!index.TryGetValue(key, out var rec))
            {
                RebuildIndex();
                if (!index.TryGetValue(key, out rec))
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
                    updated++;
                    continue;
                }
            }

            if (rec.TY != ty || rec.TGT != tgt)
            {
                rec.TY = ty;
                rec.TGT = tgt;
                updated++;
            }
        }

        return (importYear ?? DataRepository.DefaultYear, updated, addedStores);
    }

    private static string Key(int year, string month, string store, string account) =>
        $"{year}|{month}|{store}|{account}";

    private static string FormatNum(double v) => v.ToString("G15", CultureInfo.InvariantCulture);

    private static double ParseNum(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim().Replace(",", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string Get(IReadOnlyList<string> row, int i) =>
        i >= 0 && i < row.Count ? row[i] : "";

    private static int IndexOf(IReadOnlyList<string> header, params string[] names)
    {
        for (int i = 0; i < header.Count; i++)
            foreach (var n in names)
                if (string.Equals(header[i].Trim(), n, StringComparison.OrdinalIgnoreCase))
                    return i;
        return -1;
    }

    // ---- minimal XLSX write/read ----

    private static XDocument BuildSheet(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
    {
        var sheetData = new XElement(Ns + "sheetData");
        int r = 1;
        sheetData.Add(MakeRow(r++, headers));
        foreach (var row in rows)
            sheetData.Add(MakeRow(r++, row));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(Ns + "worksheet",
                new XAttribute("xmlns", Ns.NamespaceName),
                sheetData));
    }

    private static XElement MakeRow(int rowIndex, IEnumerable<string> values)
    {
        var row = new XElement(Ns + "row", new XAttribute("r", rowIndex));
        int c = 0;
        foreach (var value in values)
        {
            string cellRef = ColName(c++) + rowIndex;
            // Always write as inline string for simplicity (numbers still parse on import)
            row.Add(new XElement(Ns + "c",
                new XAttribute("r", cellRef),
                new XAttribute("t", "inlineStr"),
                new XElement(Ns + "is", new XElement(Ns + "t", value ?? ""))));
        }
        return row;
    }

    private static string ColName(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return sb.ToString();
    }

    private static void WriteWorkbook(string path, (string Name, XDocument Sheet)[] sheets)
    {
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(zip, "[Content_Types].xml",
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(Ct + "Types",
                    new XAttribute("xmlns", Ct.NamespaceName),
                    new XElement(Ct + "Default", new XAttribute("Extension", "rels"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(Ct + "Default", new XAttribute("Extension", "xml"),
                        new XAttribute("ContentType", "application/xml")),
                    new XElement(Ct + "Override", new XAttribute("PartName", "/xl/workbook.xml"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                    sheets.Select((s, i) =>
                        new XElement(Ct + "Override",
                            new XAttribute("PartName", $"/xl/worksheets/sheet{i + 1}.xml"),
                            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))))));

        WriteEntry(zip, "_rels/.rels",
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(P + "Relationships",
                    new XAttribute("xmlns", P.NamespaceName),
                    new XElement(P + "Relationship",
                        new XAttribute("Id", "rId1"),
                        new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                        new XAttribute("Target", "xl/workbook.xml")))));

        var wbRels = new XElement(P + "Relationships", new XAttribute("xmlns", P.NamespaceName));
        var wbSheets = new XElement(Ns + "sheets");
        for (int i = 0; i < sheets.Length; i++)
        {
            string rid = $"rId{i + 1}";
            wbRels.Add(new XElement(P + "Relationship",
                new XAttribute("Id", rid),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", $"worksheets/sheet{i + 1}.xml")));
            wbSheets.Add(new XElement(Ns + "sheet",
                new XAttribute("name", sheets[i].Name),
                new XAttribute("sheetId", i + 1),
                new XAttribute(R + "id", rid)));
        }

        WriteEntry(zip, "xl/_rels/workbook.xml.rels",
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), wbRels));

        WriteEntry(zip, "xl/workbook.xml",
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(Ns + "workbook",
                    new XAttribute("xmlns", Ns.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                    wbSheets)));

        for (int i = 0; i < sheets.Length; i++)
            WriteEntry(zip, $"xl/worksheets/sheet{i + 1}.xml", sheets[i].Sheet);
    }

    private static void WriteEntry(ZipArchive zip, string entryName, XDocument doc)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        doc.Save(writer);
    }

    private static Dictionary<string, string> ListSheets(ZipArchive zip)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wb = LoadXml(zip, "xl/workbook.xml");
        var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");
        var relMap = rels.Root!.Elements(P + "Relationship")
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);

        foreach (var sheet in wb.Root!.Element(Ns + "sheets")!.Elements(Ns + "sheet"))
        {
            string name = (string)sheet.Attribute("name")!;
            string rid = (string)sheet.Attribute(R + "id")!;
            string target = relMap[rid].Replace('\\', '/');
            if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) && !target.StartsWith("/"))
                target = "xl/" + target.TrimStart('/');
            else if (target.StartsWith("/"))
                target = target.TrimStart('/');
            result[name] = target;
        }
        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return list;
        var doc = LoadXml(zip, "xl/sharedStrings.xml");
        foreach (var si in doc.Root!.Elements(Ns + "si"))
        {
            var texts = si.Descendants(Ns + "t").Select(t => t.Value);
            list.Add(string.Concat(texts));
        }
        return list;
    }

    private static List<List<string>> ReadSheetRows(ZipArchive zip, string sheetPath, List<string> shared)
    {
        var doc = LoadXml(zip, sheetPath);
        var rows = new List<List<string>>();
        foreach (var row in doc.Root!.Element(Ns + "sheetData")!.Elements(Ns + "row"))
        {
            var cells = row.Elements(Ns + "c").ToList();
            if (cells.Count == 0) continue;

            int maxCol = cells.Max(c => ColIndex((string)c.Attribute("r")!));
            var values = Enumerable.Repeat("", maxCol + 1).ToList();
            foreach (var c in cells)
            {
                int idx = ColIndex((string)c.Attribute("r")!);
                values[idx] = ReadCell(c, shared);
            }
            // Skip fully empty rows
            if (values.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(values);
        }
        return rows;
    }

    private static string ReadCell(XElement c, List<string> shared)
    {
        string? t = (string?)c.Attribute("t");
        if (t == "s")
        {
            var v = c.Element(Ns + "v")?.Value;
            if (int.TryParse(v, out var i) && i >= 0 && i < shared.Count) return shared[i];
            return v ?? "";
        }
        if (t == "inlineStr")
            return string.Concat(c.Element(Ns + "is")?.Descendants(Ns + "t").Select(x => x.Value) ?? Array.Empty<string>());
        return c.Element(Ns + "v")?.Value ?? "";
    }

    private static int ColIndex(string cellRef)
    {
        int i = 0, col = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
            i++;
        }
        return col - 1;
    }

    private static XDocument LoadXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? zip.GetEntry(path.Replace('\\', '/'))
            ?? throw new InvalidOperationException($"Missing part: {path}");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }
}
