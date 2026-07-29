using System.Text.Json.Serialization;

namespace FoxTrans.Desktop.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(DesktopPreferences))]
internal sealed partial class DesktopPreferencesJsonContext :
    JsonSerializerContext;
