using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using InsideOS.Services.History;

namespace InsideOS.Controls;

/// <summary>A clickable event marker on a chart (usually a Timeline story).</summary>
public sealed record ChartMarker(DateTime Time, Color Color, string Label, object Payload);

/// <summary>
/// Lightweight line chart rendered directly with DrawingContext — no
/// per-point visuals, no layout churn, one InvalidateVisual per second.
/// Data lives in preallocated arrays; SetData copies into them. Hovering
/// shows timestamp + exact value (+ nearby event), markers are clickable.
/// </summary>
public sealed class HistoryChart : Control
{
    private readonly DateTime[] _times = new DateTime[MetricHistoryService.Capacity];
    private readonly double[] _values = new double[MetricHistoryService.Capacity];
    private int _count;
    private double _scaleMin;
    private double _scaleMax = 1;
    private TimeSpan _window = TimeSpan.FromMinutes(5);
    private DateTime _end = DateTime.Now;
    private readonly List<ChartMarker> _markers = new();
    private int _hoverIndex = -1;
    private Point _hoverPoint;

    private Color _lineColor = Color.Parse("#4D9FFF");
    private IPen _linePen = null!;
    private IBrush _fillBrush = null!;
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#12FFFFFF")), 1);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.Parse("#33FFFFFF")), 1);
    private static readonly IBrush TipBg = new SolidColorBrush(Color.Parse("#F01A1E28"));
    private static readonly IPen TipBorder = new Pen(new SolidColorBrush(Color.Parse("#2EFFFFFF")), 1);
    private static readonly IBrush TipText = new SolidColorBrush(Color.Parse("#EDEFF4"));
    private static readonly IBrush TipMuted = new SolidColorBrush(Color.Parse("#9AA3B4"));
    private static readonly IBrush AxisText = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly Typeface Face = new("Inter");

    public Func<double, string> Formatter { get; set; } = v => v.ToString("0.#", CultureInfo.InvariantCulture);

    public event Action<object>? MarkerClicked;

    public HistoryChart()
    {
        LineColor = _lineColor;
        ClipToBounds = true;
    }

    public Color LineColor
    {
        get => _lineColor;
        set
        {
            _lineColor = value;
            _linePen = new Pen(new SolidColorBrush(value), 1.6, lineJoin: PenLineJoin.Round);
            _fillBrush = new SolidColorBrush(value, 0.10);
        }
    }

    public void SetData(DateTime[] times, double[] values, int count, TimeSpan window,
        double? forcedMin = null, double? forcedMax = null, double minSpanFloor = 1)
    {
        count = Math.Min(count, _times.Length);
        Array.Copy(times, _times, count);
        Array.Copy(values, _values, count);
        _count = count;
        _window = window;
        _end = DateTime.Now;

        double min = forcedMin ?? double.MaxValue;
        double max = forcedMax ?? double.MinValue;
        if (forcedMin is null || forcedMax is null)
        {
            for (int i = 0; i < count; i++)
            {
                if (forcedMin is null && _values[i] < min) min = _values[i];
                if (forcedMax is null && _values[i] > max) max = _values[i];
            }
            if (count == 0) { min = 0; max = minSpanFloor; }
            if (forcedMin is null)
                min = Math.Min(min, 0); // ground the chart at zero for honesty
            if (forcedMax is null)
                max = Math.Max(max * 1.12, min + minSpanFloor);
        }
        _scaleMin = min;
        _scaleMax = max;
        if (_hoverIndex >= _count)
            _hoverIndex = -1;
        InvalidateVisual();
    }

    public void SetMarkers(IReadOnlyList<ChartMarker> markers)
    {
        _markers.Clear();
        _markers.AddRange(markers);
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        double w = bounds.Width, h = bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, bounds); // keeps the whole area hit-testable

        for (int g = 1; g <= 3; g++)
            ctx.DrawLine(GridPen, new Point(0, h * g / 4), new Point(w, h * g / 4));

        if (_count < 2)
        {
            var waiting = Text("Collecting data…", 10.5, TipMuted);
            ctx.DrawText(waiting, new Point((w - waiting.Width) / 2, (h - waiting.Height) / 2));
            return;
        }

        // Axis time labels (start / end of the visible window).
        var startLabel = Text((_end - _window).ToString("HH:mm"), 9, AxisText);
        var endLabel = Text(_end.ToString("HH:mm"), 9, AxisText);
        ctx.DrawText(startLabel, new Point(2, h - startLabel.Height - 1));
        ctx.DrawText(endLabel, new Point(w - endLabel.Width - 2, h - endLabel.Height - 1));

        // Polyline + translucent area fill.
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        double firstX = 0, lastX = 0;
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            for (int i = 0; i < _count; i++)
            {
                double x = X(_times[i], w);
                double y = Y(_values[i], h);
                if (i == 0)
                {
                    firstX = x;
                    lc.BeginFigure(new Point(x, y), false);
                    ac.BeginFigure(new Point(x, h), true);
                    ac.LineTo(new Point(x, y));
                }
                else
                {
                    lc.LineTo(new Point(x, y));
                    ac.LineTo(new Point(x, y));
                }
                lastX = x;
            }
            ac.LineTo(new Point(lastX, h));
            ac.EndFigure(true);
            lc.EndFigure(false);
        }
        ctx.DrawGeometry(_fillBrush, null, area);
        ctx.DrawGeometry(null, _linePen, line);
        _ = firstX;

        // Timeline story markers along the bottom edge.
        foreach (var marker in _markers)
        {
            if (marker.Time < _end - _window || marker.Time > _end)
                continue;
            double x = X(marker.Time, w);
            ctx.DrawEllipse(new SolidColorBrush(marker.Color), null, new Point(x, h - 7), 3.5, 3.5);
        }

        // Hover: hairline, dot, and an info box with time + value (+ event).
        if (_hoverIndex >= 0 && _hoverIndex < _count)
        {
            double x = X(_times[_hoverIndex], w);
            double y = Y(_values[_hoverIndex], h);
            ctx.DrawLine(HoverPen, new Point(x, 0), new Point(x, h));
            ctx.DrawEllipse(new SolidColorBrush(_lineColor), null, new Point(x, y), 3, 3);

            var timeText = Text(_times[_hoverIndex].ToString("HH:mm:ss"), 10, TipMuted);
            var valueText = Text(Formatter(_values[_hoverIndex]), 12, TipText);
            FormattedText? eventText = null;
            if (NearestMarker(x, w) is { } near)
                eventText = Text(near.Label, 9.5, new SolidColorBrush(near.Color));

            double tipW = Math.Max(Math.Max(timeText.Width, valueText.Width), eventText?.Width ?? 0) + 18;
            double tipH = timeText.Height + valueText.Height + (eventText?.Height + 3 ?? 0) + 12;
            double tx = x + 10 + tipW > w ? x - 10 - tipW : x + 10;
            var rect = new Rect(tx, 6, tipW, tipH);
            ctx.DrawRectangle(TipBg, TipBorder, new RoundedRect(rect, 6));
            ctx.DrawText(timeText, new Point(tx + 9, 11));
            ctx.DrawText(valueText, new Point(tx + 9, 11 + timeText.Height + 1));
            if (eventText is not null)
                ctx.DrawText(eventText, new Point(tx + 9, 11 + timeText.Height + valueText.Height + 4));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverPoint = e.GetPosition(this);
        int best = -1;
        double bestDist = double.MaxValue;
        double w = Bounds.Width;
        for (int i = 0; i < _count; i++)
        {
            double d = Math.Abs(X(_times[i], w) - _hoverPoint.X);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        if (best != _hoverIndex)
        {
            _hoverIndex = best;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverIndex = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (NearestMarker(p.X, Bounds.Width) is { } marker && p.Y > Bounds.Height - 16)
        {
            e.Handled = true;
            MarkerClicked?.Invoke(marker.Payload);
        }
    }

    private ChartMarker? NearestMarker(double x, double width)
    {
        ChartMarker? best = null;
        double bestDist = 9; // px hit radius
        foreach (var marker in _markers)
        {
            if (marker.Time < _end - _window || marker.Time > _end)
                continue;
            double d = Math.Abs(X(marker.Time, width) - x);
            if (d < bestDist)
            {
                bestDist = d;
                best = marker;
            }
        }
        return best;
    }

    private double X(DateTime t, double width) =>
        Math.Clamp(width * (1 - (_end - t).TotalSeconds / _window.TotalSeconds), 0, width);

    private double Y(double v, double height)
    {
        double span = Math.Max(_scaleMax - _scaleMin, 1e-9);
        return height - 6 - (Math.Clamp(v, _scaleMin, _scaleMax) - _scaleMin) / span * (height - 14);
    }

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);
}
