using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using InsideOS.Services.Laboratory;
using InsideOS.Services.Narration;

namespace InsideOS.Pages;

/// <summary>
/// The Laboratory: explains an experiment before it runs, narrates it while
/// it runs, and summarizes what was measured afterwards. Pure consumer of
/// <see cref="LaboratoryService"/> — the page renders state; the service owns
/// the child process. All copy comes from the experiment definition or from
/// measured results, so this page stays honest by construction.
/// </summary>
public partial class LaboratoryPage : UserControl
{
    private static readonly IBrush ChipRunning = new SolidColorBrush(Color.Parse("#3FBF7F"));
    private static readonly IBrush ChipDone = new SolidColorBrush(Color.Parse("#4D9FFF"));
    private static readonly IBrush ChipStopped = new SolidColorBrush(Color.Parse("#E5A455"));
    private static readonly IBrush ChipFailed = new SolidColorBrush(Color.Parse("#E56262"));
    private static readonly IBrush BodyText = new SolidColorBrush(Color.Parse("#9AA3B4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#656F82"));

    private readonly LaboratoryService _lab;
    private readonly Action<string> _navigate;
    private readonly ExperimentDefinition _experiment;
    private bool _attached;
    private string _lastPhaseCaption = "";

    public LaboratoryPage(LaboratoryService lab, Action<string> navigate)
    {
        InitializeComponent();
        _lab = lab;
        _navigate = navigate;
        _experiment = lab.Experiments[0];

        // Static copy comes straight from the definition (reusable for future cards).
        CardKicker.Text = $"EXPERIMENT · {_experiment.Category}";
        CardTitle.Text = _experiment.Title;
        AboutText.Text = _experiment.AboutToObserve;
        WhyText.Text = _experiment.WhyInteresting;
        DurationChip.Text = _experiment.DurationText;
        IntensityChip.Text = _experiment.IntensityText;
        foreach (var line in _experiment.WhatToWatch)
            WatchList.Children.Add(Bullet(line, BodyText));

        _lab.Changed += OnLabChanged;
        Render();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Render(); // catch up — the experiment keeps running while the page is hidden
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    private void OnLabChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_attached)
            Render();
    });

    // ---- rendering ----

    private void Render()
    {
        if (_lab.RunningDefinition is not null)
            RenderRunning();
        else if (_lab.LastResult is { } result && result.ExperimentId == _experiment.Id)
            RenderDone(result);
        else
            RenderIdle();
    }

    private void ShowView(Control view, string chip, IBrush? chipBrush)
    {
        IdleView.IsVisible = ReferenceEquals(view, IdleView);
        RunningView.IsVisible = ReferenceEquals(view, RunningView);
        DoneView.IsVisible = ReferenceEquals(view, DoneView);
        StateChip.Text = chip;
        if (chipBrush is not null)
            StateChip.Foreground = chipBrush;
    }

    private void RenderIdle() => ShowView(IdleView, "", null);

    private void RenderRunning()
    {
        ShowView(RunningView, "RUNNING", ChipRunning);

        var elapsed = _lab.Elapsed;
        ElapsedText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        var total = _experiment.ExpectedDuration;
        ElapsedOfText.Text = $"of about {(int)total.TotalMinutes}:{total.Seconds:00}";

        // Phase caption: latest one whose time has arrived. Fade only on change.
        string caption = "";
        foreach (var phase in _experiment.PhaseCaptions)
            if (elapsed >= phase.At)
                caption = phase.Caption;
        if (caption != _lastPhaseCaption)
        {
            _lastPhaseCaption = caption;
            PhaseText.Opacity = 0;
            DispatcherTimer.RunOnce(() =>
            {
                PhaseText.Text = caption;
                PhaseText.Opacity = 1;
            }, TimeSpan.FromMilliseconds(160));
        }

        if (_lab.LiveWorkerCpu is { } cpu)
        {
            LiveCpuText.Text = $"{cpu:0.0}% of one core";
            LiveCpuNote.Text = "Measured directly — macOS reveals exact numbers because InsideOS owns this process.";
        }
        else
        {
            LiveCpuText.Text = "—";
            LiveCpuNote.Text = "Waiting for the first per-second measurement…";
        }
    }

    private void RenderDone(ExperimentResult result)
    {
        SummaryList.Children.Clear();
        NoticedList.Children.Clear();

        // The shared Narration Engine interprets the run — the same layer
        // that narrates Timeline stories, Insights and Action Flow. This page
        // only renders its evidence (measured facts) and summary.
        var narration = NarrationEngine.NarrateExperiment(result);

        switch (result.Outcome)
        {
            case ExperimentOutcome.Completed:
                ShowView(DoneView, "COMPLETED", ChipDone);
                DoneLabel.Text = "WHAT HAPPENED";
                NoticedBlock.IsVisible = true;

                foreach (var evidence in narration.Evidence)
                    SummaryList.Children.Add(Bullet(evidence.Fact, BodyText));

                NoticedList.Children.Add(Bullet(
                    "It appeared in Processes as a second “InsideOS” — the operating system identifies "
                    + "processes by ID, not by name.", MutedText));
                NoticedList.Children.Add(Bullet(
                    "Its status read “Sleeping” while it waited, then “Running” once real work began — "
                    + "that word describes the last second of behavior, not the process's purpose.", MutedText));
                NoticedList.Children.Add(Bullet(
                    "The Timeline likely recorded its CPU rise as a story, and Action Flow's particles "
                    + "sped up while it worked.", MutedText));
                NoticedList.Children.Add(Bullet(
                    "When it exited, it vanished from the process list — the operating system reclaimed "
                    + "its memory and CPU time immediately.", MutedText));
                break;

            case ExperimentOutcome.Stopped:
                ShowView(DoneView, "STOPPED", ChipStopped);
                DoneLabel.Text = "WHAT HAPPENED";
                NoticedBlock.IsVisible = false;
                SummaryList.Children.Add(Bullet(narration.Summary, BodyText));
                break;

            default:
                ShowView(DoneView, "COULD NOT RUN", ChipFailed);
                DoneLabel.Text = "WHAT WENT WRONG";
                NoticedBlock.IsVisible = false;
                SummaryList.Children.Add(Bullet(narration.Summary, BodyText));
                break;
        }
    }

    private static Control Bullet(string text, IBrush brush)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new TextBlock
        {
            Text = "•",
            FontSize = 12.5,
            Foreground = brush,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
        });
        var body = new TextBlock
        {
            Text = text,
            FontSize = 12,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            Foreground = brush,
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    // ---- interactions ----

    private void OnStart(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _lab.Start(_experiment); // ignored if something is already running

    private void OnStop(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _lab.Stop();

    private void OnOpenFlow(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _navigate("flow");
}
