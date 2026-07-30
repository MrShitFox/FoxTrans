using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FoxTrans.Desktop.ViewModels;

namespace FoxTrans.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    private readonly Stopwatch _interactionTime = new();
    private readonly Dictionary<TextBox, TimeSpan> _textPulseDeadlines = [];
    private readonly Border _sectionContent;
    private readonly ScrollViewer _sectionScroller;
    private readonly TranslateTransform _sectionTransform;
    private SectionTransitionPhase _sectionPhase;
    private SettingsSection? _pendingSection;
    private TimeSpan _sectionPhaseStarted;
    private double _phaseStartOpacity;
    private double _phaseStartOffset;
    private bool _interactionFrameQueued;
    private bool _isAttached;

    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        _sectionContent = this.FindControl<Border>("SectionContent")!;
        _sectionScroller = this.FindControl<ScrollViewer>("SectionScroller")!;
        _sectionTransform =
            (TranslateTransform)_sectionContent.RenderTransform!;
        AddHandler(TextBox.TextChangedEvent, OnTextBoxTextChanged);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnSectionClick(
        object? sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not Button
            {
                CommandParameter: SettingsSection target
            } ||
            DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        _pendingSection = target;
        if (_sectionPhase == SectionTransitionPhase.FadingOut)
            return;
        if (_sectionPhase == SectionTransitionPhase.None &&
            target == viewModel.SelectedSection)
        {
            return;
        }

        BeginSectionPhase(SectionTransitionPhase.FadingOut);
        eventArgs.Handled = true;
    }

    private void OnTextBoxTextChanged(
        object? sender,
        TextChangedEventArgs eventArgs)
    {
        if (eventArgs.Source is not TextBox textBox ||
            !textBox.IsFocused)
        {
            return;
        }

        textBox.Classes.Set("contentChanged", true);
        _textPulseDeadlines[textBox] =
            _interactionTime.Elapsed + TimeSpan.FromMilliseconds(140);
        RequestInteractionFrame();
    }

    private void OnAttachedToVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = true;
        _interactionTime.Start();
        RequestInteractionFrame();
    }

    private void OnDetachedFromVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = false;
        _interactionFrameQueued = false;
    }

    private void OnInteractionFrame(TimeSpan timestamp)
    {
        _interactionFrameQueued = false;
        if (!_isAttached)
            return;

        TimeSpan now = _interactionTime.Elapsed;
        AdvanceSectionTransition(now);

        foreach ((TextBox textBox, TimeSpan deadline) in
                 _textPulseDeadlines.ToArray())
        {
            if (now < deadline)
                continue;
            textBox.Classes.Set("contentChanged", false);
            _textPulseDeadlines.Remove(textBox);
        }

        RequestInteractionFrame();
    }

    private void AdvanceSectionTransition(TimeSpan now)
    {
        if (_sectionPhase == SectionTransitionPhase.None)
            return;

        double duration = _sectionPhase == SectionTransitionPhase.FadingOut
            ? 0.085
            : 0.110;
        if (DataContext is SettingsViewModel { ReducedMotion: true })
            duration *= 0.55;
        double progress = Math.Clamp(
            (now - _sectionPhaseStarted).TotalSeconds / duration,
            0,
            1);
        double eased = 1 - Math.Pow(1 - progress, 3);

        if (_sectionPhase == SectionTransitionPhase.FadingOut)
        {
            _sectionContent.Opacity =
                Lerp(_phaseStartOpacity, 0, eased);
            _sectionTransform.X =
                Lerp(_phaseStartOffset, -6, eased);
            if (progress < 1)
                return;

            if (DataContext is SettingsViewModel viewModel &&
                _pendingSection is { } target)
            {
                viewModel.SelectedSection = target;
            }
            _sectionScroller.Offset =
                new Vector(_sectionScroller.Offset.X, 0);
            _sectionContent.Opacity = 0;
            _sectionTransform.X = 6;
            BeginSectionPhase(SectionTransitionPhase.FadingIn);
            return;
        }

        _sectionContent.Opacity =
            Lerp(_phaseStartOpacity, 1, eased);
        _sectionTransform.X =
            Lerp(_phaseStartOffset, 0, eased);
        if (progress >= 1)
        {
            _sectionContent.Opacity = 1;
            _sectionTransform.X = 0;
            _sectionPhase = SectionTransitionPhase.None;
        }
    }

    private void BeginSectionPhase(SectionTransitionPhase phase)
    {
        _sectionPhase = phase;
        _sectionPhaseStarted = _interactionTime.Elapsed;
        _phaseStartOpacity = _sectionContent.Opacity;
        _phaseStartOffset = _sectionTransform.X;
        RequestInteractionFrame();
    }

    private void RequestInteractionFrame()
    {
        if (!_isAttached || _interactionFrameQueued ||
            (_sectionPhase == SectionTransitionPhase.None &&
             _textPulseDeadlines.Count == 0))
        {
            return;
        }

        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        _interactionFrameQueued = true;
        topLevel.RequestAnimationFrame(OnInteractionFrame);
    }

    private static double Lerp(double start, double end, double amount) =>
        start + (end - start) * amount;

    private enum SectionTransitionPhase
    {
        None,
        FadingOut,
        FadingIn
    }
}
