using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using InsideOS.Services.ActionFlow;

namespace InsideOS.Controls;

/// <summary>
/// One node of the Action Flow diagram: icon, title, live value, an honesty
/// badge (Measured / Calculated / Estimated / Unavailable), an optional meta
/// line, a tooltip, and two glow layers — a breathing pulse whose speed
/// follows the node's activity, plus a halo that brightens as activity rises.
/// </summary>
public partial class FlowNode : UserControl
{
    private sealed record QualityVisual(string Label, IBrush Background, IBrush Border, IBrush Foreground);

    private static readonly QualityVisual[] QualityVisuals =
    [
        new("MEASURED", Brush("#1F3FBF7F"), Brush("#383FBF7F"), Brush("#7BDCA9")),
        new("CALCULATED", Brush("#1F4D9FFF"), Brush("#384D9FFF"), Brush("#8FBFFF")),
        new("ESTIMATED", Brush("#1FE8B44C"), Brush("#38E8B44C"), Brush("#F0CC82")),
        new("UNAVAILABLE", Brush("#12FFFFFF"), Brush("#1FFFFFFF"), Brush("#808A9C")),
    ];

    private static readonly TransformOperations ValueDip = TransformOperations.Parse("translateY(4px)");
    private static readonly TransformOperations ValueRest = TransformOperations.Parse("translateY(0px)");

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<FlowNode, string?>(nameof(Title));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<FlowNode, string?>(nameof(Value), "—");

    public static readonly StyledProperty<string?> MetaProperty =
        AvaloniaProperty.Register<FlowNode, string?>(nameof(Meta));

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<FlowNode, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> ExplanationProperty =
        AvaloniaProperty.Register<FlowNode, string?>(nameof(Explanation));

    public static readonly StyledProperty<MetricQuality> QualityProperty =
        AvaloniaProperty.Register<FlowNode, MetricQuality>(nameof(Quality));

    public static readonly StyledProperty<bool> ShowQualityProperty =
        AvaloniaProperty.Register<FlowNode, bool>(nameof(ShowQuality), true);

    /// <summary>0..1 — drives the breathing speed and the activity halo brightness.</summary>
    public static readonly StyledProperty<double> ActivityLevelProperty =
        AvaloniaProperty.Register<FlowNode, double>(nameof(ActivityLevel));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Meta { get => GetValue(MetaProperty); set => SetValue(MetaProperty, value); }
    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Explanation { get => GetValue(ExplanationProperty); set => SetValue(ExplanationProperty, value); }
    public MetricQuality Quality { get => GetValue(QualityProperty); set => SetValue(QualityProperty, value); }
    public bool ShowQuality { get => GetValue(ShowQualityProperty); set => SetValue(ShowQualityProperty, value); }
    public double ActivityLevel { get => GetValue(ActivityLevelProperty); set => SetValue(ActivityLevelProperty, value); }

    private string _pulseClass = "pulseCalm";

    public FlowNode()
    {
        InitializeComponent();
        GlowLayer.Classes.Add(_pulseClass);
        UpdateBadge();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (TitleText is null)
            return; // XAML content not loaded yet.

        if (change.Property == TitleProperty)
        {
            TitleText.Text = Title;
        }
        else if (change.Property == ValueProperty)
        {
            ValueText.Text = Value;
            PulseValue();
        }
        else if (change.Property == MetaProperty)
        {
            MetaText.Text = Meta;
            MetaText.IsVisible = !string.IsNullOrEmpty(Meta);
        }
        else if (change.Property == IconProperty)
        {
            IconPath.Data = Icon;
        }
        else if (change.Property == ExplanationProperty)
        {
            ToolTip.SetTip(this, string.IsNullOrEmpty(Explanation)
                ? null
                : new TextBlock { Text = Explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 });
        }
        else if (change.Property == QualityProperty || change.Property == ShowQualityProperty)
        {
            UpdateBadge();
        }
        else if (change.Property == ActivityLevelProperty)
        {
            UpdateActivity();
        }
    }

    private void UpdateBadge()
    {
        if (BadgePill is null)
            return;
        BadgePill.IsVisible = ShowQuality;
        var visual = QualityVisuals[Math.Clamp((int)Quality, 0, QualityVisuals.Length - 1)];
        BadgeText.Text = visual.Label;
        BadgeText.Foreground = visual.Foreground;
        BadgePill.Background = visual.Background;
        BadgePill.BorderBrush = visual.Border;
    }

    private void UpdateActivity()
    {
        double level = Math.Clamp(ActivityLevel, 0, 1);

        // Halo brightens with activity (transition in XAML keeps it smooth).
        ActivityGlow.Opacity = level * 0.30;

        // Breathing speed bucket — swap the class only when the bucket actually
        // changes, so the running animation isn't restarted every second.
        string pulse = level < 0.04 ? "pulseCalm" : level < 0.40 ? "pulseActive" : "pulseBusy";
        if (pulse == _pulseClass)
            return;
        GlowLayer.Classes.Remove(_pulseClass);
        GlowLayer.Classes.Add(pulse);
        _pulseClass = pulse;
    }

    private static readonly IBrush SpotlightBorderBrush = new SolidColorBrush(Color.Parse("#8A4D9FFF"));
    private static readonly TransformOperations SpotlightScale = TransformOperations.Parse("scale(1.05)");

    /// <summary>Guided-tour highlight: accent ring, border and a gentle lift.</summary>
    public void SetSpotlight(bool on)
    {
        SpotlightRing.Opacity = on ? 1 : 0;
        if (on)
        {
            CardLayer.BorderBrush = SpotlightBorderBrush;
            CardLayer.RenderTransform = SpotlightScale;
        }
        else
        {
            CardLayer.ClearValue(Border.BorderBrushProperty);
            CardLayer.ClearValue(Border.RenderTransformProperty);
        }
    }

    /// <summary>Soft dip-and-rise of the value text so per-second updates feel alive.</summary>
    private void PulseValue()
    {
        ValueText.Opacity = 0.25;
        ValueText.RenderTransform = ValueDip;
        DispatcherTimer.RunOnce(() =>
        {
            ValueText.Opacity = 1;
            ValueText.RenderTransform = ValueRest;
        }, TimeSpan.FromMilliseconds(70));
    }
}
