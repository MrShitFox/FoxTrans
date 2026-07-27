using System.Collections.ObjectModel;
using FoxTrans.Desktop.Services;

namespace FoxTrans.Desktop.ViewModels;

public sealed class PipelinePageViewModel : IPipelineStagesViewModel
{
    public PipelinePageViewModel(
        DesktopBootstrapResult bootstrap,
        PipelineViewDefinition definition,
        System.Windows.Input.ICommand openFolderCommand)
    {
        var redactor = new DesktopSecretRedactor(
            bootstrap.Plan,
            bootstrap.Config);
        Title = definition.Title;
        ConfigPath = bootstrap.ConfigPath;
        HasProblems = bootstrap.Issues.Count > 0;
        Problems = new ObservableCollection<ConfigIssueViewModel>(
            bootstrap.Issues.Select(issue =>
                new ConfigIssueViewModel(
                    issue.Path,
                    redactor.Redact(issue.Message),
                    issue.Suggestion is null
                        ? null
                        : redactor.Redact(issue.Suggestion))));
        Warnings = new ObservableCollection<string>(
            bootstrap.Warnings.Select(redactor.Redact));
        Stages = new ObservableCollection<PipelineStageViewModel>(
            definition.Nodes.Select(node => new PipelineStageViewModel(
                node,
                definition.Edges.FirstOrDefault(edge => edge.To == node.Id))));
        EffectiveSettings = new ObservableCollection<EffectiveSettingViewModel>(
            definition.Nodes.SelectMany(node => node.Settings.Select(setting =>
                new EffectiveSettingViewModel(
                    node.Title,
                    setting.Name,
                    setting.Value))));
        OpenFolderCommand = openFolderCommand;
    }

    public string Title { get; }
    public string ConfigPath { get; }
    public bool HasProblems { get; }
    public bool IsValid => !HasProblems;
    public ObservableCollection<ConfigIssueViewModel> Problems { get; }
    public ObservableCollection<string> Warnings { get; }
    public ObservableCollection<PipelineStageViewModel> Stages { get; }
    public ObservableCollection<EffectiveSettingViewModel> EffectiveSettings { get; }
    public System.Windows.Input.ICommand OpenFolderCommand { get; }
}

public sealed record EffectiveSettingViewModel(
    string Stage,
    string Name,
    string Value);

public sealed record ConfigIssueViewModel(
    string Path,
    string Message,
    string? Suggestion);
