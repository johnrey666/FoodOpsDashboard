using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private bool _metricDetailAnimating;
    private Rect _metricFlipOrigin;
    private FrameworkElement? _metricFlipSource;
    private const double MetricModalWidth = 840;
    private const double MetricModalHeight = 620;
    /// <summary>One half-turn (front→back) = land on detail face.</summary>
    private const double FlipOpenDegrees = 180;
    private static readonly Duration FlipTravel = new(TimeSpan.FromMilliseconds(1600));
    private static readonly Duration FlipClose = new(TimeSpan.FromMilliseconds(1100));

    /// <summary>
    /// Card spin angle in degrees. ScaleX = |cos(angle)| so it reads as a real Y-axis flip,
    /// and the face swaps whenever cos goes negative (back of card).
    /// </summary>
    public static readonly DependencyProperty FlipAngleProperty =
        DependencyProperty.Register(
            nameof(FlipAngle),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnFlipAngleChanged));

    public double FlipAngle
    {
        get => (double)GetValue(FlipAngleProperty);
        set => SetValue(FlipAngleProperty, value);
    }

    private static void OnFlipAngleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var w = (MainWindow)d;
        double angle = (double)e.NewValue;
        double cos = Math.Cos(angle * Math.PI / 180.0);
        w.MetricDetailFlip.ScaleX = Math.Max(0.001, Math.Abs(cos));

        bool showBack = cos < 0;
        w.MetricDetailFront.Visibility = showBack ? Visibility.Collapsed : Visibility.Visible;
        w.MetricDetailBack.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
    }

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

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && MetricDetailOverlay.Visibility == Visibility.Visible)
            {
                CloseMetricDetail();
                e.Handled = true;
            }
        };

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
        KpiPanel.Children.Add(BuildCard("Total OPEX", k.TY.TotalOpex, k.TGT.TotalOpex, k.LY.TotalOpex, PctOf(k.TY.TotalOpex, k.TY.Sales),
            detailMetric: "Total OPEX"));
        KpiPanel.Children.Add(BuildCard("SBU CM", k.TY.SbuCm, k.TGT.SbuCm, k.LY.SbuCm, PctOf(k.TY.SbuCm, k.TY.Sales)));
        KpiPanel.Children.Add(BuildCard("SBU EBITDA", k.TY.SbuEbitda, k.TGT.SbuEbitda, k.LY.SbuEbitda, PctOf(k.TY.SbuEbitda, k.TY.Sales),
            detailMetric: "SBU EBITDA"));
        KpiPanel.Children.Add(BuildCard("Net Income", k.TY.NetIncome, k.TGT.NetIncome, k.LY.NetIncome, PctOf(k.TY.NetIncome, k.TY.Sales)));
    }

    private static double? PctOf(double value, double sales) => sales != 0 ? value / sales : null;

    private Border BuildCard(string title, double ty, double tgt, double ly, double? pctOfSales, string? detailMetric = null)
    {
        var stack = new StackPanel();
        Border? card = null;

        var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (detailMetric != null)
        {
            var detailsHint = new TextBlock
            {
                Text = "Details",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["AccentBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(detailsHint, Dock.Right);
            titleRow.Children.Add(detailsHint);
        }
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(titleRow);

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

        card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 12, 12, 12),
            Margin = new Thickness(0, 0, 8, 0),
            Child = stack
        };

        if (detailMetric != null)
        {
            card.Cursor = Cursors.Hand;
            card.ToolTip = $"Open {title} breakdown";
            card.MouseLeftButtonUp += (_, _) => OpenMetricBreakdown(detailMetric, card, ty);
            card.MouseEnter += (_, _) =>
            {
                card.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
                card.Background = (Brush)Application.Current.Resources["KpiHeroBrush"];
            };
            card.MouseLeave += (_, _) =>
            {
                card.BorderBrush = (Brush)Application.Current.Resources["BorderBrush"];
                card.Background = (Brush)Application.Current.Resources["CardBrush"];
            };
        }

        return card;
    }

    private void OpenMetricBreakdown(string metric, FrameworkElement sourceCard, double tyAmount)
    {
        if (_metricDetailAnimating || MetricDetailOverlay.Visibility == Visibility.Visible) return;
        if (YearCombo.SelectedItem is not int year) return;
        if (StoreCombo.SelectedItem is not string store) return;

        var months = GetSelectedMonths();
        if (months.Count == 0)
            months = DataRepository.Months.ToList();

        MetricBreakdown breakdown = metric switch
        {
            "Total OPEX" => _calc.ComputeTotalOpexBreakdown(year, months, store),
            "SBU EBITDA" => _calc.ComputeSbuEbitdaBreakdown(year, months, store),
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null)
        };

        MetricDetailTitle.Text = breakdown.Title;
        MetricDetailFormula.Text = "  " + breakdown.Formula;
        MetricDetailFrontTitle.Text = breakdown.Title;
        MetricDetailFrontAmount.Text = FormatPeso(tyAmount);
        MetricDetailTy.Text = FormatPeso(breakdown.TY);
        MetricDetailTgt.Text = FormatPeso(breakdown.TGT);
        MetricDetailLy.Text = FormatPeso(breakdown.LY);
        MetricDetailPctSales.Text = breakdown.PctOfSales.ToString("0.0%", CultureInfo.InvariantCulture);
        MetricDetailGr.Text = breakdown.GrowthPct.ToString("+0.0%;-0.0%", CultureInfo.InvariantCulture);
        MetricDetailGr.Foreground = (Brush)Application.Current.Resources[
            breakdown.GrowthPct >= 0 ? "PositiveBrush" : "NegativeBrush"];
        BuildCompositionInfographic(breakdown);

        PlayOpenCardFlip(sourceCard);
    }

    private void BuildCompositionInfographic(MetricBreakdown breakdown)
    {
        MetricDetailCompositionPanel.Children.Clear();

        // For OPEX, share is vs |TY| sum of abs; for EBITDA, share vs |components|
        double denom = breakdown.Rows.Sum(r => Math.Abs(r.TY));
        if (denom == 0) denom = 1;

        var visibleRows = breakdown.Rows
            .Where(r => Math.Abs(r.TY) > 0.5 || Math.Abs(r.TGT) > 0.5 || Math.Abs(r.LY) > 0.5)
            .ToList();
        if (visibleRows.Count == 0)
            visibleRows = breakdown.Rows.ToList();

        MetricDetailRowCount.Text = $"{visibleRows.Count} components · share of total";

        // Accent palette for bars
        var accents = new[]
        {
            Color.FromRgb(0x0E, 0xA5, 0xE9),
            Color.FromRgb(0x38, 0xBD, 0xF8),
            Color.FromRgb(0x06, 0xB6, 0xD4),
            Color.FromRgb(0x14, 0xB8, 0xA6),
            Color.FromRgb(0x64, 0x74, 0x8B)
        };

        int i = 0;
        foreach (var row in visibleRows)
        {
            double share = Math.Abs(row.TY) / denom;
            var accent = accents[i % accents.Length];
            i++;

            var rowBorder = new Border
            {
                Background = Brushes.White,
                BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });

            // Left: label + share bar
            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var labelRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            labelRow.Children.Add(new TextBlock
            {
                Text = share.ToString("0.0%", CultureInfo.InvariantCulture),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            });
            DockPanel.SetDock(labelRow.Children[^1], Dock.Right);
            labelRow.Children.Add(new TextBlock
            {
                Text = row.Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            left.Children.Add(labelRow);

            var track = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
                ClipToBounds = true
            };
            var fill = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            // Bind width after layout via share of available track
            track.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width > 0)
                    fill.Width = Math.Max(2, e.NewSize.Width * share);
            };
            track.Child = fill;
            left.Children.Add(track);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            // Middle: TY / TGT / LY
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            mid.Children.Add(MetricMiniStat("TY", FormatPeso(row.TY)));
            mid.Children.Add(MetricMiniStat("TGT", FormatPeso(row.TGT), muted: true));
            mid.Children.Add(MetricMiniStat("LY", FormatPeso(row.LY), muted: true));
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            // Right: % sales + GR
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(new TextBlock
            {
                Text = row.PctOfSales.ToString("0.0%", CultureInfo.InvariantCulture) + " of sales",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 6)
            });
            bool up = row.GrowthPct >= 0;
            right.Children.Add(new Border
            {
                Background = up
                    ? new SolidColorBrush(Color.FromRgb(0xEC, 0xFD, 0xF5))
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xF2)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock
                {
                    Text = $"GR {(up ? "▲" : "▼")} {row.GrowthPct:+0.0%;-0.0%}",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)Application.Current.Resources[up ? "PositiveBrush" : "NegativeBrush"]
                }
            });
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            rowBorder.Child = grid;
            MetricDetailCompositionPanel.Children.Add(rowBorder);
        }
    }

    private static StackPanel MetricMiniStat(string label, string value, bool muted = false)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 2),
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Width = 28,
                    Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = value,
                    FontSize = 11,
                    FontWeight = muted ? FontWeights.Medium : FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources[muted ? "TextMutedBrush" : "TextPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private static string FormatPeso(double value) =>
        "\u20B1" + value.ToString("#,##0", CultureInfo.InvariantCulture);

    private Rect GetElementRectIn(FrameworkElement element, FrameworkElement relativeTo)
    {
        var topLeft = element.TransformToVisual(relativeTo).Transform(new Point(0, 0));
        return new Rect(topLeft.X, topLeft.Y, element.ActualWidth, element.ActualHeight);
    }

    private Rect GetCenteredModalRect()
    {
        double stageW = MetricDetailStage.ActualWidth;
        double stageH = MetricDetailStage.ActualHeight;
        double w = Math.Min(MetricModalWidth, Math.Max(320, stageW - 48));
        double h = Math.Min(MetricModalHeight, Math.Max(280, stageH - 48));
        return new Rect((stageW - w) / 2, (stageH - h) / 2, w, h);
    }

    private void PlaceModal(Rect r)
    {
        Canvas.SetLeft(MetricDetailModal, r.X);
        Canvas.SetTop(MetricDetailModal, r.Y);
        MetricDetailModal.Width = r.Width;
        MetricDetailModal.Height = r.Height;
    }

    private void ClearFlightAnimations()
    {
        MetricDetailMove.BeginAnimation(TranslateTransform.XProperty, null);
        MetricDetailMove.BeginAnimation(TranslateTransform.YProperty, null);
        MetricDetailGrow.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MetricDetailGrow.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BeginAnimation(FlipAngleProperty, null);
        MetricDetailScrim.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private void PlayOpenCardFlip(FrameworkElement sourceCard)
    {
        _metricDetailAnimating = true;
        _metricFlipSource = sourceCard;
        MetricDetailOverlay.Visibility = Visibility.Visible;
        MetricDetailOverlay.UpdateLayout();

        _metricFlipOrigin = GetElementRectIn(sourceCard, MetricDetailStage);
        var target = GetCenteredModalRect();

        sourceCard.Opacity = 0;

        // Park modal at final size/position; flight is pure transforms (grow + move + spin)
        PlaceModal(target);
        ClearFlightAnimations();

        double originCx = _metricFlipOrigin.X + _metricFlipOrigin.Width / 2;
        double originCy = _metricFlipOrigin.Y + _metricFlipOrigin.Height / 2;
        double targetCx = target.X + target.Width / 2;
        double targetCy = target.Y + target.Height / 2;

        double startScale = Math.Min(
            _metricFlipOrigin.Width / Math.Max(1, target.Width),
            _metricFlipOrigin.Height / Math.Max(1, target.Height));
        startScale = Math.Clamp(startScale, 0.08, 1.0);

        MetricDetailGrow.ScaleX = startScale;
        MetricDetailGrow.ScaleY = startScale;
        MetricDetailMove.X = originCx - targetCx;
        MetricDetailMove.Y = originCy - targetCy;
        FlipAngle = 0;
        MetricDetailScrim.Opacity = 0;
        MetricDetailModal.Opacity = 1;

        var ease = new QuarticEase { EasingMode = EasingMode.EaseInOut };

        MetricDetailScrim.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, FlipTravel)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            });

        MetricDetailMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(MetricDetailMove.X, 0, FlipTravel) { EasingFunction = ease });
        MetricDetailMove.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(MetricDetailMove.Y, 0, FlipTravel) { EasingFunction = ease });
        MetricDetailGrow.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(startScale, 1, FlipTravel) { EasingFunction = ease });
        MetricDetailGrow.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(startScale, 1, FlipTravel) { EasingFunction = ease });

        // Real Y-spin via cos(angle): 0→180° = one face flip, lands on detail (back)
        var spin = new DoubleAnimation(0, FlipOpenDegrees, FlipTravel)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        spin.Completed += (_, _) =>
        {
            BeginAnimation(FlipAngleProperty, null);
            FlipAngle = FlipOpenDegrees;
            _metricDetailAnimating = false;
        };
        BeginAnimation(FlipAngleProperty, spin);
    }

    private void CloseMetricDetail()
    {
        if (MetricDetailOverlay.Visibility != Visibility.Visible || _metricDetailAnimating) return;
        _metricDetailAnimating = true;

        var target = GetCenteredModalRect();
        var origin = _metricFlipOrigin;
        var ease = new QuarticEase { EasingMode = EasingMode.EaseInOut };

        double originCx = origin.X + origin.Width / 2;
        double originCy = origin.Y + origin.Height / 2;
        double targetCx = target.X + target.Width / 2;
        double targetCy = target.Y + target.Height / 2;

        double endScale = Math.Min(
            origin.Width / Math.Max(1, target.Width),
            origin.Height / Math.Max(1, target.Height));
        endScale = Math.Clamp(endScale, 0.08, 1.0);

        double endX = originCx - targetCx;
        double endY = originCy - targetCy;

        MetricDetailScrim.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(MetricDetailScrim.Opacity, 0, FlipClose)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn }
            });

        MetricDetailMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, endX, FlipClose) { EasingFunction = ease });
        MetricDetailMove.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, endY, FlipClose) { EasingFunction = ease });
        MetricDetailGrow.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, endScale, FlipClose) { EasingFunction = ease });
        MetricDetailGrow.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, endScale, FlipClose) { EasingFunction = ease });

        var spin = new DoubleAnimation(FlipAngle, 0, FlipClose)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        spin.Completed += (_, _) =>
        {
            ClearFlightAnimations();
            FlipAngle = 0;
            if (_metricFlipSource != null)
                _metricFlipSource.Opacity = 1;
            _metricFlipSource = null;
            MetricDetailOverlay.Visibility = Visibility.Collapsed;
            MetricDetailCompositionPanel.Children.Clear();
            _metricDetailAnimating = false;
        };
        BeginAnimation(FlipAngleProperty, spin);
    }

    private void OnMetricDetailScrimClick(object sender, MouseButtonEventArgs e) => CloseMetricDetail();

    private void OnCloseMetricDetailClick(object sender, RoutedEventArgs e) => CloseMetricDetail();

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
