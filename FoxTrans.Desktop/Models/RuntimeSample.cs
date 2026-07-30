namespace FoxTrans.Desktop.Models;

internal readonly record struct RuntimeSample(
    DesktopRuntimeSnapshot Snapshot,
    AudioVisualFrame? AudioFrame);
