using DaBtDynamicLock.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;       // Color lives here, not in Microsoft.UI

namespace DaBtDynamicLock.App;

/// <summary>
/// Drawing the signal chart.
///
/// Plain shapes on a Canvas - a polyline, a few lines and some rectangles. No
/// charting library: the picture is simple enough that one would be a
/// dependency to restore before anything could be built, and this project keeps
/// its checks runnable with nothing but the SDK.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>Room for the axis labels, in DIPs.</summary>
    private const double AxisLeft = 46;
    private const double AxisBottom = 26;
    private const double AxisTop = 8;

    /// <summary>Clear space two time labels have to leave between them.</summary>
    private const double LabelGap = 14;

    /// <summary>The range in view. 15 minutes to start with - long enough to
    /// show a walk away from the desk, short enough to show single gaps.</summary>
    private double _range = 900;

    private void BuildRangeButtons()
    {
        RangeButtons.Children.Clear();
        foreach (double seconds in ChartLayout.Ranges)
        {
            var button = new Button
            {
                Content = Texts.Get(RangeKey(seconds)),
                Tag = seconds,
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(12),
            };
            // The chosen one is marked by its accent, the way the flags are
            // marked by the bar under them - one way of showing "this one" in
            // the whole app rather than two.
            if (Math.Abs(seconds - _range) < 0.5)
                button.Background = (Brush)Application.Current
                    .Resources["AccentFillColorDefaultBrush"];
            button.Click += OnRangeChosen;
            RangeButtons.Children.Add(button);
        }
    }

    private static string RangeKey(double seconds) => seconds switch
    {
        120 => "range_2min",
        300 => "range_5min",
        900 => "range_15min",
        3600 => "range_1h",
        28800 => "range_8h",
        _ => "range_1day",
    };

    private void OnRangeChosen(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: double seconds })
            return;
        _range = seconds;
        BuildRangeButtons();
        DrawChart();
    }

    private void OnPlotResized(object sender, SizeChangedEventArgs e) => DrawChart();

    /// <summary>When the chart was last drawn, on the monotonic clock.</summary>
    private double _chartDrawnAt = double.NegativeInfinity;

    /// <summary>
    /// Whether the regular refresh should redraw the chart now. At the pace
    /// 1.5 used - every second up to a quarter of an hour, every three up to an
    /// hour, every ten beyond - rather than twice a second: on a long range a
    /// column is minutes wide and nothing visible changes in between, and on a
    /// short one a redraw every half second only made the line twitch.
    /// A range picked, a resize or a language switch still redraw at once.
    /// </summary>
    private bool ChartDue()
    {
        double every = _range <= 900 ? 1 : _range <= 3600 ? 3 : 10;
        return _host.NowMonotonic() - _chartDrawnAt >= every;
    }

    /// <summary>
    /// Draws the chart. Called on every refresh while its page is showing, so
    /// it has to be cheap: the samples are merged one per PIXEL COLUMN rather
    /// than drawn one by one, which is what stopped the Python version freezing
    /// on hundreds of thousands of drawing objects.
    /// </summary>
    private void DrawChart()
    {
        _chartDrawnAt = _host.NowMonotonic();
        Plot.Children.Clear();

        double width = Plot.ActualWidth;
        // Both measured, neither set in advance: the canvas takes the height
        // left over on its page, so a number written here would either leave a
        // gap below the chart or draw past the bottom of the card.
        double height = Plot.ActualHeight;
        if (width < AxisLeft + 40 || height < AxisBottom + 40)
            return;

        double plotWidth = width - AxisLeft;
        double plotHeight = height - AxisBottom - AxisTop;

        // The samples carry monotonic seconds; the grid has to be anchored to
        // the clock on the wall. Both are needed, so both are taken at the same
        // instant and everything is converted between them from here.
        double nowMono = _host.NowMonotonic();
        double nowWall = _host.NowWall();
        double fromMono = nowMono - _range;

        var all = _host.History.Samples();
        var inView = all.Where(s => s.At >= fromMono).ToList();
        int? threshold = (int?)_host.Settings.RssiThreshold;

        // Anything older than the left edge means the app really was watching
        // before the range began. Without it, the time before the first ever
        // reading would be shaded as lost signal.
        bool haveOlder = all.Count > 0 && all[0].At < fromMono;
        // Silences, not Silence: "Silence" is the name of a drop-down in this
        // window, and a type sharing a name with a control is the same trap
        // this project already has written down for a translation helper
        // called t().
        var silences = Silences.Bands(inView, fromMono, nowMono, threshold,
            _host.History.Downtimes(), _host.Settings.SilenceSeconds, haveOlder);

        DrawStrengthAxis(plotWidth, plotHeight);
        DrawTimeAxis(nowMono, nowWall, plotWidth, plotHeight);
        DrawSilences(silences, fromMono, nowMono, plotWidth, plotHeight);
        DrawDowntime(fromMono, nowMono, plotWidth, plotHeight);
        DrawThreshold(threshold, plotWidth, plotHeight);
        DrawSignal(inView, threshold, fromMono, nowMono, plotWidth, plotHeight);
        DrawLocks(fromMono, nowMono, plotWidth, plotHeight);
        BuildLegend(threshold);

        var summary = ChartLayout.Summarise(inView, fromMono, nowMono);
        ChartSummary.Text = summary.Count == 0
            ? Texts.Get("chart_no_signal")
            // The numbers go over as numbers: rounding them into strings here
            // would format them with the machine's culture, and the English
            // summary on Czech Windows then read "27,2/min".
            : Texts.Get("chart_summary", summary.Count, summary.PerMinute,
                summary.MedianRssi, summary.LongestSilenceSeconds);
    }

    private void DrawStrengthAxis(double plotWidth, double plotHeight)
    {
        // Every 10 dBm across the whole axis, as the shipped version draws it.
        // Three lines were enough while the chart was 240 px tall; on a chart
        // that fills the page they left the readings hanging in a third of an
        // empty rectangle, because nothing showed that the axis carries on
        // above -50 and below -90.
        for (int dbm = ChartLayout.RssiTop; dbm >= ChartLayout.RssiBottom; dbm -= 10)
        {
            double y = AxisTop + ChartLayout.Y(dbm, plotHeight);
            Plot.Children.Add(NewLine(AxisLeft, y, AxisLeft + plotWidth, y, GridBrush));
            Add(NewLabel($"{dbm}"), 4, y - 9);
        }
    }

    private void DrawTimeAxis(double nowMono, double nowWall, double plotWidth,
        double plotHeight)
    {
        // The offset at the instant drawn, not at the system's now: the two are
        // the same in a normal run, and only the first is right for a picture
        // taken at a fixed moment.
        double offset = TimeZoneInfo.Local.GetUtcOffset(
            DateTimeOffset.FromUnixTimeMilliseconds((long)(nowWall * 1000))).TotalSeconds;
        double fromWall = nowWall - _range;

        // Where the last label ended, so the next one can be left out rather
        // than printed on top of it. Which lines get a label therefore follows
        // the WIDTH, not a second step written next to the first: a narrowed
        // window would otherwise smear the times into each other.
        double labelledTo = double.NegativeInfinity;

        foreach (double at in ChartLayout.GridLines(fromWall, nowWall, offset))
        {
            double x = AxisLeft + ChartLayout.X(at, fromWall, nowWall, plotWidth);
            Plot.Children.Add(NewLine(x, AxisTop, x, AxisTop + plotHeight, GridBrush));

            // A real clock time, not "-1 min": it is what gets compared against
            // the log, and an offset from now means nothing an hour later.
            var when = DateTimeOffset.FromUnixTimeMilliseconds((long)(at * 1000)).ToLocalTime();
            var label = NewLabel(_range <= 300 ? when.ToString("H:mm:ss") : when.ToString("H:mm"));
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            double left = x - label.DesiredSize.Width / 2;
            if (left < labelledTo + LabelGap)
                continue;
            labelledTo = left + label.DesiredSize.Width;
            Add(label, left, AxisTop + plotHeight + 4);
        }
    }

    private void DrawDowntime(double fromMono, double nowMono, double plotWidth,
        double plotHeight)
    {
        foreach (var gap in _host.History.Downtimes())
        {
            if (gap.To < fromMono)
                continue;
            double x1 = AxisLeft + ChartLayout.X(gap.From, fromMono, nowMono, plotWidth);
            double x2 = AxisLeft + ChartLayout.X(gap.To, fromMono, nowMono, plotWidth);
            if (x2 - x1 < 1)
                continue;

            // Shown as a band rather than left as a flat stretch: a chart that
            // draws nothing where the app was not running claims the phone was
            // silent, which is a different thing entirely.
            var band = new Rectangle
            {
                Width = x2 - x1,
                Height = plotHeight,
                Fill = DowntimeBrush,
            };
            Add(band, x1, AxisTop);
        }
    }

    /// <summary>
    /// Shades the stretches the phone was not heard for, in two strengths: one
    /// for an ordinary gap and one for a gap the screen would have been locked
    /// for. It is what answers "why did it lock?" at a glance.
    /// </summary>
    private void DrawSilences(IReadOnlyList<SilenceBand> bands, double fromMono,
        double nowMono, double plotWidth, double plotHeight)
    {
        foreach (var band in bands)
        {
            double x1 = AxisLeft + ChartLayout.X(band.From, fromMono, nowMono, plotWidth);
            double x2 = AxisLeft + ChartLayout.X(band.To, fromMono, nowMono, plotWidth);
            if (x2 - x1 < 1)
                continue;

            Add(new Rectangle
            {
                Width = x2 - x1,
                Height = plotHeight,
                Fill = band.LongEnoughToLock ? SilenceLockBrush : SilenceBrush,
            }, x1, AxisTop);
        }
    }

    /// <summary>
    /// The sensitivity limit, when one is set. Without it the chart cannot
    /// answer the question it exists for: a reading can be drawn and still not
    /// have counted as the phone being here.
    /// </summary>
    private void DrawThreshold(int? threshold, double plotWidth, double plotHeight)
    {
        if (threshold is not int dbm)
            return;

        double y = AxisTop + ChartLayout.Y(dbm, plotHeight);
        var line = NewLine(AxisLeft, y, AxisLeft + plotWidth, y, ThresholdBrush, 1.2);
        line.StrokeDashArray = new DoubleCollection { 5, 4 };
        Plot.Children.Add(line);

        var label = NewLabel(Texts.Get("chart_threshold", dbm));
        label.Foreground = ThresholdBrush;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Add(label, AxisLeft + plotWidth - label.DesiredSize.Width - 4, y - 16);
    }

    private void DrawSignal(IReadOnlyList<Sample> inView, int? threshold,
        double fromMono, double nowMono, double plotWidth, double plotHeight)
    {
        if (inView.Count == 0)
            return;

        // Drawn the way 1.5 drew it, which David preferred (17.09.2026): in each
        // pixel column ONE THIN UPRIGHT STROKE from the weakest reading to the
        // strongest, and a thin line joining it to the column before. The first
        // 2.0 drew a thicker polyline through the strongest reading only, and
        // broke it at every empty column - that hid how much the signal spreads
        // and turned a short range into dashes.
        //
        // Whether the line is joined is decided by TIME, not by whether the
        // columns are neighbours: on a short range a column is a fraction of a
        // second, so only every few columns holds a reading. An empty column
        // does not break the line; a spacing longer than ordinary does.
        //
        // Grey where the readings do not count: with a threshold set, a weak
        // packet is drawn but did NOT count as the phone being here.
        double secondsPerPixel = _range / plotWidth;
        // Edges on the clock, not on the chart's left edge - see Columns.
        var columns = SignalHistory.Columns(inView, fromMono, secondsPerPixel, (int)plotWidth);

        var strokes = new PathGeometry();
        var weakStrokes = new PathGeometry();
        var joins = new PathGeometry();
        var weakJoins = new PathGeometry();
        (double X, double Mid, double LastAt)? previous = null;

        for (int column = 0; column < columns.Length; column++)
        {
            if (columns[column] is not Column cell)
                continue;

            double x = AxisLeft + column + 0.5;
            double top = AxisTop + ChartLayout.Y(cell.Strongest, plotHeight);
            double bottom = AxisTop + ChartLayout.Y(cell.Weakest, plotHeight);
            // A single reading still has to show - 1.5 drew a two-pixel dot.
            if (bottom - top < 1)
            {
                top -= 1;
                bottom += 1;
            }

            // The strongest reading decides: it is what counted the phone near.
            bool counts = threshold is null || cell.Strongest >= threshold;
            Segment(counts ? strokes : weakStrokes, x, top, x, bottom);

            double mid = (top + bottom) / 2;
            if (previous is var (px, pmid, pAt)
                && cell.LastAt - pAt <= Silences.Spacing(pAt, nowMono))
                Segment(counts ? joins : weakJoins, px, pmid, x, mid);
            previous = (x, mid, cell.LastAt);
        }

        // The joins underneath, the strokes on top - the same order as 1.5.
        AddPath(joins, SignalJoinBrush);
        AddPath(weakJoins, WeakJoinBrush);
        AddPath(strokes, SignalBrush);
        AddPath(weakStrokes, WeakBrush);
    }

    /// <summary>
    /// One path per colour rather than a shape per stroke: a day's range is
    /// well over a thousand strokes, redrawn every few seconds.
    /// </summary>
    private static void Segment(PathGeometry into, double x1, double y1, double x2, double y2)
    {
        var figure = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false };
        figure.Segments.Add(new LineSegment { Point = new Point(x2, y2) });
        into.Figures.Add(figure);
    }

    private void AddPath(PathGeometry geometry, Brush brush)
    {
        if (geometry.Figures.Count == 0)
            return;
        Plot.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = brush,
            StrokeThickness = 1,
        });
    }

    private void DrawLocks(double fromMono, double nowMono, double plotWidth,
        double plotHeight)
    {
        var brush = LockBrush;
        foreach (double at in _host.History.Locks())
        {
            if (at < fromMono)
                continue;
            double x = AxisLeft + ChartLayout.X(at, fromMono, nowMono, plotWidth);
            Plot.Children.Add(NewLine(x, AxisTop, x, AxisTop + plotHeight, brush, 1.5));
        }
    }

    // -------------------------------------------------------------- colours

    // Every colour the chart uses, named once. The legend is drawn from these
    // same brushes rather than from copies of the values: a legend that can
    // disagree with the picture is worse than none, and two lists of colours
    // drift apart the first time one of them is adjusted.
    // The signal colours are 1.5's, to the digit.
    private static readonly Brush SignalBrush =
        new SolidColorBrush(Color.FromArgb(255, 0x7C, 0xC0, 0xFF));
    private static readonly Brush SignalJoinBrush =
        new SolidColorBrush(Color.FromArgb(255, 0x4E, 0xA3, 0xFF));
    private static readonly Brush WeakBrush =
        new SolidColorBrush(Color.FromArgb(255, 0x79, 0x82, 0x8F));
    private static readonly Brush WeakJoinBrush =
        new SolidColorBrush(Color.FromArgb(255, 0x5C, 0x64, 0x70));
    private static readonly Brush SilenceBrush =
        new SolidColorBrush(Color.FromArgb(46, 0xE8, 0xA3, 0x3D));
    private static readonly Brush SilenceLockBrush =
        new SolidColorBrush(Color.FromArgb(54, 0xE0, 0x6C, 0x75));
    private static readonly Brush DowntimeBrush =
        new SolidColorBrush(Color.FromArgb(40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LockBrush =
        new SolidColorBrush(Color.FromArgb(200, 0xE0, 0x6C, 0x75));
    private static readonly Brush ThresholdBrush =
        new SolidColorBrush(Color.FromArgb(255, 0xE8, 0xA3, 0x3D));

    /// <summary>
    /// The key to the picture: what each colour means. Built in code, because
    /// the swatches have to BE the brushes above - a legend written in XAML
    /// would be a second set of colours to keep in step.
    /// </summary>
    private void BuildLegend(int? threshold)
    {
        ChartLegend.Children.Clear();
        ChartLegend.ColumnDefinitions.Clear();
        ChartLegend.RowDefinitions.Clear();

        var items = new List<(string Key, Brush Brush, string Shape)>
        {
            ("leg_signal", SignalBrush, "line"),
            ("leg_gap", SilenceBrush, "band"),
            ("leg_gap_lock", SilenceLockBrush, "band"),
            ("leg_downtime", DowntimeBrush, "band"),
            ("leg_locked", LockBrush, "upright"),
        };
        // Only when there is one: without a threshold nothing is ever drawn
        // weak, and a key to a colour that is not in the picture sends the
        // reader looking for it.
        if (threshold is not null)
        {
            items.Insert(1, ("leg_weak", WeakBrush, "line"));
            items.Add(("leg_threshold", ThresholdBrush, "dashed"));
        }

        const int columns = 2;
        for (int c = 0; c < columns; c++)
            ChartLegend.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r * columns < items.Count; r++)
            ChartLegend.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < items.Count; i++)
        {
            var (key, brush, shape) = items[i];
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 2, 12, 2),
            };
            row.Children.Add(Swatch(shape, brush));
            row.Children.Add(new TextBlock
            {
                Text = Texts.Get(key),
                FontSize = 12,
                Opacity = 0.95,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });

            Grid.SetColumn(row, i % columns);
            Grid.SetRow(row, i / columns);
            ChartLegend.Children.Add(row);
        }
    }

    private static FrameworkElement Swatch(string shape, Brush brush) => shape switch
    {
        // The fill is the chart's own see-through brush, so the swatch looks
        // like the band. The outline is the same colour at nearly full
        // strength: without it the faint fills vanished into the background
        // and could not be told apart (David, 17.09.2026).
        "band" => new Rectangle
        {
            Width = 22,
            Height = 14,
            Fill = brush,
            Stroke = Outline(brush),
            StrokeThickness = 1.2,
            VerticalAlignment = VerticalAlignment.Center,
        },
        "upright" => new Rectangle
        {
            Width = 2,
            Height = 14,
            Fill = brush,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        },
        "dashed" => new Line
        {
            X1 = 0,
            Y1 = 6,
            X2 = 22,
            Y2 = 6,
            Stroke = brush,
            StrokeThickness = 1.6,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            VerticalAlignment = VerticalAlignment.Center,
        },
        _ => new Rectangle
        {
            Width = 22,
            Height = 2,
            Fill = brush,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static Brush Outline(Brush fill) => fill is SolidColorBrush solid
        ? new SolidColorBrush(Color.FromArgb(210, solid.Color.R, solid.Color.G, solid.Color.B))
        : fill;

    // ------------------------------------------------------------- helpers

    private static Brush GridBrush =>
        (Brush)Application.Current.Resources["ControlStrongStrokeColorDisabledBrush"];

    private static Line NewLine(double x1, double y1, double x2, double y2,
        Brush stroke, double thickness = 1) => new()
    {
        X1 = x1,
        Y1 = y1,
        X2 = x2,
        Y2 = y2,
        Stroke = stroke,
        StrokeThickness = thickness,
    };

    private static TextBlock NewLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.95,
    };

    private void Add(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        Plot.Children.Add(element);
    }
}
