public static class DiagnosticText
{
    public static string Safe(string value, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        string safe = value.Replace('\r', ' ').Replace('\n', ' ');
        return safe.Length <= maximumLength
            ? safe
            : safe[..maximumLength] + "…";
    }
}
