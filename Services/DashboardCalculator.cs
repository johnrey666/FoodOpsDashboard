using FoodOpsDashboard.Models;

namespace FoodOpsDashboard.Services;

/// <summary>
/// Dashboard formulas. LY = prior year's TY. Month filter accepts one or many months
/// (sums / consolidates when multiple are selected).
/// </summary>
public sealed class DashboardCalculator
{
    private readonly DataRepository _repo;

    /// <summary>Expense accounts eligible for the Top 5 ranking (excludes Sales / COGS / HO).</summary>
    private static readonly HashSet<string> ExpenseAccountSet =
        DataRepository.Accounts
            .Where(a => a is not ("Sales" or "Cost of Sales" or "HO"))
            .ToHashSet(StringComparer.Ordinal);

    public DashboardCalculator(DataRepository repo) => _repo = repo;

    public HashSet<string> GetSelectedStores(string storeChoice)
    {
        if (storeChoice == "Conso")
            return _repo.Stores.Select(s => s.Name).ToHashSet();

        foreach (var group in _repo.Groups)
        {
            if (storeChoice == $"{group} Conso")
                return _repo.Stores.Where(s => s.Group == group).Select(s => s.Name).ToHashSet();
        }

        return _repo.Stores.Where(s => s.Name == storeChoice).Select(s => s.Name).ToHashSet();
    }

    private MetricSet ComputeMetrics(int year, HashSet<string>? months, HashSet<string> stores, Func<DataRecord, double> value)
    {
        bool InScope(DataRecord r) =>
            r.Year == year
            && stores.Contains(r.Store)
            && (months == null || months.Count == 0 || months.Contains(r.Month));

        double Sum(Func<DataRecord, bool> filter) =>
            _repo.Records.Where(r => InScope(r) && filter(r)).Sum(value);

        double sales = Sum(r => r.Account == "Sales");
        double costOfSales = Sum(r => r.Account == "Cost of Sales");
        double grossProfit = sales - costOfSales;

        double opexExSalesCogsHo = Sum(r => r.Account != "Sales" && r.Account != "Cost of Sales" && r.Account != "HO");
        double sbuCm = grossProfit - opexExSalesCogsHo;

        double depreciation = Sum(r => r.Account == "Depreciation");
        double amortization = Sum(r => r.Account == "Amortization");
        double sbuEbitda = sbuCm + depreciation + amortization;

        double totalOpex = Sum(r => r.Account != "Sales" && r.Account != "Cost of Sales");
        double netIncome = grossProfit - totalOpex;

        return new MetricSet
        {
            Sales = sales,
            CostOfSales = costOfSales,
            GrossProfit = grossProfit,
            SbuCm = sbuCm,
            SbuEbitda = sbuEbitda,
            TotalOpex = totalOpex,
            NetIncome = netIncome
        };
    }

    public KpiSet ComputeKpis(int year, IReadOnlyCollection<string> months, string storeChoice)
    {
        var stores = GetSelectedStores(storeChoice);
        var monthSet = months.Count == 0 ? null : months.ToHashSet(StringComparer.Ordinal);
        return new KpiSet
        {
            TY = ComputeMetrics(year, monthSet, stores, r => r.TY),
            TGT = ComputeMetrics(year, monthSet, stores, r => r.TGT),
            LY = ComputeMetrics(year - 1, monthSet, stores, r => r.TY)
        };
    }

    /// <summary>Top 5 operating expenses by TY amount for the selected year / months / store.</summary>
    public IReadOnlyList<TopExpenseRow> ComputeTopExpenses(int year, IReadOnlyCollection<string> months, string storeChoice, int topN = 5)
    {
        var stores = GetSelectedStores(storeChoice);
        var monthSet = months.Count == 0 ? null : months.ToHashSet(StringComparer.Ordinal);
        var kpis = ComputeKpis(year, months, storeChoice);
        double totalOpexTy = kpis.TY.TotalOpex;
        double salesTy = kpis.TY.Sales;

        bool InScope(DataRecord r, int y) =>
            r.Year == y
            && stores.Contains(r.Store)
            && (monthSet == null || monthSet.Contains(r.Month))
            && ExpenseAccountSet.Contains(r.Account);

        var byAccount = ExpenseAccountSet.Select(account =>
        {
            double ty = _repo.Records.Where(r => InScope(r, year) && r.Account == account).Sum(r => r.TY);
            double tgt = _repo.Records.Where(r => InScope(r, year) && r.Account == account).Sum(r => r.TGT);
            double ly = _repo.Records.Where(r => InScope(r, year - 1) && r.Account == account).Sum(r => r.TY);
            return new TopExpenseRow
            {
                Expense = account,
                TY = ty,
                TGT = tgt,
                LY = ly,
                PctOfTotalOpex = totalOpexTy != 0 ? ty / totalOpexTy : 0,
                PctOfTotalSales = salesTy != 0 ? ty / salesTy : 0
            };
        });

        return byAccount
            .OrderByDescending(r => Math.Abs(r.TY))
            .Take(topN)
            .ToList();
    }

    public IReadOnlyList<MonthlyTrendPoint> ComputeMonthlyTrend(int year, string storeChoice)
    {
        var points = new List<MonthlyTrendPoint>();
        foreach (var month in DataRepository.Months)
        {
            var k = ComputeKpis(year, new[] { month }, storeChoice);
            double gr = k.LY.Sales != 0 ? (k.TY.Sales - k.LY.Sales) / Math.Abs(k.LY.Sales) : 0;
            points.Add(new MonthlyTrendPoint
            {
                Month = month,
                TySales = k.TY.Sales,
                TgtSales = k.TGT.Sales,
                LySales = k.LY.Sales,
                NetIncome = k.TY.NetIncome,
                GrowthPct = gr
            });
        }
        return points;
    }
}
