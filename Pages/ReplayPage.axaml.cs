using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using InsideOS.Services.Replay;
using InsideOS.Services.Timeline;

namespace InsideOS.Pages;

/// <summary>
/// Replay 1.0: a calm transport over the last recorded session. Play, pause,
/// restart, current time and a list of the session's major events — nothing
/// more. The actual time travel happens everywhere else: while a replay is
/// active, every page of the app renders the replayed moment through the
/// same feeds it always uses.
/// </summary>
public partial class ReplayPage : UserControl
{
    private static readonly IBrush PrimaryText = new SolidColorBrush(Color.Parse("#EDEFF4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush AccentText = new SolidColorBrush(Color.Parse("#4D9FFF"));
    private static readonly IBrush ChipPlaying = new SolidColorBrush(Color.Parse("#3FBF7F"));
    private static readonly IBrush ChipPaused = new SolidColorBrush(Color.Parse("#E5A455"));
    private static readonly IBrush ChipEnded = new SolidColorBrush(Color.Parse("#4D9FFF"));
    private static readonly IBrush ChipReady = new SolidColorBrush(Color.Parse("#656F82"));

    private static readonly IBrush MarkerInfo = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush MarkerNotice = new SolidColorBrush(Color.Parse("#E5A455"));
    private static readonly IBrush MarkerHigh = new SolidColorBrush(Color.Parse("#E56262"));

    private readonly ReplayService _replay;
    private readonly List<(TimeSpan Offset, TextBlock Time, TextBlock Title)> _eventRows = new();
    private ReplaySession? _renderedSession;
    private bool _attached;
    private bool _syncingScrub;

    public ReplayPage(ReplayService replay)
    {
        InitializeComponent();
        _replay = replay;
        _replay.Changed += () => { if (_attached) Render(); }; // Changed is raised on the UI thread

        // Scrubbing: grabbing the track pauses playback (and it stays paused
        // on release); every value change is a direct jump through the same
        // frame application the player uses. Tunnel routing sees the press
        // before the slider consumes it.
        Scrub.AddHandler(PointerPressedEvent, (_, _) => _replay.PauseForScrub(),
            RoutingStrategies.Tunnel);
        Scrub.ValueChanged += OnScrubValueChanged;
        MarkerCanvas.SizeChanged += (_, _) => PositionMarkers();
    }

    private void OnScrubValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingScrub)
            return; // playback moving the thumb, not the user
        _replay.SeekTo(TimeSpan.FromSeconds(e.NewValue));
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Render();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false; // replay itself continues; only this page pauses rendering
    }

    private void Render()
    {
        var session = _replay.AvailableSession;
        bool has = session is not null && session.Frames.Count > 0;
        EmptyState.IsVisible = !has;
        SessionCard.IsVisible = has;
        EventsCard.IsVisible = has;
        if (session is null)
            return;

        if (!ReferenceEquals(session, _renderedSession))
        {
            _renderedSession = session;
            SessionTitle.Text = session.Title;
            SessionMeta.Text =
                $"Recorded {session.StartedAt:HH:mm:ss} · {Format(session.Duration)} of real measurements · "
                + $"{session.Frames.Count} one-second frames · {session.Events.Count} events";
            BuildEventRows(session);
            _syncingScrub = true;
            Scrub.Maximum = Math.Max(1, session.Duration.TotalSeconds);
            Scrub.Value = 0;
            _syncingScrub = false;
            EndLabel.Text = Format(session.Duration);
            PositionMarkers();
        }

        var playback = _replay.Playback;
        bool active = playback is ReplayPlayback.Playing or ReplayPlayback.Paused or ReplayPlayback.Ended;

        PlayButton.Content = playback switch
        {
            ReplayPlayback.Playing => "⏸  Pause",
            ReplayPlayback.Paused => "▶  Resume",
            ReplayPlayback.Ended => "▶  Play Again",
            _ => "▶  Play",
        };
        RestartButton.IsVisible = playback is ReplayPlayback.Playing or ReplayPlayback.Paused;
        ExitButton.IsVisible = active;
        ReplayHint.IsVisible = active;

        (StateChip.Text, StateChip.Foreground) = playback switch
        {
            ReplayPlayback.Playing => ("REPLAYING", ChipPlaying),
            ReplayPlayback.Paused => ("PAUSED", ChipPaused),
            ReplayPlayback.Ended => ("ENDED", ChipEnded),
            _ => ("READY", ChipReady),
        };

        TimeText.Text = active
            ? $"{Format(_replay.Position)} / {Format(session.Duration)}"
            : Format(session.Duration);

        // The scrubber is live only while inside the replay; playback moves
        // the thumb without re-triggering a seek.
        ScrubBlock.IsVisible = true;
        Scrub.IsEnabled = active;
        if (active)
        {
            _syncingScrub = true;
            Scrub.Value = _replay.Position.TotalSeconds;
            _syncingScrub = false;
        }

        // Events up to the current position read as "already happened".
        var position = active ? _replay.Position : TimeSpan.MinValue;
        foreach (var (offset, time, title) in _eventRows)
        {
            bool played = offset <= position;
            time.Foreground = played ? AccentText : MutedText;
            title.Foreground = played ? PrimaryText : MutedText;
        }
    }

    /// <summary>
    /// The tape records every event the system produced; the page curates.
    /// Major = the experiment's own process, learning milestones, and anything
    /// the timeline itself considered noteworthy. The rest still replays —
    /// it just doesn't clutter the map.
    /// </summary>
    private static bool IsMajor(ReplaySession session, ReplayEvent evt) =>
        evt.Pid == session.FocusPid
        || evt.Pid < 0
        || evt.Event.Severity >= TimelineSeverity.Notice;

    private static IEnumerable<ReplayEvent> MajorEvents(ReplaySession session)
    {
        foreach (var evt in session.Events)
            if (IsMajor(session, evt) && evt.Event.Time >= session.StartedAt)
                yield return evt;
    }

    private void BuildEventRows(ReplaySession session)
    {
        _eventRows.Clear();
        EventsList.Children.Clear();
        int hidden = 0;
        foreach (var evt in session.Events)
            if (!IsMajor(session, evt))
                hidden++;
        HiddenNote.IsVisible = hidden > 0;
        HiddenNote.Text = hidden > 0
            ? $"{hidden} quieter background event{(hidden == 1 ? "" : "s")} from other processes "
              + "aren't listed — the replay still reproduces everything."
            : "";
        foreach (var evt in MajorEvents(session))
        {
            var offset = evt.Event.Time - session.StartedAt;
            if (offset < TimeSpan.Zero)
                continue;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*") };
            var time = new TextBlock
            {
                Text = Format(offset),
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = MutedText,
            };
            grid.Children.Add(time);
            var title = new TextBlock
            {
                Text = evt.Pid >= 0 ? $"{evt.ProcessName} — {evt.Event.Title}" : evt.Event.Title,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = MutedText,
            };
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);
            EventsList.Children.Add(grid);
            _eventRows.Add((offset, time, title));
        }
    }

    /// <summary>Small muted dots along the track marking the major events.</summary>
    private void PositionMarkers()
    {
        MarkerCanvas.Children.Clear();
        var session = _renderedSession;
        double width = MarkerCanvas.Bounds.Width;
        if (session is null || width <= 0 || session.Duration.TotalSeconds <= 0)
            return;
        foreach (var evt in MajorEvents(session))
        {
            var offset = evt.Event.Time - session.StartedAt;
            if (offset < TimeSpan.Zero)
                continue;
            double x = offset.TotalSeconds / session.Duration.TotalSeconds * width;
            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = evt.Event.Severity switch
                {
                    TimelineSeverity.High => MarkerHigh,
                    TimelineSeverity.Notice => MarkerNotice,
                    _ => MarkerInfo,
                },
                Opacity = 0.9,
            };
            Canvas.SetLeft(dot, Math.Clamp(x - 2, 0, Math.Max(0, width - 4)));
            Canvas.SetTop(dot, 0);
            MarkerCanvas.Children.Add(dot);
        }
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:0}:{t.Seconds:00}";

    private void OnPlayPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_replay.Playback == ReplayPlayback.Inactive)
            _replay.Start();
        else
            _replay.TogglePlayPause();
    }

    private void OnRestart(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _replay.Restart();

    private void OnExit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _replay.Exit();
}
