using CommunityToolkit.Mvvm.ComponentModel;
using FoxTrans.Desktop.Models;

namespace FoxTrans.Desktop.ViewModels;

public sealed class StreamingTextViewModel : ObservableObject
{
    public static readonly TimeSpan UtteranceExitDuration =
        TimeSpan.FromMilliseconds(220);

    private readonly StreamingTextAnimator _animator = new();
    private string _displayedText = "";
    private string _stableText = "";
    private string _animatedSuffixText = "";
    private double _opacity = 1;
    private double _suffixOpacity = 1;
    private double _suffixOffsetY;
    private double _exitOffsetY;
    private DateTimeOffset? _exitStarted;
    private string? _pendingText;
    private bool _pendingFinal;

    public string DisplayedText
    {
        get => _displayedText;
        private set => SetProperty(ref _displayedText, value);
    }

    public double Opacity
    {
        get => _opacity;
        private set => SetProperty(ref _opacity, value);
    }

    public string StableText
    {
        get => _stableText;
        private set => SetProperty(ref _stableText, value);
    }

    public string AnimatedSuffixText
    {
        get => _animatedSuffixText;
        private set => SetProperty(ref _animatedSuffixText, value);
    }

    public double SuffixOpacity
    {
        get => _suffixOpacity;
        private set => SetProperty(ref _suffixOpacity, value);
    }

    public double SuffixOffsetY
    {
        get => _suffixOffsetY;
        private set => SetProperty(ref _suffixOffsetY, value);
    }

    public double ExitOffsetY
    {
        get => _exitOffsetY;
        private set => SetProperty(ref _exitOffsetY, value);
    }

    public bool IsExiting => _exitStarted is not null;
    public bool IsCaughtUp => _animator.IsCaughtUp;
    public int PendingElementCount => _animator.PendingElementCount;

    public void SetTarget(string? text, bool isFinal, DateTimeOffset now)
    {
        if (IsExiting)
        {
            _pendingText = text ?? "";
            _pendingFinal = isFinal;
            return;
        }
        _animator.SetTarget(text, isFinal, now);
        Apply();
    }

    public void BeginNewUtterance(DateTimeOffset now)
    {
        if (IsExiting)
            return;
        _pendingText = "";
        _pendingFinal = false;
        if (DisplayedText.Length == 0)
        {
            _animator.Reset();
            Apply();
            return;
        }
        _exitStarted = now;
    }

    public void Advance(DateTimeOffset now, bool reducedMotion)
    {
        if (_exitStarted is { } exit)
        {
            double progress = reducedMotion
                ? 1
                : Math.Clamp(
                    (now - exit).TotalMilliseconds /
                    UtteranceExitDuration.TotalMilliseconds,
                    0,
                    1);
            double eased = progress * progress;
            Opacity = 1 - eased;
            ExitOffsetY = 3 * eased;
            if (progress < 1)
                return;

            string pending = _pendingText ?? "";
            bool final = _pendingFinal;
            _exitStarted = null;
            _pendingText = null;
            _pendingFinal = false;
            _animator.Reset();
            _animator.SetTarget(pending, final, now);
            ExitOffsetY = 0;
        }
        _animator.Advance(now, reducedMotion);
        Apply();
    }

    private void Apply()
    {
        DisplayedText = _animator.DisplayedText;
        StableText = _animator.StableText;
        AnimatedSuffixText = _animator.AnimatedSuffixText;
        SuffixOpacity = _animator.SuffixOpacity;
        SuffixOffsetY = _animator.SuffixOffsetY;
        if (!IsExiting)
            Opacity = _animator.Opacity;
        OnPropertyChanged(nameof(IsCaughtUp));
        OnPropertyChanged(nameof(PendingElementCount));
        OnPropertyChanged(nameof(IsExiting));
    }
}
