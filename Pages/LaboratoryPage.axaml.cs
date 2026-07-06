using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using InsideOS.Services.Laboratory;
using InsideOS.Services.Narration;

namespace InsideOS.Pages;

/// <summary>
/// The Laboratory: explains each experiment before it runs, narrates it
/// while it runs, and summarizes what was measured afterwards. Pure consumer
/// of <see cref="LaboratoryService"/> — the page renders state; the service
/// owns the child process; the shared Narration Engine interprets results.
/// Every word on a card comes from the experiment definition or from
/// measured results, so this page stays honest by construction and knows
/// nothing about any specific experiment.
/// </summary>
public partial class LaboratoryPage : UserControl
{
    private static readonly IBrush ChipRunning = new SolidColorBrush(Color.Parse("#3FBF7F"));
    private static readonly IBrush ChipDone = new SolidColorBrush(Color.Parse("#4D9FFF"));
    private static readonly IBrush ChipStopped = new SolidColorBrush(Color.Parse("#E5A455"));
    private static readonly IBrush ChipFailed = new SolidColorBrush(Color.Parse("#E56262"));
    private static readonly IBrush BodyText = new SolidColorBrush(Color.Parse("#9AA3B4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#656F82"));

    /// <summary>All controls of one experiment card, bound to one definition.</summary>
    private sealed class Card
    {
        public required ExperimentDefinition Definition;
        public required TextBlock StateChip, CardTitle;
        public required StackPanel IdleView, RunningView, DoneView;
        public required StackPanel WatchList, SummaryList, NoticedList, NoticedBlock;
        public required TextBlock ElapsedText, ElapsedOfText, PhaseText, LiveCpuText, LiveCpuNote, DoneLabel;
        public required Button StartButton;
        public string LastPhaseCaption = "";
    }

    private readonly LaboratoryService _lab;
    private readonly Action<string> _navigate;
    private readonly List<Card> _cards = new();
    private bool _attached;

    public LaboratoryPage(LaboratoryService lab, Action<string> navigate)
    {
        InitializeComponent();
        _lab = lab;
        _navigate = navigate;

        // Two cards in XAML, bound to the catalog in order. Future experiments
        // extend this list (or replace it with a fully templated version).
        _cards.Add(new Card
        {
            Definition = lab.Experiments[0],
            StateChip = StateChip, CardTitle = CardTitle,
            IdleView = IdleView, RunningView = RunningView, DoneView = DoneView,
            WatchList = WatchList, SummaryList = SummaryList,
            NoticedList = NoticedList, NoticedBlock = NoticedBlock,
            ElapsedText = ElapsedText, ElapsedOfText = ElapsedOfText, PhaseText = PhaseText,
            LiveCpuText = LiveCpuText, LiveCpuNote = LiveCpuNote, DoneLabel = DoneLabel,
            StartButton = StartButton,
        });
        _cards.Add(new Card
        {
            Definition = lab.Experiments[1],
            StateChip = StateChip2, CardTitle = CardTitle2,
            IdleView = IdleView2, RunningView = RunningView2, DoneView = DoneView2,
            WatchList = WatchList2, SummaryList = SummaryList2,
            NoticedList = NoticedList2, NoticedBlock = NoticedBlock2,
            ElapsedText = ElapsedText2, ElapsedOfText = ElapsedOfText2, PhaseText = PhaseText2,
            LiveCpuText = LiveCpuText2, LiveCpuNote = LiveCpuNote2, DoneLabel = DoneLabel2,
            StartButton = StartButton2,
        });

        // Static copy comes straight from the definitions.
        CardKicker.Text = $"EXPERIMENT · {_cards[0].Definition.Category}";
        CardKicker2.Text = $"EXPERIMENT · {_cards[1].Definition.Category}";
        foreach (var card in _cards)
        {
            card.CardTitle.Text = card.Definition.Title;
            foreach (var line in card.Definition.WhatToWatch)
                card.WatchList.Children.Add(Bullet(line, BodyText));
        }
        AboutText.Text = _cards[0].Definition.AboutToObserve;
        WhyText.Text = _cards[0].Definition.WhyInteresting;
        DurationChip.Text = _cards[0].Definition.DurationText;
        IntensityChip.Text = _cards[0].Definition.IntensityText;
        AboutText2.Text = _cards[1].Definition.AboutToObserve;
        WhyText2.Text = _cards[1].Definition.WhyInteresting;
        DurationChip2.Text = _cards[1].Definition.DurationText;
        IntensityChip2.Text = _cards[1].Definition.IntensityText;

        _lab.Changed += OnLabChanged;
        Render();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Render(); // catch up — an experiment keeps running while the page is hidden
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
        var running = _lab.RunningDefinition;
        foreach (var card in _cards)
        {
            if (ReferenceEquals(running, card.Definition))
                RenderRunning(card);
            else if (_lab.LastResult is { } result && result.ExperimentId == card.Definition.Id
                     && running is null)
                RenderDone(card, result);
            else
                RenderIdle(card, startEnabled: running is null);
        }
    }

    private static void ShowView(Card card, StackPanel view, string chip, IBrush? chipBrush)
    {
        card.IdleView.IsVisible = ReferenceEquals(view, card.IdleView);
        card.RunningView.IsVisible = ReferenceEquals(view, card.RunningView);
        card.DoneView.IsVisible = ReferenceEquals(view, card.DoneView);
        card.StateChip.Text = chip;
        if (chipBrush is not null)
            card.StateChip.Foreground = chipBrush;
    }

    private static void RenderIdle(Card card, bool startEnabled)
    {
        ShowView(card, card.IdleView, "", null);
        // One experiment at a time: other cards wait until the run finishes.
        card.StartButton.IsEnabled = startEnabled;
    }

    private void RenderRunning(Card card)
    {
        ShowView(card, card.RunningView, "RUNNING", ChipRunning);

        var elapsed = _lab.Elapsed;
        card.ElapsedText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        var total = card.Definition.ExpectedDuration;
        card.ElapsedOfText.Text = $"of about {(int)total.TotalMinutes}:{total.Seconds:00}";

        // Phase caption: latest one whose time has arrived. Fade only on change.
        string caption = "";
        foreach (var phase in card.Definition.PhaseCaptions)
            if (elapsed >= phase.At)
                caption = phase.Caption;
        if (caption != card.LastPhaseCaption)
        {
            card.LastPhaseCaption = caption;
            card.PhaseText.Opacity = 0;
            DispatcherTimer.RunOnce(() =>
            {
                card.PhaseText.Text = caption;
                card.PhaseText.Opacity = 1;
            }, TimeSpan.FromMilliseconds(160));
        }

        if (_lab.LiveWorkerCpu is { } cpu)
        {
            card.LiveCpuText.Text = $"{cpu:0.0}% of one core";
            card.LiveCpuNote.Text = "Measured directly — macOS reveals exact numbers because InsideOS owns this process.";
        }
        else
        {
            card.LiveCpuText.Text = "—";
            card.LiveCpuNote.Text = "Waiting for the first per-second measurement…";
        }
    }

    private static void RenderDone(Card card, ExperimentResult result)
    {
        card.StartButton.IsEnabled = true;
        card.SummaryList.Children.Clear();
        card.NoticedList.Children.Clear();

        // The shared Narration Engine interprets the run — the same layer
        // that narrates Timeline stories, Insights and Action Flow. This page
        // only renders its evidence (measured facts) and summary.
        var narration = NarrationEngine.NarrateExperiment(result);

        switch (result.Outcome)
        {
            case ExperimentOutcome.Completed:
                ShowView(card, card.DoneView, "COMPLETED", ChipDone);
                card.DoneLabel.Text = "WHAT HAPPENED";
                card.NoticedBlock.IsVisible = true;

                foreach (var evidence in narration.Evidence)
                    card.SummaryList.Children.Add(Bullet(evidence.Fact, BodyText));
                foreach (var learned in card.Definition.WhatYouLearned)
                    card.NoticedList.Children.Add(Bullet(learned, MutedText));
                break;

            case ExperimentOutcome.Stopped:
                ShowView(card, card.DoneView, "STOPPED", ChipStopped);
                card.DoneLabel.Text = "WHAT HAPPENED";
                card.NoticedBlock.IsVisible = false;
                card.SummaryList.Children.Add(Bullet(narration.Summary, BodyText));
                break;

            default:
                ShowView(card, card.DoneView, "COULD NOT RUN", ChipFailed);
                card.DoneLabel.Text = "WHAT WENT WRONG";
                card.NoticedBlock.IsVisible = false;
                card.SummaryList.Children.Add(Bullet(narration.Summary, BodyText));
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
        _lab.Start(_cards[0].Definition); // ignored if something is already running

    private void OnStart2(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _lab.Start(_cards[1].Definition);

    private void OnStop(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _lab.Stop();

    private void OnStop2(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _lab.Stop();

    private void OnOpenFlow(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _navigate("flow");
}
