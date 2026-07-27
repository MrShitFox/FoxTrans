using FoxTrans.Desktop.Models;
using Xunit;

public sealed class StreamingTextAnimatorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void SimpleSuffixGrowthRetainsDisplayedPrefix()
    {
        var animator = Settled("hello");
        animator.SetTarget("hello world", false, Start + TimeSpan.FromSeconds(1));

        Assert.Equal("hello", animator.DisplayedText);
        animator.Advance(Start + TimeSpan.FromSeconds(2));
        Assert.Equal("hello world", animator.DisplayedText);
    }

    [Fact]
    public void PartialCorrectionKeepsOnlyStableGraphemePrefix()
    {
        var animator = Settled("The quik fox");
        DateTimeOffset now = Start + TimeSpan.FromSeconds(1);
        animator.SetTarget("The quick fox", false, now);

        Assert.Equal("The qui", animator.DisplayedText);
        animator.Advance(now + TimeSpan.FromSeconds(1));
        Assert.Equal("The quick fox", animator.DisplayedText);
    }

    [Fact]
    public void CompleteReplacementUsesOneShortCrossfade()
    {
        var animator = Settled("completely old sentence");
        DateTimeOffset now = Start + TimeSpan.FromSeconds(1);
        animator.SetTarget("entirely new statement", false, now);

        Assert.Equal("entirely new statement", animator.DisplayedText);
        Assert.InRange(animator.Opacity, 0.34, 0.36);
        animator.Advance(now + StreamingTextAnimator.ReplacementCrossfade);
        Assert.Equal(1, animator.Opacity);
        Assert.True(animator.IsCaughtUp);
    }

    [Theory]
    [InlineData("Привет, мир")]
    [InlineData("مرحبا بالعالم")]
    [InlineData("你好，世界")]
    [InlineData("e\u0301cole")]
    [InlineData("👨‍👩‍👧‍👦 family")]
    public void UnicodeTextConvergesWithoutSplittingTextElements(string text)
    {
        var animator = new StreamingTextAnimator();
        animator.SetTarget(text, true, Start);
        animator.Advance(Start + TimeSpan.FromSeconds(1));

        Assert.Equal(text, animator.DisplayedText);
        Assert.True(animator.IsCaughtUp);
        Assert.DoesNotContain('\uFFFD', animator.DisplayedText);
    }

    [Fact]
    public void ZWJFamilyCountsAsOneStableElement()
    {
        const string family = "👨‍👩‍👧‍👦";
        Assert.Single(StreamingTextAnimator.TextElements(family));
        Assert.Equal(
            2,
            StreamingTextAnimator.LongestStablePrefix(
                family + " hello",
                family + " world"));
    }

    [Fact]
    public void RapidUpdatesCoalesceToOneLatestTarget()
    {
        var animator = new StreamingTextAnimator();
        for (int index = 0; index < 10_000; index++)
            animator.SetTarget($"newest {index}", false, Start);

        Assert.InRange(animator.QueuedTargetCount, 0, 1);
        animator.Advance(Start + TimeSpan.FromSeconds(2));
        Assert.Equal("newest 9999", animator.DisplayedText);
    }

    [Fact]
    public void CatchUpModeAcceleratesWhenPresenterFallsBehind()
    {
        var animator = new StreamingTextAnimator();
        animator.SetTarget(new string('x', 100), false, Start);
        Assert.True(animator.IsCatchUpMode);

        animator.Advance(Start + TimeSpan.FromMilliseconds(250));

        Assert.InRange(
            StreamingTextAnimator.TextElements(animator.DisplayedText).Length,
            30,
            40);
    }

    [Fact]
    public void FinalTextSettlesWithinItsBound()
    {
        var animator = new StreamingTextAnimator();
        string text = new('ж', 200);
        animator.SetTarget(text, true, Start);

        animator.Advance(Start + StreamingTextAnimator.FinalSettlementBound);

        Assert.Equal(text, animator.DisplayedText);
        Assert.True(animator.IsCaughtUp);
    }

    [Fact]
    public void ReducedMotionConvergesImmediatelyButKeepsCorrectText()
    {
        var animator = new StreamingTextAnimator();
        animator.SetTarget("latest model value", false, Start);

        animator.Advance(Start, reducedMotion: true);

        Assert.Equal("latest model value", animator.DisplayedText);
        Assert.True(animator.IsCaughtUp);
    }

    private static StreamingTextAnimator Settled(string text)
    {
        var animator = new StreamingTextAnimator();
        animator.SetTarget(text, true, Start);
        animator.Advance(Start + TimeSpan.FromSeconds(1));
        Assert.True(animator.IsCaughtUp);
        return animator;
    }
}
