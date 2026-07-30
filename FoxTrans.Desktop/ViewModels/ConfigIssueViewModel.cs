namespace FoxTrans.Desktop.ViewModels;

public sealed record ConfigIssueViewModel(
    string Path,
    string Message,
    string? Suggestion);
