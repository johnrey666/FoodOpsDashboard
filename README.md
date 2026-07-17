# LFR 2026 — Food Operations Dashboard (C# / WPF)

## Metrics & formulas (how the app calculates)

All KPIs are **derived from Data Entry detail accounts** for the selected **Year**, **Month(s)**, and **Store** (including Conso / Group Conso).  
**LY** for any metric = the same formula on **prior-year TY** (not a typed LY column).

| Metric | Formula |
|--------|---------|
| **Sales** | Sum of account `Sales` |
| **Cost of Sales** | Sum of account `Cost of Sales` |
| **Gross Profit** | Sales − Cost of Sales |
| **Total OPEX** | Sum of **all accounts except** `Sales` and `Cost of Sales` (**includes HO**) |
| **SBU CM** | Gross Profit − (all accounts **except** `Sales`, `Cost of Sales`, and **HO**) |
| **SBU EBITDA** | SBU CM + `Depreciation` + `Amortization` |
| **Net Income** | Gross Profit − Total OPEX |

### Card extras

| Field | Formula |
|-------|---------|
| **GR** (growth rate) | `(TY − LY) / \|LY\|` (0 if LY = 0) |
| **vs TGT** | `(TY − TGT) / \|TGT\|` |
| **vs LY** | `(TY − LY) / \|LY\|` |
| **% of sales** | Metric TY ÷ Sales TY (where shown) |

### Other dashboard math

| Item | Formula |
|------|---------|
| **HO** | Head Office allocation account. Excluded from SBU CM; included in Total OPEX. |
| **Top 5 operating expenses** | Expense accounts (not Sales / Cost of Sales / HO), ranked by \|TY\|, top 5 |
| **% UTIL** | Account TY ÷ Account TGT |
| **% OPEX** | Account TY ÷ Total OPEX |
| **% SALES** | Account TY ÷ Sales |
| **VS LY** (expenses) | `(TY − LY) / \|LY\|` for that account |
| **Monthly sales trend** | Always all 12 months for the selected Year + Store (month filter ignored on the chart) |

> **Note:** Some Excel workbooks store `Total Direct Operating Expenses`, `SBU CM`, etc. as ready-made Data rows. This app **does not** read those summary rows for KPIs — it **recalculates** from detail lines using the table above.

---

A fully offline C# rewrite of `fod-dashboard-fixed.xlsx`.

## Views (header nav)

| Nav | What you do |
|-----|-------------|
| **Dashboard** | KPIs, trend, top expenses for Year + Month(s) + Store |
| **Data Entry** | Enter **TY** and **TGT** by Year / Month / Store |
| **Store List** | Add / rename / remove stores |

## Year & last year (LY)

- Pick a **Year** on Dashboard and Data Entry.
- You only enter **TY** and **TGT**.
- **LY is automatic**: it is the **TY from the previous year** for the same Month / Store / Account.
- Example: viewing 2026 → LY comes from 2025 TY. Enter 2027 data → LY comes from 2026 TY.
- The year dropdown includes ±1 around your data so you can start a new year anytime.

## How to run

Needs Windows and the .NET SDK (WPF).

set PATH=C:\Users\fo.technicalsupport\dotnet-sdk;%PATH%
dotnet --version
```
dotnet run
```

## Data storage

- Day-to-day: use **Data Entry** and **Store List** — edits auto-save (no Save button).
- **Export xlsx** / **Import xlsx** on Data Entry: share a full year (DATA + Stores sheets) between PCs.
- To wipe local edits and re-seed from the embedded Excel export, delete the
  folder `%LocalAppData%\FoodOpsDashboard\` and restart the app.

## Notes

- KPI row: Sales, Cost of Sales, Gross Profit, Total OPEX, SBU CM, SBU EBITDA, Net Income.
- Dashboard months are multi-select — select several months to consolidate totals.
- Top 5 operating expenses are ranked by TY amount for the current filters.
- Monthly trend always shows all 12 months for the selected store/year.
