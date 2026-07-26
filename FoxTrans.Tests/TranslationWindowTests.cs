using Xunit;

public sealed class TranslationWindowTests
{
    [Theory]
    [InlineData("", 10)]
    [InlineData("short text", 20)]
    [InlineData("exactly ten", 11)]
    public void TextAtOrBelowLimitIsUnchanged(string text, int limit)
    {
        BoundedTextWindow result = TranslationWindows.Build(text, limit);
        Assert.Equal(text, result.Text);
        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void ExactTextElementLimitIsUnchanged()
    {
        BoundedTextWindow result = TranslationWindows.Build("1234567890", 10);
        Assert.Equal("1234567890", result.Text);
        Assert.False(result.WasTruncated);
    }

    [Fact]
    public void LongTextKeepsNewestEndingAndNeverExceedsLimit()
    {
        BoundedTextWindow result = TranslationWindows.Build(
            "old old old old old newest ending",
            16);
        Assert.True(result.WasTruncated);
        Assert.EndsWith("newest ending", result.Text);
        Assert.True(TranslationWindows.TextElementCount(result.Text) <= 16);
    }

    [Fact]
    public void SentenceBoundaryIsPreferred()
    {
        BoundedTextWindow result = TranslationWindows.Build(
            "Old sentence. Newest words here",
            20);
        Assert.Equal("…Newest words here", result.Text);
    }

    [Fact]
    public void ClausePunctuationBoundaryIsPreferred()
    {
        BoundedTextWindow result = TranslationWindows.Build(
            "old material; newest ending",
            16);
        Assert.Equal("…newest ending", result.Text);
    }

    [Fact]
    public void WordBoundaryIsPreferredWhenNoPunctuationExists()
    {
        BoundedTextWindow result = TranslationWindows.Build(
            "abcdefgh old newest ending",
            15);
        Assert.Equal("…newest ending", result.Text);
    }

    [Fact]
    public void UnicodeTextElementsAreNeverSplit()
    {
        string family = "👨‍👩‍👧‍👦";
        string combining = "e\u0301";
        BoundedTextWindow result = TranslationWindows.Build(
            "old" + family + combining + "新しい終わり",
            6);
        Assert.DoesNotContain("\uFFFD", result.Text);
        Assert.False(char.IsLowSurrogate(result.Text[1]));
        Assert.True(TranslationWindows.TextElementCount(result.Text) <= 6);
    }

    [Fact]
    public void ContinuousGrowthSlidesWindowForward()
    {
        BoundedTextWindow first =
            TranslationWindows.Build("one two three four five", 14);
        BoundedTextWindow second =
            TranslationWindows.Build("one two three four five six seven", 14);
        Assert.NotEqual(first.Text, second.Text);
        Assert.EndsWith("six seven", second.Text);
        Assert.DoesNotContain("one", second.Text);
    }

    [Fact]
    public void TrackerCandidatesContainCompleteCurrentWindowNotDelta()
    {
        DateTimeOffset time = DateTimeOffset.UnixEpoch;
        UtteranceTransition first = UtteranceTracking.ReducePartial(
            LogicalUtteranceState.Initial,
            new StreamingPartialTranscript(1, "I want to test this", 80),
            time,
            100);
        UtteranceTransition second = UtteranceTracking.ReducePartial(
            first.State,
            new StreamingPartialTranscript(
                2,
                "I want to test this because translation updates",
                160),
            time.AddMilliseconds(100),
            100);
        Assert.Equal(
            "I want to test this because translation updates",
            second.Candidate!.SourceText);
        Assert.StartsWith(first.Candidate!.SourceText, second.Candidate.SourceText);
    }
}
