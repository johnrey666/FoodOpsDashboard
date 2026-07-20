using FoodOpsDashboard.Models;

namespace FoodOpsDashboard.Services;

/// <summary>
/// Dashboard formulas. LY = prior year's TY. Month filter accepts one or many months
/// (sums / consolidates when multiple are selected).
/// </summary>
public sealed class DashboardCalculator
{
    private readonly DataRepository _repo;

    /// <summary>Expense accounts eligible for the Top 5 ranking.</summary>
    private static readonly HashSet<string> ExpenseAccountSet =
        DataRepository.Accounts
            .Where(a => !AccountCatalog.TopExpenseExclusions.Contains(a))
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
        double miscIncome = Sum(r => r.Account == "Miscellaneous Income");
        double grossProfit = sales - costOfSales + miscIncome;

        double storeOpex = Sum(r => AccountCatalog.IsStoreOperatingExpense(r.Account));
        double ho = Sum(r => r.Account == "HO");
        double corpEo = Sum(r => r.Account == "Corp & EO Share");
        double interest = Sum(r => r.Account == "Interest Expense");
        double goc = Sum(r => r.Account == "GOC Expenses");
        double sbuCm = grossProfit - storeOpex - ho;

        double depreciation = Sum(r => r.Account == "Depreciation");
        double amortization = Sum(r => r.Account == "Amortization");
        double hoDa = Sum(r => r.Account == "HO DA");
        double sbuEbitda = sbuCm + depreciation + amortization + hoDa;

        double totalOpex = storeOpex;
        double netIncome = sbuCm - corpEo - interest - goc;

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

    /// <summary>
    /// Total OPEX = store-level operating expenses (TOTAL STORE OPERATING EXPENSES in MPT).
    /// </summary>
    public MetricBreakdown ComputeTotalOpexBreakdown(int year, IReadOnlyCollection<string> months, string storeChoice)
    {
        var kpis = ComputeKpis(year, months, storeChoice);
        var accounts = DataRepository.Accounts
            .Where(AccountCatalog.IsStoreOperatingExpense)
            .ToArray();

        var rows = SumAccounts(year, months, storeChoice, accounts, kpis.TY.Sales)
            .OrderByDescending(r => Math.Abs(r.TY))
            .ToList();

        return new MetricBreakdown
        {
            Title = "Total OPEX",
            Formula = "Sum of store operating expense accounts (MPT: TOTAL STORE OPERATING EXPENSES). Excludes HO and below-the-line items.",
            TY = kpis.TY.TotalOpex,
            TGT = kpis.TGT.TotalOpex,
            LY = kpis.LY.TotalOpex,
            PctOfSales = kpis.TY.Sales != 0 ? kpis.TY.TotalOpex / kpis.TY.Sales : 0,
            Rows = rows
        };
    }

    /// <summary>
    /// SBU EBITDA = SBU CM + Depreciation + Amortization + HO DA.
    /// </summary>
    public MetricBreakdown ComputeSbuEbitdaBreakdown(int year, IReadOnlyCollection<string> months, string storeChoice)
    {
        var stores = GetSelectedStores(storeChoice);
        var monthSet = months.Count == 0 ? null : months.ToHashSet(StringComparer.Ordinal);
        var kpis = ComputeKpis(year, months, storeChoice);
        double salesTy = kpis.TY.Sales;

        double SumAccount(int y, string account, Func<DataRecord, double> value) =>
            _repo.Records
                .Where(r => r.Year == y
                    && stores.Contains(r.Store)
                    && (monthSet == null || monthSet.Count == 0 || monthSet.Contains(r.Month))
                    && r.Account == account)
                .Sum(value);

        var depTy = SumAccount(year, "Depreciation", r => r.TY);
        var depTgt = SumAccount(year, "Depreciation", r => r.TGT);
        var depLy = SumAccount(year - 1, "Depreciation", r => r.TY);
        var amortTy = SumAccount(year, "Amortization", r => r.TY);
        var amortTgt = SumAccount(year, "Amortization", r => r.TGT);
        var amortLy = SumAccount(year - 1, "Amortization", r => r.TY);
        var hoDaTy = SumAccount(year, "HO DA", r => r.TY);
        var hoDaTgt = SumAccount(year, "HO DA", r => r.TGT);
        var hoDaLy = SumAccount(year - 1, "HO DA", r => r.TY);

        var rows = new List<MetricBreakdownRow>
        {
            new()
            {
                Label = "SBU CM",
                TY = kpis.TY.SbuCm,
                TGT = kpis.TGT.SbuCm,
                LY = kpis.LY.SbuCm,
                PctOfSales = salesTy != 0 ? kpis.TY.SbuCm / salesTy : 0
            },
            new()
            {
                Label = "Depreciation",
                TY = depTy,
                TGT = depTgt,
                LY = depLy,
                PctOfSales = salesTy != 0 ? depTy / salesTy : 0
            },
            new()
            {
                Label = "Amortization",
                TY = amortTy,
                TGT = amortTgt,
                LY = amortLy,
                PctOfSales = salesTy != 0 ? amortTy / salesTy : 0
            },
            new()
            {
                Label = "HO DA",
                TY = hoDaTy,
                TGT = hoDaTgt,
                LY = hoDaLy,
                PctOfSales = salesTy != 0 ? hoDaTy / salesTy : 0
            }
        };

        return new MetricBreakdown
        {
            Title = "SBU EBITDA",
            Formula = "SBU CM + Depreciation + Amortization + HO DA.",
            TY = kpis.TY.SbuEbitda,
            TGT = kpis.TGT.SbuEbitda,
            LY = kpis.LY.SbuEbitda,
            PctOfSales = salesTy != 0 ? kpis.TY.SbuEbitda / salesTy : 0,
            Rows = rows
        };
    }

    private IEnumerable<MetricBreakdownRow> SumAccounts(
        int year,
        IReadOnlyCollection<string> months,
        string storeChoice,
        IEnumerable<string> accounts,
        double salesTy)
    {
        var stores = GetSelectedStores(storeChoice);
        var monthSet = months.Count == 0 ? null : months.ToHashSet(StringComparer.Ordinal);

        bool InScope(DataRecord r, int y) =>
            r.Year == y
            && stores.Contains(r.Store)
            && (monthSet == null || monthSet.Count == 0 || monthSet.Contains(r.Month));

        foreach (var account in accounts)
        {
            double ty = _repo.Records.Where(r => InScope(r, year) && r.Account == account).Sum(r => r.TY);
            double tgt = _repo.Records.Where(r => InScope(r, year) && r.Account == account).Sum(r => r.TGT);
            double ly = _repo.Records.Where(r => InScope(r, year - 1) && r.Account == account).Sum(r => r.TY);
            yield return new MetricBreakdownRow
            {
                Label = account,
                TY = ty,
                TGT = tgt,
                LY = ly,
                PctOfSales = salesTy != 0 ? ty / salesTy : 0
            };
        }
    }
}
