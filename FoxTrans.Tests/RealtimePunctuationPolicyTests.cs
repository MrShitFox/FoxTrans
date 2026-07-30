using Xunit;

public sealed class RealtimePunctuationPolicyTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Hello.")]
    [InlineData("Hello!")]
    [InlineData("Really?")]
    [InlineData("Finished…")]
    [InlineData("Part one; part two")]
    [InlineData("Key: value")]
    [InlineData("Готово!")]
    [InlineData("(\"Done.\")")]
    [InlineData("完了。")]
    [InlineData("هل انتهى؟")]
    [InlineData("What?!")]
    [InlineData("Yes!!!")]
    [InlineData("Fullwidth！")]
    public void ExplicitStrongTerminatorsAreStrong(string current) =>
        Assert.Equal(
            RealtimePunctuationKind.Strong,
            RealtimePunctuationPolicy.ClassifyNewTrailingPunctuation("", current));

    [Theory]
    [InlineData("Hello,")]
    [InlineData("well ")]
    [InlineData("maybe -")]
    [InlineData("maybe —")]
    [InlineData("مرحبا،")]
    [InlineData("項目、")]
    public void ExplicitWeakBoundariesAreWeak(string current) =>
        Assert.Equal(
            RealtimePunctuationKind.Weak,
            RealtimePunctuationPolicy.ClassifyNewTrailingPunctuation("", current));

    [Theory]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("«")]
    [InlineData("»")]
    [InlineData("plain lexical ending")]
    public void BracketsQuotesAndLexicalEndingsAreNone(string current) =>
        Assert.Equal(
            RealtimePunctuationKind.None,
            RealtimePunctuationPolicy.ClassifyNewTrailingPunctuation("", current));

    [Fact]
    public void ClassificationIsChangeAwareAndTextElementSafe()
    {
        Assert.Equal(
            RealtimePunctuationKind.Strong,
            Classify("Hello", "Hello."));
        Assert.Equal(
            RealtimePunctuationKind.Strong,
            Classify("Hello.", "Hello!"));
        Assert.Equal(
            RealtimePunctuationKind.None,
            Classify("Hello.", "Hello. again"));
        Assert.Equal(
            RealtimePunctuationKind.Strong,
            Classify("Готово", "Готово!\")]}"));
        Assert.Equal(
            RealtimePunctuationKind.None,
            Classify("Он сказал привет", "Он сказал привет\""));
        Assert.Equal(
            RealtimePunctuationKind.Strong,
            Classify("👩‍💻 e\u0301", "👩‍💻 e\u0301."));
        Assert.Equal(
            RealtimePunctuationKind.None,
            Classify("👩‍💻 e\u0301", "👩‍💻 e\u0301"));
    }

    [Theory]
    [InlineData("Hello,")]
    [InlineData("well ")]
    [InlineData("maybe -")]
    [InlineData("مرحبا،")]
    [InlineData("項目、")]
    public void WeakPunctuationDoesNotSelectPunctuationDecision(string source)
    {
        RealtimeSchedulingDecision decision = Decide(
            source,
            minimumChangedWords: 99,
            now: Start.AddMilliseconds(100));
        Assert.Equal(RealtimeSchedulingDecision.Wait, decision);
    }

    [Fact]
    public void SchedulingOrderAroundPunctuationIsUnchanged()
    {
        Assert.Equal(
            RealtimeSchedulingDecision.Wait,
            Decide("Hello.", 99, Start.AddMilliseconds(99)));
        Assert.Equal(
            RealtimeSchedulingDecision.MaximumInterval,
            Decide("Hello.", 99, Start.AddMilliseconds(500)));
        Assert.Equal(
            RealtimeSchedulingDecision.Punctuation,
            Decide("Hello.", 99, Start.AddMilliseconds(100)));
        Assert.Equal(
            RealtimeSchedulingDecision.MinimumChangedWords,
            Decide("one two,", 2, Start.AddMilliseconds(100)));
    }

    private static RealtimePunctuationKind Classify(string previous, string current) =>
        RealtimePunctuationPolicy.ClassifyNewTrailingPunctuation(previous, current);

    private static RealtimeSchedulingDecision Decide(
        string source,
        int minimumChangedWords,
        DateTimeOffset now) =>
        RealtimeTranslationPolicy.Decide(
            new TranslationCandidate(
                1,
                1,
                1,
                source,
                false,
                false,
                1,
                80,
                Start,
                now),
            null,
            null,
            now,
            new ResolvedRealtimeSettings(
                100,
                500,
                minimumChangedWords,
                1000,
                600));
}
