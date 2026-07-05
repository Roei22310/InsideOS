using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace InsideOS.Controls;

/// <summary>
/// The first-run guided introduction. Purely presentational: it raises events
/// and MainWindow orchestrates navigation, selection, the guided tour and
/// Learn Mode using the existing services. Empty areas pass clicks through,
/// so the experiment and tour steps run over the live application.
/// Every step change fades the previous view out before easing the next one
/// in, and a persistent chip shows "Lesson 1 · Step x of 4" with an animated
/// progress bar.
/// </summary>
public partial class OnboardingOverlay : UserControl
{
    private const int TotalSteps = 4;
    private const double StepBarWidth = 120;

    private static readonly TransformOperations SlideDown = TransformOperations.Parse("translateY(26px)");
    private static readonly TransformOperations SlideUpHidden = TransformOperations.Parse("translateY(-14px)");
    private static readonly TransformOperations Rest = TransformOperations.Parse("translateY(0px)");
    private static readonly TransformOperations PopSmall = TransformOperations.Parse("scale(0.94)");
    private static readonly TransformOperations PopRest = TransformOperations.Parse("scale(1)");
    private static readonly TransformOperations CheckSmall = TransformOperations.Parse("scale(0.6)");

    public event Action? StartLearningRequested;
    public event Action? SkipRequested;
    public event Action? ShowMeWhyRequested;
    public event Action? ContinueLearningRequested;
    public event Action? ContinueRequested;

    public OnboardingOverlay()
    {
        InitializeComponent();
    }

    public void ShowWelcome()
    {
        SetStep(1);
        SwitchTo(() =>
        {
            Reveal(WelcomeView, PopRest);
            ShowChip();
        });
    }

    public void ShowExperiment()
    {
        SetStep(2);
        SwitchTo(() => Reveal(ExperimentView, Rest));
    }

    public void ShowSuccess()
    {
        SetStep(3);
        SwitchTo(() =>
        {
            Backdrop.IsVisible = true;
            Backdrop.Opacity = 1;
            Reveal(SuccessView, PopRest);
            // Staggered celebration: check pops, then title, body, hint, button.
            DispatcherTimer.RunOnce(() =>
            {
                SuccessCheck.Opacity = 1;
                SuccessCheck.RenderTransform = PopRest;
            }, TimeSpan.FromMilliseconds(140));
            StaggerIn(SuccessTitle, 240);
            StaggerIn(SuccessBody, 330);
            StaggerIn(SuccessHint, 410);
            StaggerIn(SuccessButton, 500);
        });
    }

    public void ShowGuided(string caption)
    {
        SetStep(4);
        GuidedCaption.Text = caption;
        SwitchTo(() => Reveal(GuidedView, Rest));
    }

    /// <summary>Fades the tour caption out, swaps the text, fades back in.</summary>
    public void UpdateGuidedCaption(string caption)
    {
        GuidedCaption.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            GuidedCaption.Text = caption;
            GuidedCaption.Opacity = 1;
        }, TimeSpan.FromMilliseconds(170));
    }

    public void ShowLessonComplete(string progressLabel, double progressFraction)
    {
        LessonProgressText.Text = progressLabel;
        SwitchTo(() =>
        {
            Backdrop.IsVisible = true;
            Backdrop.Opacity = 1;
            Reveal(LessonCompleteView, PopRest);
            DispatcherTimer.RunOnce(
                () => LessonProgressFill.Width = StepBarWidth * Math.Clamp(progressFraction, 0, 1),
                TimeSpan.FromMilliseconds(350));
        });
    }

    public void HideAll()
    {
        FadeOutVisibleViews();
        HideChip();
        DispatcherTimer.RunOnce(CollapseHiddenViews, TimeSpan.FromMilliseconds(300));
    }

    public void SetStep(int step)
    {
        StepText.Text = $"STEP {step} OF {TotalSteps}";
        StepFill.Width = StepBarWidth * step / TotalSteps;
    }

    // ---- transition machinery ----

    /// <summary>Fades out whatever is visible, then runs the reveal action —
    /// every step change feels sequenced instead of abrupt.</summary>
    private void SwitchTo(Action reveal)
    {
        bool hadVisible = FadeOutVisibleViews();
        DispatcherTimer.RunOnce(() =>
        {
            CollapseHiddenViews();
            reveal();
        }, TimeSpan.FromMilliseconds(hadVisible ? 200 : 30));
    }

    private void Reveal(Border view, TransformOperations restTransform)
    {
        view.IsVisible = true;
        DispatcherTimer.RunOnce(() =>
        {
            view.Opacity = 1;
            view.RenderTransform = restTransform;
        }, TimeSpan.FromMilliseconds(30));
    }

    private void ShowChip()
    {
        ProgressChip.IsVisible = true;
        DispatcherTimer.RunOnce(() =>
        {
            ProgressChip.Opacity = 1;
            ProgressChip.RenderTransform = Rest;
        }, TimeSpan.FromMilliseconds(30));
    }

    private void HideChip()
    {
        ProgressChip.Opacity = 0;
        ProgressChip.RenderTransform = SlideUpHidden;
        DispatcherTimer.RunOnce(() =>
        {
            if (ProgressChip.Opacity == 0)
                ProgressChip.IsVisible = false;
        }, TimeSpan.FromMilliseconds(300));
    }

    private bool FadeOutVisibleViews()
    {
        bool any = false;
        if (WelcomeView.IsVisible) { WelcomeView.Opacity = 0; any = true; }
        if (ExperimentView.IsVisible) { ExperimentView.Opacity = 0; ExperimentView.RenderTransform = SlideDown; any = true; }
        if (SuccessView.IsVisible) { SuccessView.Opacity = 0; SuccessView.RenderTransform = PopSmall; any = true; }
        if (GuidedView.IsVisible) { GuidedView.Opacity = 0; GuidedView.RenderTransform = SlideDown; any = true; }
        if (LessonCompleteView.IsVisible) { LessonCompleteView.Opacity = 0; LessonCompleteView.RenderTransform = PopSmall; any = true; }
        if (Backdrop.IsVisible) Backdrop.Opacity = 0;
        return any;
    }

    private void CollapseHiddenViews()
    {
        if (WelcomeView.Opacity == 0) WelcomeView.IsVisible = false;
        if (ExperimentView.Opacity == 0) ExperimentView.IsVisible = false;
        if (GuidedView.Opacity == 0) GuidedView.IsVisible = false;
        if (Backdrop.Opacity == 0) Backdrop.IsVisible = false;
        if (SuccessView.Opacity == 0)
        {
            SuccessView.IsVisible = false;
            ResetStagger(SuccessTitle);
            ResetStagger(SuccessBody);
            ResetStagger(SuccessHint);
            ResetStagger(SuccessButton);
            SuccessCheck.Opacity = 0;
            SuccessCheck.RenderTransform = CheckSmall;
        }
        if (LessonCompleteView.Opacity == 0)
        {
            LessonCompleteView.IsVisible = false;
            LessonProgressFill.Width = 0;
        }
    }

    private static void StaggerIn(Control control, int delayMs) =>
        DispatcherTimer.RunOnce(() =>
        {
            control.Opacity = 1;
            control.RenderTransform = Rest;
        }, TimeSpan.FromMilliseconds(delayMs));

    private static void ResetStagger(Control control)
    {
        control.Opacity = 0;
        control.RenderTransform = TransformOperations.Parse("translateY(8px)");
    }

    private void OnStartLearning(object? sender, RoutedEventArgs e) => StartLearningRequested?.Invoke();

    private void OnSkip(object? sender, RoutedEventArgs e) => SkipRequested?.Invoke();

    private void OnShowMeWhy(object? sender, RoutedEventArgs e) => ShowMeWhyRequested?.Invoke();

    private void OnContinueLearning(object? sender, RoutedEventArgs e) => ContinueLearningRequested?.Invoke();

    private void OnContinue(object? sender, RoutedEventArgs e) => ContinueRequested?.Invoke();
}
