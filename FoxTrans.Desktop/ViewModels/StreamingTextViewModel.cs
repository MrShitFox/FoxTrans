using CommunityToolkit.Mvvm.ComponentModel;
using FoxTrans.Desktop.Models;

namespace FoxTrans.Desktop.ViewModels;

public sealed class StreamingTextViewModel : ObservableObject
{
    private readonly StreamingTextAnimator _animator = new();
    private string _displayedText = "";
    private double _opacity = 1;

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

    public bool IsCaughtUp => _animator.IsCaughtUp;
    public int PendingElementCount => _animator.PendingElementCount;

    public void SetTarget(string? text, bool isFinal, DateTimeOffset now)
    {
        _animator.SetTarget(text, isFinal, now);
        Apply();
    }

    public void Advance(DateTimeOffset now, bool reducedMotion)
    {
        _animator.Advance(now, reducedMotion);
        Apply();
    }

    private void Apply()
    {
        DisplayedText = _animator.DisplayedText;
        Opacity = _animator.Opacity;
        OnPropertyChanged(nameof(IsCaughtUp));
        OnPropertyChanged(nameof(PendingElementCount));
    }
}
