using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using InsideOS.Pages;
using Avalonia.Threading;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Explanations;
using InsideOS.Services.History;
using InsideOS.Services.Insights;
using InsideOS.Services.Laboratory;
using InsideOS.Services.Learning;
using InsideOS.Services.Processes;
using InsideOS.Services.Replay;
using InsideOS.Services.Settings;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;

namespace InsideOS;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, Control> _pages = new();
    private readonly LiveMetricsService _metrics;
    private readonly ProcessMonitorService _processes;
    private readonly ProcessSelection _processSelection = new();
    private readonly ProcessFlowMonitor _flow;
    private readonly ExplanationFeed _explanations;
    private readonly RuleBasedExplanationEngine _flowExplainer;
    private readonly LearnModeState _learnMode = new();
    private readonly AppSettingsService _settings = AppSettingsService.Load();
    private readonly LessonManager _lessons;
    private readonly SystemStoryService _story;
    private readonly InsightService _insights;
    private readonly MetricHistoryService _history;
    private readonly LaboratoryService _lab;
    private readonly ReplayState _replayState = new();
    private readonly SessionRecorder _recorder;
    private readonly ReplayService _replay;
    private readonly ILearnContentService _learnContent;
    private bool _syncingNav;
    private bool _onboardingSelectionPending;
    private int _tourVersion;

    public MainWindow()
    {
        InitializeComponent();
        _metrics = new LiveMetricsService(CreateMetricsSource());
        _metrics.Start();
        _processes = new ProcessMonitorService(CreateProcessSource());
        _flow = new ProcessFlowMonitor(_processes, _processSelection, CreateProcessIoSource());
        _flowExplainer = new RuleBasedExplanationEngine();
        _explanations = new ExplanationFeed(_flow, _flowExplainer);
        _lessons = new LessonManager(new BuiltInLessonProvider(), _settings);
        _story = new SystemStoryService(_processes, _processSelection, CreateProcessIoSource(), _replayState);
        _story.EnsureStarted(); // record the system's story from launch, not from first page visit
        _insights = new InsightService(_metrics, _processes, _story, _replayState); // interpretation lives in NarrationEngine
        _insights.EnsureStarted(); // pure subscription on top of already-running services
        _history = new MetricHistoryService(_metrics, _processes, _replayState);
        _history.EnsureStarted(); // rolling in-memory history from launch, same sources
        _recorder = new SessionRecorder(_processes, _metrics, _flow, _story); // attaches only while an experiment records
        _lab = new LaboratoryService(_processes, _processSelection, _metrics, _story, _recorder, _replayState);
        _replay = new ReplayService(_replayState, _processes, _metrics, _flow, _story,
            _insights, _history, _recorder, _lab);
        _replay.Changed += UpdateReplayPill; // title-bar pill says when we're in the past
        _replay.Sought += _flowExplainer.Reset; // jumps never smooth evidence across time
        _learnContent = new LearnContentService(_metrics.StaticInfo.TotalMemoryBytes); // shared by Action Flow + Processes
        SelectNav("live"); // Learning sits above Live View, but the dashboard stays the start page

        Onboarding.StartLearningRequested += OnOnboardingStartLearning;
        Onboarding.SkipRequested += OnOnboardingFinished;
        Onboarding.ShowMeWhyRequested += OnOnboardingShowMeWhy;
        Onboarding.ContinueLearningRequested += OnOnboardingContinueLearning;
        Onboarding.ContinueRequested += OnOnboardingFinished;
        _processSelection.Changed += OnOnboardingSelection;
        if (!_settings.OnboardingCompleted)
            Onboarding.ShowWelcome();
    }

    // ---- First-run experience orchestration (reuses the normal services) ----

    private void OnOnboardingStartLearning()
    {
        SelectNav("processes");
        Onboarding.ShowExperiment();
        _onboardingSelectionPending = true;
    }

    private void OnOnboardingSelection(ProcessSample? selected)
    {
        if (!_onboardingSelectionPending || selected is null)
            return;
        _onboardingSelectionPending = false;
        Dispatcher.UIThread.Post(Onboarding.ShowSuccess);
    }

    /// <summary>Guided discovery: spotlights the flow nodes one at a time with
    /// captions, then opens the Learn panel and shows the Lesson Complete card.</summary>
    private async void OnOnboardingShowMeWhy()
    {
        int version = ++_tourVersion;

        _learnMode.Set(true);
        UpdateModeSegments();
        SelectNav("flow");
        if (_pages.TryGetValue("flow", out var page) && page is not ActionFlowPage)
            return;
        var flowPage = (ActionFlowPage)_pages["flow"];

        Onboarding.ShowGuided("Watch the diagram — let's follow your application through the operating system.");

        async Task<bool> StepAsync(int delayMs, LearnTopicId? topic, string? caption)
        {
            await Task.Delay(delayMs);
            if (version != _tourVersion)
                return false;
            flowPage.SpotlightNode(topic);
            if (caption is not null)
                Onboarding.UpdateGuidedCaption(caption);
            return true;
        }

        if (!await StepAsync(1000, LearnTopicId.Process,
            "This is the process you selected — your application as the operating system sees it."))
            return;
        if (!await StepAsync(2200, LearnTopicId.Cpu,
            "This is where your application currently spends processor time."))
            return;
        if (!await StepAsync(2400, LearnTopicId.Memory,
            "This is the memory currently reserved by the application."))
            return;
        if (!await StepAsync(2400, LearnTopicId.Network,
            "This is the network activity generated by the application."))
            return;
        if (!await StepAsync(2400, null, null))
            return;

        flowPage.OpenLearnTopic(LearnTopicId.Process);
        flowPage.HighlightExplanationCard();
        await Task.Delay(700);
        if (version != _tourVersion)
            return;

        if (_lessons.FirstLesson is { } lesson1)
        {
            bool wasCompleted = _lessons.IsCompleted(lesson1);
            _lessons.MarkCompleted(lesson1.Id);
            if (!wasCompleted)
                _story.ReportLearningEvent("Lesson completed",
                    $"You completed \"{lesson1.Title}\" — Lesson 1 of your learning journey.");
        }
        Onboarding.ShowLessonComplete(
            $"LESSON 1 OF {_lessons.PlannedLessonCount} · PROGRESS {_lessons.ProgressPercent}%",
            _lessons.ProgressPercent / 100.0);
    }

    private void OnOnboardingContinueLearning()
    {
        // The Learning Journey page is home for all future lessons.
        FinishOnboarding();
        SelectNav("learning");
    }

    private void OnOnboardingFinished() => FinishOnboarding();

    private void FinishOnboarding()
    {
        _tourVersion++;
        Onboarding.HideAll();
        _onboardingSelectionPending = false;
        if (_pages.TryGetValue("flow", out var page) && page is ActionFlowPage flowPage)
            flowPage.SpotlightNode(null);
        _settings.OnboardingCompleted = true;
        _settings.Save();
        _lessons.RaiseChanged(); // Lesson 1 completion syncs with the onboarding flag
    }

    /// <summary>Lesson 1 *is* the guided introduction; future lessons plug in here.</summary>
    private void StartLesson(Lesson lesson)
    {
        if (lesson.Id == _lessons.FirstLesson?.Id)
            RestartOnboarding();
    }

    /// <summary>Dashboard "most active process" click: select it and open Action Flow.</summary>
    private void OpenProcessInFlow(ProcessSample sample)
    {
        _processSelection.Select(sample);
        SelectNav("flow");
    }

    /// <summary>
    /// Timeline click-through: select the story's process, open Action Flow in
    /// Learn Mode, open the explanation panel and briefly spotlight the node
    /// the story is about. Learning stories route to the Learning page.
    /// </summary>
    private void OpenTimelineStory(TimelineStorySnapshot story)
    {
        if (story.Pid < 0)
        {
            SelectNav("learning");
            return;
        }

        if (story.LastSample is { } sample)
            _processSelection.Select(sample);
        _learnMode.Set(true);
        UpdateModeSegments();
        SelectNav("flow");

        if (!_pages.TryGetValue("flow", out var page) || page is not ActionFlowPage flowPage)
            return;
        var topic = TopicFor(story);
        int version = _tourVersion;
        DispatcherTimer.RunOnce(() =>
        {
            if (version != _tourVersion)
                return; // onboarding took over in the meantime
            flowPage.OpenLearnTopic(topic);
            flowPage.SpotlightNode(topic);
            flowPage.HighlightExplanationCard();
            DispatcherTimer.RunOnce(() =>
            {
                if (version == _tourVersion)
                    flowPage.SpotlightNode(null);
            }, TimeSpan.FromMilliseconds(2600));
        }, TimeSpan.FromMilliseconds(420));
    }

    private static LearnTopicId TopicFor(TimelineStorySnapshot story)
    {
        // The latest event decides the highlighted node, falling back to the
        // most significant category the story contains.
        var latest = story.Events[^1].Category;
        if (MapTopic(latest) is { } topic)
            return topic;
        foreach (var category in story.Categories)
            if (MapTopic(category) is { } mapped)
                return mapped;
        return LearnTopicId.Process;
    }

    private static LearnTopicId? MapTopic(TimelineCategory category) => category switch
    {
        TimelineCategory.Cpu => LearnTopicId.Cpu,
        TimelineCategory.Memory => LearnTopicId.Memory,
        TimelineCategory.Disk => LearnTopicId.Disk,
        TimelineCategory.Network => LearnTopicId.Network,
        TimelineCategory.Process => LearnTopicId.Process,
        _ => null,
    };

    private void RestartOnboarding()
    {
        _tourVersion++;
        _onboardingSelectionPending = false;
        if (_pages.TryGetValue("flow", out var page) && page is ActionFlowPage flowPage)
            flowPage.SpotlightNode(null);
        Onboarding.ShowWelcome();
    }

    private void SelectNav(string key)
    {
        if (key == "settings")
        {
            SettingsList.SelectedIndex = 0;
            return;
        }
        foreach (var item in NavList.Items)
        {
            if (item is ListBoxItem { Tag: string tag } listItem && tag == key)
            {
                NavList.SelectedItem = listItem;
                return;
            }
        }
    }

    private static ISystemMetricsSource CreateMetricsSource() =>
        OperatingSystem.IsMacOS()
            ? new MacSystemMetricsSource()
            : new FallbackSystemMetricsSource(); // TODO(Windows): WindowsSystemMetricsSource

    private static IProcessInfoSource CreateProcessSource() =>
        OperatingSystem.IsMacOS()
            ? new MacProcessInfoSource()
            : new FallbackProcessInfoSource(); // TODO(Windows): WindowsProcessInfoSource

    private static IProcessIoSource CreateProcessIoSource() =>
        OperatingSystem.IsMacOS()
            ? new MacProcessIoSource()
            : new FallbackProcessIoSource(); // TODO(Windows): WindowsProcessIoSource

    protected override void OnClosed(EventArgs e)
    {
        _lab.Dispose(); // kills any running experiment child first
        _history.Dispose();
        _insights.Dispose();
        _story.Dispose();
        _metrics.Dispose();
        _processes.Dispose();
        _flow.Dispose();
        base.OnClosed(e);
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingNav
            || sender is not ListBox list
            || list.SelectedItem is not ListBoxItem item
            || item.Tag is not string key)
        {
            return;
        }

        // Keep only one of the two nav lists selected at a time.
        _syncingNav = true;
        if (ReferenceEquals(list, NavList))
            SettingsList.SelectedIndex = -1;
        else
            NavList.SelectedIndex = -1;
        _syncingNav = false;

        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
        }

        PageHost.Content = page;
        CrumbText.Text = TitleFor(key);
    }

    private Control CreatePage(string key)
    {
        switch (key)
        {
            case "learning":
                return new LearningPage(_lessons, StartLesson);
            case "laboratory":
                _processes.EnsureStarted(); // experiments are observed through the normal pipeline
                return new LaboratoryPage(_lab, SelectNav, StartReplay);
            case "replay":
                return new ReplayPage(_replay);
            case "processes":
                _processes.EnsureStarted();
                return new ProcessExplorerPage(_processes, _processSelection, _learnContent,
                    _metrics.StaticInfo.TotalMemoryBytes);
            case "timeline":
                _story.EnsureStarted();
                return new TimelinePage(_story, _insights, OpenTimelineStory);
            case "flow":
                _flow.EnsureStarted();
                return new ActionFlowPage(_flow, _explanations, _learnMode, _learnContent,
                    _metrics.StaticInfo.TotalMemoryBytes);
            case "metrics":
                return new MetricsPage(_history, _story, OpenTimelineStory, OpenProcessInFlow,
                    _metrics.StaticInfo.TotalMemoryBytes);
            case "settings":
                return new SettingsPage(_lessons, RestartOnboarding);
            default:
                _processes.EnsureStarted(); // already running via the story service; explicit for clarity
                return new LiveViewPage(_metrics, _processes, _story, _insights, _lessons,
                    OpenProcessInFlow, OpenTimelineStory, SelectNav);
        }
    }

    private static string TitleFor(string key) => key switch
    {
        "learning" => "Learning",
        "laboratory" => "Laboratory",
        "replay" => "Replay",
        "processes" => "Processes",
        "timeline" => "Timeline",
        "flow" => "Action Flow",
        "metrics" => "Metrics",
        "settings" => "Settings",
        _ => "Live View",
    };

    /// <summary>Laboratory "Replay Session" click-through: enter replay and show the page.</summary>
    private void StartReplay()
    {
        if (_replay.Start())
            SelectNav("replay");
    }

    private void UpdateReplayPill()
    {
        if (_replayState.IsReplaying)
        {
            StatusText.Text = $"Replaying · {(int)_replay.Position.TotalMinutes:0}:{_replay.Position.Seconds:00}";
            StatusDot.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5A455"));
        }
        else
        {
            StatusText.Text = "Ready";
            StatusDot.Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3FBF7F"));
        }
    }

    private void OnMonitorModePressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _learnMode.Set(false);
        UpdateModeSegments();
    }

    private void OnLearnModePressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _learnMode.Set(true);
        UpdateModeSegments();
    }

    private void UpdateModeSegments()
    {
        if (_learnMode.IsLearnMode)
        {
            MonitorModeSeg.Classes.Remove("selected");
            if (!LearnModeSeg.Classes.Contains("selected"))
                LearnModeSeg.Classes.Add("selected");
        }
        else
        {
            LearnModeSeg.Classes.Remove("selected");
            if (!MonitorModeSeg.Classes.Contains("selected"))
                MonitorModeSeg.Classes.Add("selected");
        }
    }

    private void OnDragAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
