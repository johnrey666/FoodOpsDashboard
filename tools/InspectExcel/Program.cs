using System.IO.Compression;
using System.Xml.Linq;

var path = args.Length > 0 ? args[0] : @"c:\Users\fo.technicalsupport\Downloads\JUNE 2026_FO1 MPT per Format.xlsx";
var sheetName = args.Length > 1 ? args[1] : "conso";
var maxRows = args.Length > 2 && int.TryParse(args[2], out var n) ? n : 120;

XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
XNamespace p = "http://schemas.openxmlformats.org/package/2006/relationships";

using var zip = ZipFile.OpenRead(path);
var shared = ReadShared(zip, ns);
var sheets = ListSheets(zip, ns, r, p);
Console.WriteLine("Sheets: " + string.Join(", ", sheets.Keys));

if (!sheets.TryGetValue(sheetName, out var sheetPath))
{
    Console.WriteLine($"Sheet '{sheetName}' not found.");
    return;
}

var rows = ReadSheetRows(zip, sheetPath, ns, shared);
Console.WriteLine($"\n=== {sheetName} ({rows.Count} rows) ===\n");
var mode = args.Length > 3 ? args[3] : "preview";
var maxCols = args.Length > 4 && int.TryParse(args[4], out var mc) ? mc : 12;

if (mode == "accounts")
{
    DumpAccounts(rows);
    return;
}

if (mode == "headers")
{
    for (int i = 0; i < Math.Min(6, rows.Count); i++)
        Console.WriteLine($"{i}: {string.Join(" | ", rows[i].Select((c, ci) => $"[{ci}]{Trunc(c, 30)}"))}");
    return;
}

if (mode == "june")
{
    int tyCol = FindMonthTyColumn(rows, args.Length > 5 ? args[5] : "JUNE");
    Console.WriteLine($"Month TY column index: {tyCol} (TGT={tyCol + 6}, LY={tyCol + 2})");
    string? currentStore = null;
    foreach (var row in rows)
    {
        string c0 = Get(row, 0);
        string c1 = Get(row, 1);
        string c2 = Get(row, 2).Trim();
        if (!string.IsNullOrWhiteSpace(c0) && c1 == "REVENUES")
            currentStore = c0.Trim();
        if (string.IsNullOrWhiteSpace(c2)) continue;
        if (c2 is "Sales" or "Cost of Sales" or "Gross Profit" or "Gross Profit " or "SBU CM" or "SBU EBITDA" or "HO" or "HO DA" or "Net Income" or "Miscellaneous Income" or "Add: Miscellaneous Income")
        {
            Console.WriteLine($"{currentStore,-20} {c2,-28} TY={Parse(Get(row, tyCol)),14:N2} LY={Parse(Get(row, tyCol + 2)),14:N2} TGT={Parse(Get(row, tyCol + 6)),14:N2}");
        }
    }
    return;
}

for (int i = 0; i < Math.Min(maxRows, rows.Count); i++)
{
    var cols = rows[i].Take(maxCols).Select(c => Trunc(c, 40));
    Console.WriteLine($"{i,3}: {string.Join(" | ", cols)}");
}

static void DumpAccounts(List<List<string>> rows)
{
    foreach (var row in rows)
    {
        string label = row.Count > 2 ? row[2].Trim() : "";
        if (string.IsNullOrWhiteSpace(label)) continue;
        if (label is "Sales" or "Cost of Sales" or "Gross Profit" or "Gross Profit " ||
            label.Contains("CM", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("EBITDA", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("TOTAL", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Net Income", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith("Add:", StringComparison.OrdinalIgnoreCase) ||
            label is "DA" or "HO" or "HO DA" or "Selling Profit" ||
            label.Contains("IFO", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Interest", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("GOC", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Corp", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("EO Share", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine(label);
    }
}

static int FindMonthTyColumn(List<List<string>> rows, string month)
{
    var header = rows[2];
    for (int i = 0; i < header.Count; i++)
    {
        if (string.Equals(header[i].Trim(), month, StringComparison.OrdinalIgnoreCase))
            return i;
    }
    throw new InvalidOperationException($"Month {month} not found");
}

static string Get(List<string> row, int i) => i >= 0 && i < row.Count ? row[i] : "";
static double Parse(string s) => double.TryParse(s?.Replace(",", ""), out var v) ? v : 0;

static string Trunc(string? s, int max) =>
    string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

static List<string> ReadShared(ZipArchive zip, XNamespace ns)
{
    var list = new List<string>();
    var entry = zip.GetEntry("xl/sharedStrings.xml");
    if (entry is null) return list;
    using var stream = entry.Open();
    var doc = XDocument.Load(stream);
    foreach (var si in doc.Root!.Elements(ns + "si"))
        list.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
    return list;
}

static Dictionary<string, string> ListSheets(ZipArchive zip, XNamespace ns, XNamespace r, XNamespace p)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var wb = LoadXml(zip, "xl/workbook.xml");
    var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");
    var relMap = rels.Root!.Elements(p + "Relationship")
        .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
    foreach (var sheet in wb.Root!.Element(ns + "sheets")!.Elements(ns + "sheet"))
    {
        string name = (string)sheet.Attribute("name")!;
        string rid = (string)sheet.Attribute(r + "id")!;
        string target = relMap[rid].Replace('\\', '/');
        if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            target = "xl/" + target.TrimStart('/');
        result[name] = target;
    }
    return result;
}

static List<List<string>> ReadSheetRows(ZipArchive zip, string sheetPath, XNamespace ns, List<string> shared)
{
    var doc = LoadXml(zip, sheetPath);
    var rows = new List<List<string>>();
    foreach (var row in doc.Root!.Element(ns + "sheetData")!.Elements(ns + "row"))
    {
        var cells = row.Elements(ns + "c").ToList();
        if (cells.Count == 0) continue;
        int maxCol = cells.Max(c => ColIndex((string)c.Attribute("r")!));
        var values = Enumerable.Repeat("", maxCol + 1).ToList();
        foreach (var c in cells)
            values[ColIndex((string)c.Attribute("r")!)] = ReadCell(c, ns, shared);
        if (values.All(string.IsNullOrWhiteSpace)) continue;
        rows.Add(values);
    }
    return rows;
}

static string ReadCell(XElement c, XNamespace ns, List<string> shared)
{
    string? t = (string?)c.Attribute("t");
    if (t == "s")
    {
        var v = c.Element(ns + "v")?.Value;
        if (int.TryParse(v, out var i) && i >= 0 && i < shared.Count) return shared[i];
        return v ?? "";
    }
    if (t == "inlineStr")
        return string.Concat(c.Element(ns + "is")?.Descendants(ns + "t").Select(x => x.Value) ?? Array.Empty<string>());
    return c.Element(ns + "v")?.Value ?? "";
}

static int ColIndex(string cellRef)
{
    int i = 0, col = 0;
    while (i < cellRef.Length && char.IsLetter(cellRef[i]))
    {
        col = col * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
        i++;
    }
    return col - 1;
}

static XDocument LoadXml(ZipArchive zip, string path)
{
    var entry = zip.GetEntry(path) ?? throw new InvalidOperationException($"Missing: {path}");
    using var stream = entry.Open();
    return XDocument.Load(stream);
}
