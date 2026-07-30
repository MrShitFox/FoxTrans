public sealed record AudioInputDevice(
    int DeviceNumber,
    string DisplayName,
    string? NativeId = null,
    bool IsDefault = false);

public sealed record ResolvedAudioInput(
    int DeviceNumber,
    string DisplayName,
    AudioFormat Format,
    string? NativeId = null);

public interface IAudioInputDeviceCatalogue
{
    IReadOnlyList<AudioInputDevice> GetInputs();
}

public interface IAudioCapture : IAudioSource, IDisposable;

public interface IAudioCaptureFactory
{
    IAudioCapture Create(ResolvedAudioInput input);
}

public sealed class NativeAudioInputDeviceCatalogue : IAudioInputDeviceCatalogue
{
    public IReadOnlyList<AudioInputDevice> GetInputs() =>
        NativeAudioInterop.GetInputs();
}

public sealed class AudioDeviceSelectionException(string message) : Exception(message);

public static class AudioDeviceSelection
{
    public static ResolvedAudioInput Resolve(
        string? selection,
        IReadOnlyList<AudioInputDevice> devices,
        AudioFormat format)
    {
        if (devices.Count == 0)
            throw new AudioDeviceSelectionException("No microphone input devices are available.");

        string value = string.IsNullOrWhiteSpace(selection) ? "default" : selection.Trim();
        if (string.Equals(value, "default", StringComparison.OrdinalIgnoreCase))
            return ResolveDevice(
                devices.FirstOrDefault(static device => device.IsDefault) ?? devices[0],
                format);

        if (int.TryParse(value, out int index))
        {
            AudioInputDevice? indexed = devices.FirstOrDefault(device => device.DeviceNumber == index);
            if (indexed is null)
                throw new AudioDeviceSelectionException(
                    $"Microphone index {index} is invalid. Available indices: {string.Join(", ", devices.Select(device => device.DeviceNumber))}.");
            return ResolveDevice(indexed, format);
        }

        AudioInputDevice[] exact = devices
            .Where(device => string.Equals(device.DisplayName, value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exact.Length == 1)
            return ResolveDevice(exact[0], format);
        if (exact.Length > 1)
            throw Ambiguous(value, exact);

        AudioInputDevice[] substring = devices
            .Where(device => device.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return substring.Length switch
        {
            1 => ResolveDevice(substring[0], format),
            > 1 => throw Ambiguous(value, substring),
            _ => throw new AudioDeviceSelectionException(
                $"No microphone matches '{value}'. Run '{FoxTransExecutableNames.Cli} devices' to list inputs.")
        };
    }

    public static string FormatList(IReadOnlyList<AudioInputDevice> devices)
    {
        if (devices.Count == 0)
            return "Input devices:\n\n  No microphone input devices are available.";
        return "Input devices:\n\n" +
            string.Join(Environment.NewLine, devices.Select(device =>
                $"  {device.DeviceNumber}  {device.DisplayName}")) +
            "\n\nUse in config:\n" +
            "\"audio\": { \"device\": \"0\" }\n" +
            "or:\n" +
            $"\"audio\": {{ \"device\": \"{Escape(devices[0].DisplayName)}\" }}";
    }

    private static ResolvedAudioInput ResolveDevice(AudioInputDevice device, AudioFormat format) =>
        new(device.DeviceNumber, device.DisplayName, format, device.NativeId);

    private static AudioDeviceSelectionException Ambiguous(
        string selection,
        IReadOnlyList<AudioInputDevice> matches) =>
        new($"Microphone selection '{selection}' is ambiguous. Matches: " +
            string.Join(", ", matches.Select(device => $"{device.DeviceNumber} {device.DisplayName}")) + ".");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public static class FoxTransExecutableNames
{
    public static string Desktop => OperatingSystem.IsWindows()
        ? "FoxTrans.exe"
        : "FoxTrans";

    public static string Cli => OperatingSystem.IsWindows()
        ? "FoxTrans.Cli.exe"
        : "FoxTrans.Cli";
}
