namespace FoodOpsDashboard.Models;

/// <summary>
/// The 7 headline metrics computed for a single value column (TY, TGT or LY),
/// mirroring one row of the DASHBOARD sheet's hidden calc block (e.g. row 100/101/102).
/// </summary>
public sealed class MetricSet
{
    public double Sales { get; init; }
    public double CostOfSales { get; init; }
    public double GrossProfit { get; init; }
    public double SbuCm { get; init; }
    public double SbuEbitda { get; init; }
    public double TotalOpex { get; init; }
    public double NetIncome { get; init; }
}

/// <summary>
/// TY / TGT / LY versions of <see cref="MetricSet"/> for a given month + store selection,
/// mirroring the A100:G102 block on the DASHBOARD sheet.
/// </summary>
public sealed class KpiSet
{
    public required MetricSet TY { get; init; }
    public required MetricSet TGT { get; init; }
    public required MetricSet LY { get; init; }
}

/// <summary>
/// One row of the "TOP OPERATING EXPENSES" table (rows 29-38 on DASHBOARD).
/// </summary>
public sealed class TopExpenseRow
{
    public required string Expense { get; init; }
    public double TY { get; init; }
    public double TGT { get; init; }
    public double LY { get; init; }
    public double PctOfTotalOpex { get; init; }
    public double PctOfTotalSales { get; init; }
    /// <summary>% Utilization = TY / TGT for this account.</summary>
    public double UtilizationPct => TGT != 0 ? TY / TGT : 0;
    public double GrowthAmount => TY - LY;
    public double GrowthPct => LY != 0 ? (TY - LY) / System.Math.Abs(LY) : 0;
}

/// <summary>
/// One row of the "MONTHLY AVERAGE (full year)" table (rows 19-25 on DASHBOARD).
/// </summary>
public sealed class MonthlyAverageRow
{
    public required string Metric { get; init; }
    public double TyAvg { get; init; }
    public double TgtAvg { get; init; }
    public double LyAvg { get; init; }
    public double VsTarget => TyAvg - TgtAvg;
    public double VsLastYearGrowth => LyAvg != 0 ? (TyAvg - LyAvg) / System.Math.Abs(LyAvg) : 0;
}

/// <summary>
/// One point of the "MONTHLY TREND" line chart (P7:U19 on DASHBOARD).
/// </summary>
public sealed class MonthlyTrendPoint
{
    public required string Month { get; init; }
    public double TySales { get; init; }
    public double TgtSales { get; init; }
    public double LySales { get; init; }
    public double NetIncome { get; init; }
    public double GrowthPct { get; init; }
}
