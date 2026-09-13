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

    /// <summary>
    /// Draws the chart. Called on every refresh while its page is showing, so
    /// it has to be cheap: the samples are merged one per PIXEL COLUMN rather
    /// than drawn one by one, which is what stopped the Python version freezing
    /// on hundreds of thousands of drawing objects.
    /// </summary>
    private void DrawChart()
    {
        Plot.Children.Clear();

        double width = Plot.ActualWidth;
        double height = Plot.Height;
        if (width < AxisLeft + 40 || height < AxisBottom + 40)
            return;

        double plotWidth = width - AxisLeft;
        double plotHeight = height - AxisBottom - AxisTop;

        // The samples carry monotonic seconds; the grid has to be anchored to
        // the clock on the wall. Both are needed, so both are taken at the same
        // instant and everything is converted between them from here.
        double nowMono = PhoneWatch.MonotonicSeconds();
        double nowWall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        double fromMono = nowMono - _range;

        var inView = _host.History.Samples().Where(s => s.At >= fromMono).ToList();

        DrawStrengthAxis(plotWidth, plotHeight);
        DrawTimeAxis(nowMono, nowWall, plotWidth, plotHeight);
        DrawDowntime(fromMono, nowMono, plotWidth, plotHeight);
        DrawSignal(inView, fromMono, nowMono, plotWidth, plotHeight);
        DrawLocks(fromMono, nowMono, plotWidth, plotHeight);

        var summary = ChartLayout.Summarise(inView, fromMono, nowMono);
        ChartSummary.Text = summary.Count == 0
            ? Texts.Get("chart_no_signal")
            : Texts.Get("chart_summary", summary.Count, $"{summary.PerMinute:F1}",
                summary.MedianRssi, $"{summary.LongestSilenceSeconds:F0}");
    }

    private void DrawStrengthAxis(double plotWidth, double plotHeight)
    {
        foreach (int dbm in new[] { -50, -70, -90 })
        {
            double y = AxisTop + ChartLayout.Y(dbm, plotHeight);
            Plot.Children.Add(NewLine(AxisLeft, y, AxisLeft + plotWidth, y, GridBrush));
            Add(NewLabel($"{dbm}"), 4, y - 9);
        }
    }

    private void DrawTimeAxis(double nowMono, double nowWall, double plotWidth,
        double plotHeight)
    {
        double offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now).TotalSeconds;
        double fromWall = nowWall - _range;

        foreach (double at in ChartLayout.GridLines(fromWall, nowWall, offset))
        {
            double x = AxisLeft + ChartLayout.X(at, fromWall, nowWall, plotWidth);
            Plot.Children.Add(NewLine(x, AxisTop, x, AxisTop + plotHeight, GridBrush));

            // A real clock time, not "-1 min": it is what gets compared against
            // the log, and an offset from now means nothing an hour later.
            var when = DateTimeOffset.FromUnixTimeMilliseconds((long)(at * 1000)).ToLocalTime();
            var label = NewLabel(_range <= 300 ? when.ToString("H:mm:ss") : when.ToString("H:mm"));
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Add(label, x - label.DesiredSize.Width / 2, AxisTop + plotHeight + 4);
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
                Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            };
            Add(band, x1, AxisTop);
        }
    }

    private void DrawSignal(IReadOnlyList<Sample> inView, double fromMono, double nowMono,
        double plotWidth, double plotHeight)
    {
        if (inView.Count == 0)
            return;

        // One point per pixel column. Where a column holds no reading the line
        // is broken rather than bridged - a bridge over a gap would draw a
        // signal that was never there, and the gaps are the whole point.
        var runs = new List<PointCollection>();
        var current = new PointCollection();
        double secondsPerPixel = _range / plotWidth;

        for (int column = 0; column < (int)plotWidth; column++)
        {
            double from = fromMono + column * secondsPerPixel;
            var merged = SignalHistory.Merge(inView, from, from + secondsPerPixel);
            if (merged is not Column cell)
            {
                if (current.Count > 1)
                    runs.Add(current);
                current = new PointCollection();
                continue;
            }
            // The strongest reading of the column: the axis is labelled "higher
            // is better", and the strongest is what decided whether the phone
            // counted as near.
            current.Add(new Point(AxisLeft + column,
                AxisTop + ChartLayout.Y(cell.Strongest, plotHeight)));
        }
        if (current.Count > 1)
            runs.Add(current);

        foreach (var run in runs)
        {
            Plot.Children.Add(new Polyline
            {
                Points = run,
                Stroke = new SolidColorBrush(Color.FromArgb(255, 0x57, 0xD3, 0x8C)),
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
    }

    private void DrawLocks(double fromMono, double nowMono, double plotWidth,
        double plotHeight)
    {
        var brush = new SolidColorBrush(Color.FromArgb(200, 0xE0, 0x6C, 0x75));
        foreach (double at in _host.History.Locks())
        {
            if (at < fromMono)
                continue;
            double x = AxisLeft + ChartLayout.X(at, fromMono, nowMono, plotWidth);
            Plot.Children.Add(NewLine(x, AxisTop, x, AxisTop + plotHeight, brush, 1.5));
        }
    }

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
