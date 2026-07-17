using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FoodOpsDashboard.Models;
using FoodOpsDashboard.Services;

namespace FoodOpsDashboard;

public partial class MainWindow : Window
{
    private readonly DataRepository _repo = new();
    private readonly DashboardCalculator _calc;
    private IReadOnlyList<MonthlyTrendPoint>? _lastTrend;
    private string? _storeNameBeforeEdit;
    private bool _suppressFilterEvents;

    public MainWindow()
    {
        InitializeComponent();
        _calc = new DashboardCalculator(_repo);

        BuildMonthChecks();
        RefreshYearDropdowns();
        RefreshStoreDropdowns();
        StoreCombo.SelectedIndex = 0;

        InitDataEntryFilters();
        StoreGrid.ItemsSource = _repo.Stores;

        ShowView("dashboard");
        RefreshDashboard();
        RefreshDataGrid();
        SetStatus("Ready — select one or more months to consolidate");
    }

    // ===================== NAV / SAVE =====================

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (NavDashboard.IsChecked == true) ShowView("dashboard");
        else if (NavDataEntry.IsChecked == true) ShowView("data");
        else if (NavStoreList.IsChecked == true) ShowView("stores");
    }

    private void ShowView(string which)
    {
        DashboardView.Visibility = which == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        DataEntryView.Visibility = which == "data" ? Visibility.Visible : Visibility.Collapsed;
        StoreListView.Visibility = which == "stores" ? Visibility.Visible : Visibility.Collapsed;

        if (which == "dashboard")
        {
            RefreshYearDropdowns();
            RefreshStoreDropdowns();
            RefreshDashboard();
        }
        else if (which == "data")
        {
            RefreshYearDropdowns();
            RefreshDataStoreFilter();
            RefreshDataGrid();
        }
    }

    private void OnSaveAllClick(object sender, RoutedEventArgs e)
    {
        CommitGrids();
        _repo.Save();
        RefreshYearDropdowns();
        RefreshStoreDropdowns();
        RefreshDashboard();
        RefreshDataGrid();
        SetStatus($"Saved {DateTime.Now:HH:mm:ss} — {_repo.Records.Count:N0} rows, {_repo.Stores.Count} stores");
    }

    private void OnSaveStoresClick(object sender, RoutedEventArgs e)
    {
        CommitGrids();
        foreach (var year in _repo.GetAvailableYears())
            _repo.EnsureCompleteGrid(year);
        _repo.Save();
        RefreshStoreDropdowns();
        RefreshDataStoreFilter();
        SetStatus($"Stores saved {DateTime.Now:HH:mm:ss}");
    }

    private void CommitGrids()
    {
        DataEntryGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DataEntryGrid.CommitEdit(DataGridEditingUnit.Row, true);
        StoreGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        StoreGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ===================== DASHBOARD =====================

    private void BuildMonthChecks()
    {
        MonthCheckPanel.Children.Clear();
        foreach (var month in DataRepository.Months)
        {
            var btn = new ToggleButton
            {
                Content = month,
                Style = (Style)FindResource("MonthToggleStyle"),
                IsChecked = month == "Jan"
            };
            btn.Checked += OnMonthCheckChanged;
            btn.Unchecked += OnMonthCheckChanged;
            MonthCheckPanel.Children.Add(btn);
        }
    }

    private List<string> GetSelectedMonths()
    {
        return MonthCheckPanel.Children
            .OfType<ToggleButton>()
            .Where(c => c.IsChecked == true)
            .Select(c => c.Content?.ToString() ?? "")
            .Where(m => m.Length > 0)
            .ToList();
    }

    private void OnSelectAllMonths(object sender, RoutedEventArgs e)
    {
        _suppressFilterEvents = true;
        foreach (var btn in MonthCheckPanel.Children.OfType<ToggleButton>())
            btn.IsChecked = true;
        _suppressFilterEvents = false;
        RefreshDashboard();
    }

    private void OnClearMonths(object sender, RoutedEventArgs e)
    {
        _suppressFilterEvents = true;
        foreach (var btn in MonthCheckPanel.Children.OfType<ToggleButton>())
            btn.IsChecked = false;
        _suppressFilterEvents = false;
        RefreshDashboard();
    }

    private void OnMonthCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressFilterEvents || !IsLoaded) return;
        RefreshDashboard();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents || !IsLoaded) return;

        if (YearCombo.SelectedItem is int year)
            _repo.EnsureCompleteGrid(year);

        RefreshDashboard();
    }

    private void OnTrendCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastTrend != null) DrawTrendChart(_lastTrend);
    }

    private void RefreshDashboard()
    {
        if (YearCombo.SelectedItem is not int year) return;
        if (StoreCombo.SelectedItem is not string store) return;

        var months = GetSelectedMonths();
        // No month checked → treat as full year (all months)
        if (months.Count == 0)
            months = DataRepository.Months.ToList();

        var kpis = _calc.ComputeKpis(year, months, store);
        BuildKpiCards(kpis);

        ExpenseGrid.ItemsSource = _calc.ComputeTopExpenses(year, months, store, topN: 5);

        _lastTrend = _calc.ComputeMonthlyTrend(year, store);
        DrawTrendChart(_lastTrend);
    }

    private void RefreshYearDropdowns()
    {
        _suppressFilterEvents = true;
        var years = _repo.GetYearChoices();
        int? prevDash = YearCombo.SelectedItem as int?;
        int? prevData = DataYearCombo.SelectedItem as int?;

        YearCombo.ItemsSource = years;
        DataYearCombo.ItemsSource = years;

        YearCombo.SelectedItem = prevDash is int pd && years.Contains(pd)
            ? pd
            : years.FirstOrDefault(y => y == DataRepository.DefaultYear, years.First());
        DataYearCombo.SelectedItem = prevData is int py && years.Contains(py)
            ? py
            : YearCombo.SelectedItem;

        _suppressFilterEvents = false;
    }

    private void RefreshStoreDropdowns()
    {
        _suppressFilterEvents = true;
        var previous = StoreCombo.SelectedItem as string;
        var items = _repo.BuildStoreDropdown();
        StoreCombo.ItemsSource = items;
        StoreCombo.SelectedItem = previous != null && items.Contains(previous) ? previous : items.FirstOrDefault();
        _suppressFilterEvents = false;
    }

    private void BuildKpiCards(KpiSet k)
    {
        KpiPanel.Children.Clear();

        // Key financial metrics (formulas documented in README.md)
        KpiPanel.Children.Add(BuildCard("Sales", k.TY.Sales, k.TGT.Sales, k.LY.Sales, null));
        KpiPanel.Children.Add(BuildCard("Cost of Sales", k.TY.CostOfSales, k.TGT.CostOfSales, k.LY.CostOfSales, PctOf(k.TY.CostOfSales, k.TY.Sales)));
        KpiPanel.Children.Add(BuildCard("Gross Profit", k.TY.GrossProfit, k.TGT.GrossProfit, k.LY.GrossProfit, PctOf(k.TY.GrossProfit, k.TY.Sales)));
        KpiPanel.Children.Add(BuildCard("Total OPEX", k.TY.TotalOpex, k.TGT.TotalOpex, k.LY.TotalOpex, PctOf(k.TY.TotalOpex, k.TY.Sales)));
        KpiPanel.Children.Add(BuildCard("SBU CM", k.TY.SbuCm, k.TGT.SbuCm, k.LY.SbuCm, PctOf(k.TY.SbuCm, k.TY.Sales)));
        KpiPanel.Children.Add(BuildCard("SBU EBITDA", k.TY.SbuEbitda, k.TGT.SbuEbitda, k.LY.SbuEbitda, PctOf(k.TY.SbuEbitda, k.TY.Sales)));
        KpiPanel.Children.Add(BuildCard("Net Income", k.TY.NetIncome, k.TGT.NetIncome, k.LY.NetIncome, PctOf(k.TY.NetIncome, k.TY.Sales)));
    }

    private static double? PctOf(double value, double sales) => sales != 0 ? value / sales : null;

    private static Border BuildCard(string title, double ty, double tgt, double ly, double? pctOfSales)
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            Margin = new Thickness(0, 0, 0, 6)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "\u20B1" + ty.ToString("#,##0", CultureInfo.InvariantCulture),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            Margin = new Thickness(0, 0, 0, 8)
        });

        // GR = growth rate vs last year — Excel: ABS((TY-LY)/LY) with sign from TY>=LY
        // Equivalent to (TY - LY) / |LY|
        double gr = GrowthRate(ty, ly);
        stack.Children.Add(GrBadge(gr));

        var variances = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        variances.Children.Add(VarianceBadge("vs TGT", ty - tgt, tgt));
        variances.Children.Add(new Border { Width = 6 });
        variances.Children.Add(VarianceBadge("vs LY", ty - ly, ly));
        stack.Children.Add(variances);

        if (pctOfSales is double p)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{p:0.0%} of sales",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 12, 12, 12),
            Margin = new Thickness(0, 0, 8, 0),
            Child = stack
        };
    }

    /// <summary>
    /// Excel Growth rate: IFERROR(ABS(VarVsLy/LY)*IF(TY&gt;=LY,1,-1),"")
    /// which equals (TY - LY) / |LY| when LY ≠ 0.
    /// </summary>
    private static double GrowthRate(double ty, double ly) =>
        ly != 0 ? (ty - ly) / Math.Abs(ly) : 0;

    private static Border GrBadge(double gr)
    {
        bool positive = gr >= 0;
        var fg = (Brush)Application.Current.Resources[positive ? "PositiveBrush" : "NegativeBrush"];
        var bg = positive
            ? new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xF2));
        string arrow = positive ? "\u25B2" : "\u25BC";

        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = fg,
                Text = $"GR  {arrow} {gr:+0.0%;-0.0%}"
            }
        };
    }

    private static Border VarianceBadge(string label, double diff, double baseline)
    {
        double pct = baseline != 0 ? diff / Math.Abs(baseline) : 0;
        bool positive = diff >= 0;
        var fg = (Brush)Application.Current.Resources[positive ? "PositiveBrush" : "NegativeBrush"];

        return new Border
        {
            Background = positive
                ? new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xF2)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            Child = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg,
                Text = $"{label} {pct:+0.0%;-0.0%}"
            }
        };
    }

    // ===================== DATA ENTRY =====================

    private void InitDataEntryFilters()
    {
        _suppressFilterEvents = true;
        var months = new List<string> { "(All)" };
        months.AddRange(DataRepository.Months);
        DataMonthCombo.ItemsSource = months;
        DataMonthCombo.SelectedItem = "Jan";
        RefreshDataStoreFilter();
        _suppressFilterEvents = false;
    }

    private void RefreshDataStoreFilter()
    {
        _suppressFilterEvents = true;
        var previous = DataStoreCombo.SelectedItem as string;
        var stores = new List<string> { "(All)" };
        stores.AddRange(_repo.Stores.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
        DataStoreCombo.ItemsSource = stores;
        DataStoreCombo.SelectedItem = previous != null && stores.Contains(previous)
            ? previous
            : stores.Skip(1).FirstOrDefault() ?? "(All)";
        _suppressFilterEvents = false;
    }

    private void OnDataFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents || !IsLoaded) return;
        if (DataYearCombo.SelectedItem is int year)
            _repo.EnsureCompleteGrid(year);
        RefreshDataGrid();
    }

    private void RefreshDataGrid()
    {
        if (DataYearCombo.SelectedItem is not int year) return;
        string? month = DataMonthCombo.SelectedItem as string;
        string? store = DataStoreCombo.SelectedItem as string;
        var rows = _repo.FilterEntryRows(year, month, store).ToList();
        DataEntryGrid.ItemsSource = rows;
        DataRowCountText.Text = $"{rows.Count:N0} rows · LY = {year - 1} TY";
    }

    private void OnDataEntryPreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.Column.Header?.ToString() is not ("TY" or "TGT")) return;
        if (e.EditingElement is not TextBox box) return;

        box.PreviewTextInput -= OnNumericPreviewTextInput;
        box.PreviewTextInput += OnNumericPreviewTextInput;
        DataObject.RemovePastingHandler(box, OnNumericPaste);
        DataObject.AddPastingHandler(box, OnNumericPaste);
    }

    private static void OnNumericPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox box) return;
        string next = box.Text.Remove(box.SelectionStart, box.SelectionLength)
            .Insert(box.SelectionStart, e.Text);
        e.Handled = !IsValidNumberDraft(next);
    }

    private static void OnNumericPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.CancelCommand(); return; }
        string paste = (string)e.DataObject.GetData(DataFormats.Text)!;
        string next = box.Text.Remove(box.SelectionStart, box.SelectionLength)
            .Insert(box.SelectionStart, paste);
        if (!IsValidNumberDraft(next)) e.CancelCommand();
    }

    /// <summary>Allows empty, "-", ".", "-.", digits with optional one decimal point.</summary>
    private static bool IsValidNumberDraft(string text)
    {
        if (string.IsNullOrEmpty(text) || text is "-" or "." or "-.") return true;
        return Regex.IsMatch(text, @"^-?\d*(\.\d*)?$");
    }

    private void OnDataEntryCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Column.Header?.ToString() is not ("TY" or "TGT")) return;
        if (e.EditingElement is not TextBox box) return;

        string text = box.Text.Trim().Replace(",", "");
        if (string.IsNullOrEmpty(text) || text is "-" or "." or "-.")
        {
            box.Text = "0";
            return;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out _))
        {
            e.Cancel = true;
            MessageBox.Show("Please enter a valid number.", "Data Entry", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportExcelClick(object sender, RoutedEventArgs e)
    {
        if (DataYearCombo.SelectedItem is not int year)
        {
            MessageBox.Show("Select a year to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CommitGrids();
        _repo.EnsureCompleteGrid(year);
        _repo.Save();

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export year data",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"LFR-FoodOps-{year}.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx"
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            ExcelExchange.ExportYear(dlg.FileName, year, _repo.Records, _repo.Stores);
            SetStatus($"Exported {year} → {System.IO.Path.GetFileName(dlg.FileName)}");
            MessageBox.Show(
                $"Exported all {year} DATA rows (and Stores) to:\n{dlg.FileName}",
                "Export complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportExcelClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import year data",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(
            "Import will update TY/TGT for matching Year / Month / Store / Account rows from the file.\n\nContinue?",
            "Import xlsx",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            CommitGrids();
            var (year, updated, addedStores) = ExcelExchange.Import(dlg.FileName, _repo);
            _repo.Save();

            RefreshYearDropdowns();
            DataYearCombo.SelectedItem = year;
            RefreshStoreDropdowns();
            RefreshDataStoreFilter();
            RefreshDataGrid();
            RefreshDashboard();

            SetStatus($"Imported {System.IO.Path.GetFileName(dlg.FileName)} — {updated} cells updated");
            MessageBox.Show(
                $"Import complete for year {year}.\nRows updated: {updated}\nStores added: {addedStores}",
                "Import complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ===================== STORE LIST =====================

    private void OnAddStoreClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = NewStoreNameBox.Text.Trim();
            var group = NewStoreGroupBox.Text.Trim();
            var years = _repo.GetYearChoices();
            _repo.AddStore(name, group, years);
            _repo.Save();
            NewStoreNameBox.Clear();
            NewStoreGroupBox.Clear();
            RefreshStoreDropdowns();
            RefreshDataStoreFilter();
            RefreshDataGrid();
            SetStatus($"Added store '{name}'");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Add Store", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRemoveStoreClick(object sender, RoutedEventArgs e)
    {
        if (StoreGrid.SelectedItem is not StoreInfo store)
        {
            MessageBox.Show("Select a store row first.", "Remove Store", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Remove '{store.Name}' and all of its DATA rows?",
            "Remove Store",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _repo.RemoveStore(store);
        _repo.Save();
        RefreshStoreDropdowns();
        RefreshDataStoreFilter();
        RefreshDataGrid();
        SetStatus($"Removed store '{store.Name}'");
    }

    private void OnStoreBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is StoreInfo store && e.Column.Header?.ToString() == "Store Name")
            _storeNameBeforeEdit = store.Name;
    }

    private void OnStoreCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not StoreInfo store) return;
        if (e.Column.Header?.ToString() != "Store Name") return;

        Dispatcher.BeginInvoke(() =>
        {
            var oldName = _storeNameBeforeEdit;
            _storeNameBeforeEdit = null;
            if (oldName == null || oldName == store.Name) return;

            if (string.IsNullOrWhiteSpace(store.Name))
            {
                store.Name = oldName;
                MessageBox.Show("Store name cannot be empty.", "Store List", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_repo.Stores.Any(s => !ReferenceEquals(s, store) &&
                                      string.Equals(s.Name, store.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var attempted = store.Name;
                store.Name = oldName;
                MessageBox.Show($"Store '{attempted}' already exists.", "Store List", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _repo.RenameStoreKey(oldName, store.Name);
            RefreshStoreDropdowns();
            RefreshDataStoreFilter();
            RefreshDataGrid();
        });
    }

    // ===================== TREND CHART =====================

    private void DrawTrendChart(IReadOnlyList<MonthlyTrendPoint> points)
    {
        TrendCanvas.Children.Clear();

        double width = TrendCanvas.ActualWidth;
        double height = TrendCanvas.ActualHeight;
        if (width < 20 || height < 20 || points.Count == 0) return;

        const double leftPad = 52, rightPad = 8, topPad = 8, bottomPad = 24;
        double plotWidth = width - leftPad - rightPad;
        double plotHeight = height - topPad - bottomPad;
        if (plotWidth <= 0 || plotHeight <= 0) return;

        double max = points.SelectMany(p => new[] { p.TySales, p.TgtSales, p.LySales }).DefaultIfEmpty(0).Max();
        double min = points.SelectMany(p => new[] { p.TySales, p.TgtSales, p.LySales }).DefaultIfEmpty(0).Min();
        min = Math.Min(0, min);
        if (max <= min) max = min + 1;

        double X(int i) => leftPad + plotWidth * i / Math.Max(points.Count - 1, 1);
        double Y(double v) => topPad + plotHeight - (v - min) / (max - min) * plotHeight;

        for (int i = 0; i <= 4; i++)
        {
            double v = min + (max - min) * i / 4;
            double y = Y(v);

            TrendCanvas.Children.Add(new Line
            {
                X1 = leftPad, X2 = width - rightPad, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF3)),
                StrokeThickness = 1
            });

            var label = new TextBlock
            {
                Text = FormatCompact(v),
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"]
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
            TrendCanvas.Children.Add(label);
        }

        for (int i = 0; i < points.Count; i++)
        {
            var label = new TextBlock
            {
                Text = points[i].Month,
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"]
            };
            Canvas.SetLeft(label, X(i) - 10);
            Canvas.SetTop(label, height - bottomPad + 6);
            TrendCanvas.Children.Add(label);
        }

        DrawSeries(points.Select(p => p.LySales).ToList(), X, Y, "LySeriesBrush", dashed: true);
        DrawSeries(points.Select(p => p.TgtSales).ToList(), X, Y, "TgtSeriesBrush", dashed: true);
        DrawSeries(points.Select(p => p.TySales).ToList(), X, Y, "TySeriesBrush", dashed: false);
    }

    private void DrawSeries(IReadOnlyList<double> values, Func<int, double> x, Func<double, double> y, string brushKey, bool dashed)
    {
        var brush = (Brush)Application.Current.Resources[brushKey];
        var polyline = new Polyline
        {
            Stroke = brush,
            StrokeThickness = dashed ? 1.5 : 2,
            StrokeDashArray = dashed ? new DoubleCollection { 4, 3 } : null
        };

        var points = new PointCollection();
        for (int i = 0; i < values.Count; i++)
            points.Add(new Point(x(i), y(values[i])));
        polyline.Points = points;
        TrendCanvas.Children.Add(polyline);

        for (int i = 0; i < values.Count; i++)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Fill = brush };
            Canvas.SetLeft(dot, x(i) - 2.5);
            Canvas.SetTop(dot, y(values[i]) - 2.5);
            TrendCanvas.Children.Add(dot);
        }
    }

    private static string FormatCompact(double v)
    {
        double abs = Math.Abs(v);
        if (abs >= 1_000_000) return (v / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (abs >= 1_000) return (v / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return v.ToString("0", CultureInfo.InvariantCulture);
    }
}
