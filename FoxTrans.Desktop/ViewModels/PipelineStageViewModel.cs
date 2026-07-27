using CommunityToolkit.Mvvm.ComponentModel;

namespace FoxTrans.Desktop.ViewModels;

public sealed class PipelineStageViewModel : ObservableObject
{
    private string _status = "Configured";
    private string _detail = "";
    private bool _isActive;
    private bool _isSuccess;
    private bool _isWarning;
    private bool _isError;
    private double _flowOffset;

    public PipelineStageViewModel(
        PipelineNodeDefinition definition,
        PipelineEdgeDefinition? incoming)
    {
        Id = definition.Id;
        Title = definition.Title;
        Subtitle = definition.Subtitle;
        Settings = definition.Settings;
        HasIncoming = incoming is not null;
        IncomingLabel = incoming is null
            ? ""
            : DataLabel(incoming.DataKind);
    }

    public PipelineNodeId Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public IReadOnlyList<PipelineSettingView> Settings { get; }
    public bool HasIncoming { get; }
    public string IncomingLabel { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public bool IsSuccess
    {
        get => _isSuccess;
        private set => SetProperty(ref _isSuccess, value);
    }

    public bool IsWarning
    {
        get => _isWarning;
        private set => SetProperty(ref _isWarning, value);
    }

    public bool IsError
    {
        get => _isError;
        private set => SetProperty(ref _isError, value);
    }

    public double FlowOffset
    {
        get => _flowOffset;
        private set => SetProperty(ref _flowOffset, value);
    }

    public void Apply(
        PipelineNodeState state,
        PipelineEdgeState? incoming,
        DateTimeOffset now,
        double animationSeconds,
        bool reducedMotion)
    {
        Status = StatusText(state.Status);
        Detail = state.LastError ?? state.Detail ?? state.WorkIdentity ?? "";
        IsActive = state.Status is
            PipelineNodeStatus.Starting or
            PipelineNodeStatus.Listening or
            PipelineNodeStatus.Receiving or
            PipelineNodeStatus.Recording or
            PipelineNodeStatus.Active or
            PipelineNodeStatus.Publishing or
            PipelineNodeStatus.Reconnecting;
        IsSuccess = state.Status is
            PipelineNodeStatus.Ready or
            PipelineNodeStatus.Settled;
        IsWarning = state.Status is
            PipelineNodeStatus.Warning or
            PipelineNodeStatus.Waiting or
            PipelineNodeStatus.Buffering or
            PipelineNodeStatus.Queued or
            PipelineNodeStatus.TimedOut;
        IsError = state.Status is
            PipelineNodeStatus.Error or
            PipelineNodeStatus.Quarantined;

        if (incoming is null)
            return;
        bool recentActivity = incoming.LastActivity is { } observed &&
            now >= observed &&
            now - observed <= TimeSpan.FromMilliseconds(900);
        FlowOffset = reducedMotion
            ? recentActivity ? 25 : 0
            : IsActive || recentActivity
                ? animationSeconds % 1 * 50
                : 0;
    }

    private static string DataLabel(PipelineDataKind kind) => kind switch
    {
        PipelineDataKind.PcmAudio => "audio",
        PipelineDataKind.SpeechSegment => "segment",
        PipelineDataKind.Transcript => "text",
        PipelineDataKind.Translation => "translation",
        PipelineDataKind.OutputUpdate => "output",
        PipelineDataKind.TypingControl => "typing",
        _ => "data"
    };

    private static string StatusText(PipelineNodeStatus status) => status switch
    {
        PipelineNodeStatus.Receiving => "Live",
        PipelineNodeStatus.Recording => "Speech",
        PipelineNodeStatus.Active => "Processing",
        PipelineNodeStatus.Publishing => "Sending",
        PipelineNodeStatus.Ready => "Ready",
        PipelineNodeStatus.Settled => "Complete",
        PipelineNodeStatus.Reconnecting => "Reconnecting",
        PipelineNodeStatus.Quarantined => "Unavailable",
        _ => status.ToString()
    };
}
