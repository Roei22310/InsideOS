using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using InsideOS.Controls;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Explanations;
using InsideOS.Services.Narration;
using InsideOS.Services.Learning;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.ViewModels;

namespace InsideOS.Pages;

public partial class ActionFlowPage : UserControl
{
    private const string ProcessExplanation =
        "A process is a running program with its own protected memory space. " +
        "The operating system schedules it on and off the CPU thousands of times per second.";

    private const string CpuExplanation =
        "The share of processor time the OS scheduler is granting this process. " +
        "100% equals one fully busy core, so values above 100% mean several cores at once.";

    private const string CpuCalculatedNote =
        "\n\nShown as Calculated: for processes owned by other users, macOS provides " +
        "a smoothed kernel average instead of an exact per-second sample.";

    private const string MemoryExplanation =
        "The physical RAM the OS currently keeps resident for this process — " +
        "its working data, code and buffers.";

    private const string DiskExplanation =
        "How fast this process is reading from and writing to storage right now, " +
        "from the kernel's per-process I/O accounting.";

    private const string DiskUnavailableNote =
        "\n\nUnavailable here: macOS only reveals disk I/O counters for processes " +
        "you own — reading them for system processes requires root.";

    private const string NetworkExplanation =
        "Bytes flowing through this process's network sockets per second, " +
        "sampled from the kernel's per-process network statistics.";

    private const string NetworkUnavailableNote =
        "\n\nUnavailable: per-process network statistics could not be sampled on this system.";

    private enum EdgeTier { None, Idle, Low, Medium, High }

    private static readonly string[] EdgeTierClasses = ["fIdle", "fLow", "fMed", "fHigh"];
    private static readonly TransformOperations DiagramRest = TransformOperations.Parse("scale(1)");
    private static readonly TransformOperations DiagramSmall = TransformOperations.Parse("scale(0.98)");

    private static readonly IBrush ExplanationAccent = new SolidColorBrush(Color.Parse("#4D9FFF"));
    private static readonly IBrush ExplanationMuted = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush ExplanationAlert = new SolidColorBrush(Color.Parse("#E85C5C"));

    private static readonly TransformOperations PanelHidden = TransformOperations.Parse("translateX(380px)");
    private static readonly TransformOperations PanelShown = TransformOperations.Parse("translateX(0px)");
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly IBrush ChipBg = new SolidColorBrush(Color.Parse("#12FFFFFF"));
    private static readonly IBrush ChipBorder = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IBrush ChipFg = new SolidColorBrush(Color.Parse("#9AA3B4"));

    private readonly ulong _totalMemoryBytes;
    private readonly LearnModeState _learnMode;
    private readonly ILearnContentService _learnContent;
    private int _displayedPid = -1;
    private int _applyVersion;
    private string? _displayedExplanation;
    private ProcessFlowSnapshot? _lastSnapshot;
    private LearnTopicId? _openTopic;

    public ActionFlowPage(
        ProcessFlowMonitor flow,
        ExplanationFeed explanations,
        LearnModeState learnMode,
        ILearnContentService learnContent,
        ulong totalMemoryBytes)
    {
        InitializeComponent();
        _totalMemoryBytes = totalMemoryBytes;
        _learnMode = learnMode;
        _learnContent = learnContent;

        ProcessNode.Explanation = ProcessExplanation;
        CpuNode.Explanation = CpuExplanation;
        MemoryNode.Explanation = MemoryExplanation;
        DiskNode.Explanation = DiskExplanation;
        NetworkNode.Explanation = NetworkExplanation;

        RegisterLearnNode(ProcessNode, LearnTopicId.Process);
        RegisterLearnNode(CpuNode, LearnTopicId.Cpu);
        RegisterLearnNode(MemoryNode, LearnTopicId.Memory);
        RegisterLearnNode(DiskNode, LearnTopicId.Disk);
        RegisterLearnNode(NetworkNode, LearnTopicId.Network);
        UpdateLearnAffordances(learnMode.IsLearnMode);

        flow.FlowUpdated += snapshot => Dispatcher.UIThread.Post(() => Apply(snapshot));
        flow.Selection.Changed += selected => Dispatcher.UIThread.Post(() => OnSelectionChanged(selected));
        explanations.ExplanationUpdated += explanation =>
            Dispatcher.UIThread.Post(() => ApplyExplanation(explanation));
        learnMode.Changed += learn => Dispatcher.UIThread.Post(() => UpdateLearnAffordances(learn));
    }

    // ---- Learn Mode ----

    private static readonly IBrush HighlightBorderBrush = new SolidColorBrush(Color.Parse("#884D9FFF"));
    private static readonly IBrush HighlightBackground = new SolidColorBrush(Color.Parse("#144D9FFF"));

    /// <summary>Opens the Learn panel for a topic (used by the first-run experience).</summary>
    public void OpenLearnTopic(LearnTopicId topic) => OpenLearnPanel(topic);

    /// <summary>Guided-tour spotlight: highlights one node (or none) at a time.</summary>
    public void SpotlightNode(LearnTopicId? topic)
    {
        ProcessNode.SetSpotlight(topic == LearnTopicId.Process);
        CpuNode.SetSpotlight(topic == LearnTopicId.Cpu);
        MemoryNode.SetSpotlight(topic == LearnTopicId.Memory);
        DiskNode.SetSpotlight(topic == LearnTopicId.Disk);
        NetworkNode.SetSpotlight(topic == LearnTopicId.Network);
    }

    /// <summary>Briefly calls attention to the explanation card (first-run experience).</summary>
    public void HighlightExplanationCard()
    {
        ExplanationCard.BorderBrush = HighlightBorderBrush;
        ExplanationCard.Background = HighlightBackground;
        DispatcherTimer.RunOnce(() =>
        {
            ExplanationCard.ClearValue(Border.BorderBrushProperty);
            ExplanationCard.ClearValue(Border.BackgroundProperty);
        }, TimeSpan.FromSeconds(4));
    }

    private void RegisterLearnNode(FlowNode node, LearnTopicId topic) =>
        node.PointerPressed += (_, e) =>
        {
            if (!_learnMode.IsLearnMode)
                return;
            e.Handled = true;
            OpenLearnPanel(topic);
        };

    private void UpdateLearnAffordances(bool learn)
    {
        var cursor = learn ? HandCursor : Cursor.Default;
        ProcessNode.Cursor = cursor;
        CpuNode.Cursor = cursor;
        MemoryNode.Cursor = cursor;
        DiskNode.Cursor = cursor;
        NetworkNode.Cursor = cursor;
        LearnHint.IsVisible = learn && FlowView.IsVisible;
        if (!learn)
            CloseLearnPanel();
    }

    private void OpenLearnPanel(LearnTopicId topic)
    {
        if (_openTopic == topic)
            return;
        bool wasOpen = _openTopic is not null;
        _openTopic = topic;

        if (wasOpen)
        {
            // Fade the content out, swap it, fade back in — the panel itself stays.
            LearnContentHost.Opacity = 0;
            DispatcherTimer.RunOnce(() =>
            {
                if (_openTopic is { } current)
                {
                    FillLearnPanel(current);
                    LearnContentHost.Opacity = 1;
                }
            }, TimeSpan.FromMilliseconds(160));
        }
        else
        {
            FillLearnPanel(topic);
            LearnContentHost.Opacity = 1;
            LearnPanel.IsHitTestVisible = true;
            LearnPanel.Opacity = 1;
            LearnPanel.RenderTransform = PanelShown;
        }
    }

    private void CloseLearnPanel()
    {
        _openTopic = null;
        LearnPanel.IsHitTestVisible = false;
        LearnPanel.Opacity = 0;
        LearnPanel.RenderTransform = PanelHidden;
    }

    private void OnLearnClosePressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        CloseLearnPanel();
    }

    private void FillLearnPanel(LearnTopicId topic)
    {
        var content = _learnContent.GetContent(topic);
        LearnTitle.Text = content.Title;
        LearnWhatItDoes.Text = content.WhatItDoes;
        LearnWhatHappening.Text = string.IsNullOrEmpty(_displayedExplanation)
            ? "Still watching — a live explanation appears after a few seconds."
            : _displayedExplanation;

        if (_lastSnapshot is { } snapshot)
        {
            LearnWhy.Text = _learnContent.DescribeWhy(topic, snapshot);
            LearnWorry.Text = _learnContent.DescribeWorry(topic, snapshot);
        }
        else
        {
            LearnWhy.Text = "Reading live data…";
            LearnWorry.Text = "Reading live data…";
        }

        LearnMoreWrap.Children.Clear();
        foreach (var concept in content.RelatedConcepts)
        {
            LearnMoreWrap.Children.Add(new Border
            {
                Background = ChipBg,
                BorderBrush = ChipBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(99),
                Padding = new Thickness(10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = concept, FontSize = 11, Foreground = ChipFg },
            });
        }
    }

    /// <summary>Keeps the open panel's live sections in sync with the latest snapshot.</summary>
    private void RefreshLearnPanel(ProcessFlowSnapshot snapshot)
    {
        if (_openTopic is not { } topic)
            return;
        string why = _learnContent.DescribeWhy(topic, snapshot);
        if (LearnWhy.Text != why)
            LearnWhy.Text = why;
        string worry = _learnContent.DescribeWorry(topic, snapshot);
        if (LearnWorry.Text != worry)
            LearnWorry.Text = worry;
    }

    /// <summary>Fades the explanation text out, swaps it, and fades back in.</summary>
    private void ApplyExplanation(Explanation explanation)
    {
        if (explanation.Kind == ActivityTone.Terminated)
            return; // the exited empty-state already tells this story

        ExplanationIcon.Stroke = explanation.Kind switch
        {
            ActivityTone.Idle => ExplanationMuted,
            ActivityTone.Terminated => ExplanationAlert,
            _ => ExplanationAccent,
        };

        if (explanation.Text == _displayedExplanation)
            return;
        _displayedExplanation = explanation.Text;

        if (_openTopic is not null && LearnWhatHappening.Text != explanation.Text)
            LearnWhatHappening.Text = explanation.Text;

        ExplanationText.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            ExplanationText.Text = _displayedExplanation;
            ExplanationText.Opacity = 1;
        }, TimeSpan.FromMilliseconds(200));
    }

    private void OnSelectionChanged(ProcessSample? selected)
    {
        if (selected is null)
        {
            _displayedPid = -1;
            EmptyTitle.Text = "No process selected";
            EmptyBody.Text = "Open the Processes page and select an application to watch how it flows through the CPU, memory, disk and network.";
            ShowEmptyState();
        }
        // A new selection is handled by the next FlowUpdated snapshot,
        // which arrives immediately via the monitor's on-change sample.
    }

    private void ShowEmptyState()
    {
        FlowView.IsVisible = false;
        LegendBar.IsVisible = false;
        ExplanationCard.IsVisible = false;
        _displayedExplanation = null;
        EmptyState.IsVisible = true;
        DiagramRoot.Opacity = 0;
        DiagramRoot.RenderTransform = DiagramSmall;
        CloseLearnPanel();
    }

    private async void Apply(ProcessFlowSnapshot snapshot)
    {
        int version = ++_applyVersion;
        _lastSnapshot = snapshot;

        if (snapshot.ProcessExited)
        {
            _displayedPid = -1;
            EmptyTitle.Text = $"{snapshot.Name} has exited";
            EmptyBody.Text = "The process ended while you were watching it. Select another process on the Processes page.";
            ShowEmptyState();
            return;
        }

        bool switching = _displayedPid != -1 && _displayedPid != snapshot.Pid;
        if (switching)
        {
            DiagramRoot.Opacity = 0;
            DiagramRoot.RenderTransform = DiagramSmall;
            await Task.Delay(240);
            if (version != _applyVersion)
                return; // a newer snapshot took over during the fade
        }

        _displayedPid = snapshot.Pid;
        EmptyState.IsVisible = false;
        FlowView.IsVisible = true;
        LegendBar.IsVisible = true;
        ExplanationCard.IsVisible = true;
        LearnHint.IsVisible = _learnMode.IsLearnMode;
        RefreshLearnPanel(snapshot);

        ProcessNode.Value = snapshot.Name;
        ProcessNode.Meta = $"PID {snapshot.Pid} · {ProcessRowViewModel.StatusLabel(snapshot.Status)}";

        // CPU
        double cpuLevel = snapshot.Cpu.Value is { } cpuRaw ? Math.Clamp(cpuRaw / 100.0, 0, 1) : 0;
        CpuNode.Quality = snapshot.Cpu.Quality;
        CpuNode.Value = snapshot.Cpu.Value is { } cpu ? $"{cpu:0.0}%" : "—";
        CpuNode.Explanation = CpuExplanation
            + (snapshot.Cpu.Quality == MetricQuality.Calculated ? CpuCalculatedNote : "");
        CpuNode.ActivityLevel = cpuLevel;
        ProcessNode.ActivityLevel = cpuLevel;
        SetEdgeTier(FlowCpuEdge, CpuTier(snapshot.Cpu.Value));

        // Memory
        double memoryShare = 0;
        MemoryNode.Quality = snapshot.Memory.Quality;
        if (snapshot.Memory.Value is { } memory)
        {
            MemoryNode.Value = Format.Bytes(memory);
            if (_totalMemoryBytes > 0)
            {
                memoryShare = 100.0 * memory / _totalMemoryBytes;
                MemoryNode.Meta = $"{memoryShare:0.0}% of installed RAM";
            }
        }
        else
        {
            MemoryNode.Value = "—";
            MemoryNode.Meta = null;
        }
        MemoryNode.ActivityLevel = Math.Clamp(memoryShare / 100.0, 0, 1);
        SetEdgeTier(FlowMemoryEdge, MemoryTier(snapshot.Memory.Value is null ? null : memoryShare));

        // Disk
        DiskNode.Quality = snapshot.Disk.Quality;
        DiskNode.Value = snapshot.Disk.Quality == MetricQuality.Unavailable
            ? "Unavailable"
            : snapshot.Disk.Value is { } disk ? Format.Speed(disk) : "—";
        DiskNode.Meta = snapshot.DiskReadBps is { } dr && snapshot.DiskWriteBps is { } dw
            ? $"R {Format.Speed(dr)} · W {Format.Speed(dw)}"
            : null;
        DiskNode.Explanation = DiskExplanation
            + (snapshot.Disk.Quality == MetricQuality.Unavailable ? DiskUnavailableNote : "");
        DiskNode.ActivityLevel = RateLevel(snapshot.Disk.Value, fullScaleBytesPerSec: 20 * 1048576.0);
        SetEdgeTier(FlowDiskEdge, RateTier(snapshot.Disk));

        // Network
        NetworkNode.Quality = snapshot.Network.Quality;
        NetworkNode.Value = snapshot.Network.Quality == MetricQuality.Unavailable
            ? "Unavailable"
            : snapshot.Network.Value is { } net ? Format.Speed(net) : "—";
        NetworkNode.Meta = snapshot.NetworkInBps is { } ni && snapshot.NetworkOutBps is { } no
            ? $"↓ {Format.Speed(ni)} · ↑ {Format.Speed(no)}"
            : null;
        NetworkNode.Explanation = NetworkExplanation
            + (snapshot.Network.Quality == MetricQuality.Unavailable ? NetworkUnavailableNote : "");
        NetworkNode.ActivityLevel = RateLevel(snapshot.Network.Value, fullScaleBytesPerSec: 5 * 1048576.0);
        SetEdgeTier(FlowNetworkEdge, RateTier(snapshot.Network));

        DiagramRoot.Opacity = 1;
        DiagramRoot.RenderTransform = DiagramRest;
    }

    private static double RateLevel(double? bytesPerSec, double fullScaleBytesPerSec) =>
        bytesPerSec is { } rate ? Math.Clamp(rate / fullScaleBytesPerSec, 0, 1) : 0;

    private static EdgeTier CpuTier(double? cpuPercent) => cpuPercent switch
    {
        null => EdgeTier.None,
        < 0.5 => EdgeTier.Idle,
        < 15 => EdgeTier.Low,
        < 50 => EdgeTier.Medium,
        _ => EdgeTier.High,
    };

    private static EdgeTier MemoryTier(double? sharePercent) => sharePercent switch
    {
        null => EdgeTier.None,
        < 5 => EdgeTier.Idle,
        < 15 => EdgeTier.Low,
        < 35 => EdgeTier.Medium,
        _ => EdgeTier.High,
    };

    private static EdgeTier RateTier(FlowMetric metric)
    {
        if (metric.Quality == MetricQuality.Unavailable || metric.Value is not { } rate)
            return EdgeTier.None;
        return rate switch
        {
            < 1024 => EdgeTier.Idle,             // < 1 KB/s
            < 200 * 1024 => EdgeTier.Low,        // < 200 KB/s
            < 5 * 1048576 => EdgeTier.Medium,    // < 5 MB/s
            _ => EdgeTier.High,
        };
    }

    /// <summary>Applies a tier class, touching Classes only when the tier
    /// actually changed so running dash animations aren't restarted.</summary>
    private static void SetEdgeTier(Path edge, EdgeTier tier)
    {
        string? desired = tier == EdgeTier.None ? null : EdgeTierClasses[(int)tier - 1];
        foreach (var cls in EdgeTierClasses)
        {
            if (cls != desired)
                edge.Classes.Remove(cls);
        }
        if (desired is not null && !edge.Classes.Contains(desired))
            edge.Classes.Add(desired);
    }
}
