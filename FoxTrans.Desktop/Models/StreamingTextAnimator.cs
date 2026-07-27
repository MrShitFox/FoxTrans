using System.Globalization;

namespace FoxTrans.Desktop.Models;

public sealed class StreamingTextAnimator
{
    public const double NormalElementsPerSecond = 42;
    public const double CatchUpElementsPerSecond = 140;
    public static readonly TimeSpan ReplacementCrossfade =
        TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan FinalSettlementBound =
        TimeSpan.FromMilliseconds(450);

    private string[] _target = [];
    private string[] _displayed = [];
    private DateTimeOffset? _lastAdvanced;
    private DateTimeOffset? _crossfadeStarted;
    private DateTimeOffset _targetUpdatedAt;
    private double _revealBudget;
    private bool _isFinal;

    public string TargetText { get; private set; } = "";
    public string DisplayedText { get; private set; } = "";
    public double Opacity { get; private set; } = 1;
    public bool IsCaughtUp => _displayed.Length == _target.Length &&
        string.Equals(DisplayedText, TargetText, StringComparison.Ordinal);
    public bool IsCatchUpMode => _target.Length - _displayed.Length > 18;
    public int PendingElementCount =>
        Math.Max(0, _target.Length - _displayed.Length);
    public int QueuedTargetCount => IsCaughtUp ? 0 : 1;

    public void SetTarget(
        string? text,
        bool isFinal,
        DateTimeOffset now)
    {
        text ??= "";
        if (string.Equals(text, TargetText, StringComparison.Ordinal))
        {
            _isFinal |= isFinal;
            return;
        }

        string[] next = TextElements(text);
        int common = LongestCommonPrefix(_target, next);
        int replacementSize = Math.Max(
            _target.Length - common,
            next.Length - common);
        bool largeReplacement =
            _target.Length > 0 &&
            _displayed.Length > 0 &&
            replacementSize >= 8 &&
            common < Math.Max(_target.Length, next.Length) / 3;

        TargetText = text;
        _target = next;
        _targetUpdatedAt = now;
        _isFinal = isFinal;
        _revealBudget = 0;
        _lastAdvanced ??= now;

        if (largeReplacement)
        {
            _displayed = [.. next];
            DisplayedText = text;
            Opacity = 0.35;
            _crossfadeStarted = now;
            return;
        }

        int retained = Math.Min(common, _displayed.Length);
        if (_displayed.Length != retained)
            _displayed = _displayed[..retained];
        DisplayedText = string.Concat(_displayed);
        Opacity = 1;
        _crossfadeStarted = null;
    }

    public void Advance(DateTimeOffset now, bool reducedMotion = false)
    {
        DateTimeOffset previous = _lastAdvanced ?? now;
        _lastAdvanced = now;
        TimeSpan elapsed = now >= previous ? now - previous : TimeSpan.Zero;

        if (_crossfadeStarted is { } crossfade)
        {
            if (reducedMotion)
            {
                Opacity = 1;
                _crossfadeStarted = null;
            }
            else
            {
                double progress = Math.Clamp(
                    (now - crossfade).TotalMilliseconds /
                    ReplacementCrossfade.TotalMilliseconds,
                    0,
                    1);
                Opacity = 0.35 + 0.65 * progress;
                if (progress >= 1)
                    _crossfadeStarted = null;
            }
            return;
        }

        int pending = PendingElementCount;
        if (pending == 0)
        {
            DisplayedText = TargetText;
            Opacity = 1;
            return;
        }

        if (_isFinal && now - _targetUpdatedAt >= FinalSettlementBound)
        {
            Reveal(pending);
            return;
        }

        if (reducedMotion)
        {
            Reveal(pending);
            return;
        }

        double rate = IsCatchUpMode
            ? CatchUpElementsPerSecond
            : NormalElementsPerSecond;
        if (_isFinal)
        {
            rate = Math.Max(
                rate,
                pending / FinalSettlementBound.TotalSeconds);
        }
        _revealBudget += elapsed.TotalSeconds * rate;
        int reveal = Math.Min(pending, (int)_revealBudget);
        if (reveal <= 0)
            return;
        _revealBudget -= reveal;
        Reveal(reveal);
    }

    private void Reveal(int count)
    {
        int nextLength = Math.Min(_target.Length, _displayed.Length + count);
        _displayed = _target[..nextLength];
        DisplayedText = nextLength == _target.Length
            ? TargetText
            : string.Concat(_displayed);
        Opacity = 1;
    }

    public static int LongestStablePrefix(string oldText, string newText) =>
        LongestCommonPrefix(TextElements(oldText), TextElements(newText));

    public static string[] TextElements(string text)
    {
        var elements = new List<string>();
        TextElementEnumerator enumerator =
            StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            elements.Add(enumerator.GetTextElement());
        return elements.ToArray();
    }

    private static int LongestCommonPrefix(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        int common = 0;
        while (common < left.Count &&
               common < right.Count &&
               string.Equals(left[common], right[common], StringComparison.Ordinal))
        {
            common++;
        }
        return common;
    }
}
