namespace FoodOpsDashboard.Services;

/// <summary>
/// Canonical account list and labels from the FO MPT workbook.
/// </summary>
public static class AccountCatalog
{
    public static readonly string[] Accounts =
    {
        "Sales",
        "Cost of Sales",
        "Miscellaneous Income",
        "Salaries & Wages",
        "Officers' Salaries",
        "Agency Services",
        "Security Services",
        "Directors' Fees/Per Diem",
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
        "Marketing, Advertising & Promo",
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
        "Business Research & Dev. Expense",
        "Dues & Subscription",
        "Depreciation",
        "Amortization",
        "Royalties",
        "Other Expenses",
        "Miscellaneous",
        "HO",
        "HO DA",
        "Corp & EO Share",
        "Interest Expense",
        "GOC Expenses"
    };

    /// <summary>Revenue-side accounts (not expenses).</summary>
    public static readonly HashSet<string> RevenueAccounts = new(StringComparer.Ordinal)
    {
        "Sales",
        "Cost of Sales",
        "Miscellaneous Income"
    };

    /// <summary>Below SBU CM: HO allocation, EBITDA add-back, and post-IFO charges.</summary>
    public static readonly HashSet<string> BelowSbuCmAccounts = new(StringComparer.Ordinal)
    {
        "HO",
        "HO DA",
        "Corp & EO Share",
        "Interest Expense",
        "GOC Expenses"
    };

    /// <summary>Accounts excluded from SBU CM (store operating expense roll-up).</summary>
    public static readonly HashSet<string> SbuCmExclusions =
        RevenueAccounts.Union(BelowSbuCmAccounts).ToHashSet(StringComparer.Ordinal);

    public static bool IsStoreOperatingExpense(string account) =>
        !SbuCmExclusions.Contains(account);

    /// <summary>Accounts excluded from Top 5 operating expense ranking.</summary>
    public static readonly HashSet<string> TopExpenseExclusions = new(StringComparer.Ordinal)
    {
        "Sales",
        "Cost of Sales",
        "Miscellaneous Income",
        "HO",
        "HO DA",
        "Corp & EO Share",
        "Interest Expense",
        "GOC Expenses"
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Officers Salaries"] = "Officers' Salaries",
        ["Directors Fees"] = "Directors' Fees/Per Diem",
        ["Marketing & Advertising"] = "Marketing, Advertising & Promo",
        ["Business Research & Dev"] = "Business Research & Dev. Expense",
        ["Add: Miscellaneous Income"] = "Miscellaneous Income",
        ["Miscellaneous Income"] = "Miscellaneous Income",
        ["Other Expenses (CTK allocation 1%)"] = "Other Expenses",
        ["Corp& EO Share"] = "Corp & EO Share",
        ["Corp & EO Share"] = "Corp & EO Share",
        ["Gross Profit"] = "",
        ["Gross Profit "] = "",
        ["Selling Profit"] = "",
        ["TOTAL STORE OPERATING EXPENSES"] = "",
        ["STORE CM"] = "",
        ["STORE CM  "] = "",
        ["DA"] = "",
        ["Store EBITDA"] = "",
        ["SBU CM"] = "",
        ["SBU EBITDA"] = "",
        ["SBU IFO"] = "",
        ["Corp & Shared"] = "",
        ["EO Share"] = "",
        ["Net Income"] = "",
        ["Net Income After Other Expenses"] = ""
    };

    private static readonly HashSet<string> AccountSet =
        Accounts.ToHashSet(StringComparer.Ordinal);

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var label = raw.Trim();
        if (Aliases.TryGetValue(label, out var mapped))
            return string.IsNullOrEmpty(mapped) ? null : mapped;
        return AccountSet.Contains(label) ? label : null;
    }

    public static bool IsDetailAccount(string? raw) => Normalize(raw) is not null;
}
