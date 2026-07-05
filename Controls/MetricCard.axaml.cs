using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace InsideOS.Controls;

/// <summary>
/// Dashboard card with an icon, title, value, explanatory subtitle and an
/// optional animated usage bar that changes color at warning thresholds.
/// </summary>
public partial class MetricCard : UserControl
{
    private static readonly IBrush BarNormal = new SolidColorBrush(Color.Parse("#4D9FFF"));
    private static readonly IBrush BarWarn = new SolidColorBrush(Color.Parse("#E8B44C"));
    private static readonly IBrush BarHot = new SolidColorBrush(Color.Parse("#E85C5C"));
    private static readonly IBrush BarGood = new SolidColorBrush(Color.Parse("#3FBF7F"));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<MetricCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<MetricCard, string?>(nameof(Subtitle));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<MetricCard, string?>(nameof(Value), "—");

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<MetricCard, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<bool> ShowBarProperty =
        AvaloniaProperty.Register<MetricCard, bool>(nameof(ShowBar));

    public static readonly StyledProperty<double> BarValueProperty =
        AvaloniaProperty.Register<MetricCard, double>(nameof(BarValue));

    /// <summary>When true, low bar values are bad (battery) instead of high ones (load).</summary>
    public static readonly StyledProperty<bool> InvertBarSeverityProperty =
        AvaloniaProperty.Register<MetricCard, bool>(nameof(InvertBarSeverity));

    public static readonly StyledProperty<double> ValueFontSizeProperty =
        AvaloniaProperty.Register<MetricCard, double>(nameof(ValueFontSize), 24.0);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool ShowBar
    {
        get => GetValue(ShowBarProperty);
        set => SetValue(ShowBarProperty, value);
    }

    public double BarValue
    {
        get => GetValue(BarValueProperty);
        set => SetValue(BarValueProperty, value);
    }

    public bool InvertBarSeverity
    {
        get => GetValue(InvertBarSeverityProperty);
        set => SetValue(InvertBarSeverityProperty, value);
    }

    public double ValueFontSize
    {
        get => GetValue(ValueFontSizeProperty);
        set => SetValue(ValueFontSizeProperty, value);
    }

    public MetricCard()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (TitleText is null)
            return; // XAML content not loaded yet.

        if (change.Property == TitleProperty)
            TitleText.Text = Title;
        else if (change.Property == SubtitleProperty)
            SubtitleText.Text = Subtitle;
        else if (change.Property == ValueProperty)
            ValueText.Text = Value;
        else if (change.Property == IconProperty)
            IconPath.Data = Icon;
        else if (change.Property == ValueFontSizeProperty)
            ValueText.FontSize = ValueFontSize;
        else if (change.Property == ShowBarProperty)
            Bar.IsVisible = ShowBar;
        else if (change.Property == BarValueProperty || change.Property == InvertBarSeverityProperty)
            UpdateBar();
    }

    private void UpdateBar()
    {
        Bar.Value = BarValue;
        Bar.Foreground = InvertBarSeverity
            ? BarValue <= 20 ? BarHot : BarValue <= 40 ? BarWarn : BarGood
            : BarValue >= 85 ? BarHot : BarValue >= 60 ? BarWarn : BarNormal;
    }
}
