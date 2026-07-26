# FoxTrans

FoxTrans is a lightweight voice translator for VRChat. It captures microphone audio, filters it with WebRTC VAD, sends translated text to VRChat over OSC, and supports both direct audio translation and a classic Whisper-style transcription followed by text translation.

## Requirements

- Windows x64
- .NET SDK 10.0.302 or a later .NET 10 feature-band SDK (selected through `global.json`)
- Visual Studio is not required. VS Code with C# Dev Kit works directly with `FoxTrans.slnx`.

## Build and publish

Clone the repository and run the single canonical publish command from the repository root:

```powershell
git clone https://github.com/MrShitFox/FoxTrans.git
cd FoxTrans
dotnet publish -c Release
```

The result is `FoxTrans\bin\Release\net10.0\win-x64\publish\FoxTrans.exe`.

It is a self-contained Windows x64 single-file executable: the .NET runtime and managed/native dependencies are bundled, so a target computer does not need a separately installed .NET Runtime.

In VS Code, the shared **Build** and **Publish Release** tasks run `dotnet build` and the same canonical publish command respectively.

## First run and configuration

Run `FoxTrans.exe` once. It creates `config.jsonc` and `foxtrans.schema.json` in its current working directory and exits. JSON comments and trailing commas are supported. Configure the key through an environment reference such as `env:OPENROUTER_API_KEY`, enable OSC in VRChat (`Options -> OSC -> Enable`), then run the executable again.

`config.jsonc` is local configuration and is intentionally ignored by Git because it can contain an API key. Do not commit it. Existing legacy `config.json` files are safely migrated to `config.jsonc` and retained as `config.legacy.json`; the program exits after migration for review.

```jsonc
{
  "$schema": "./foxtrans.schema.json",
  "version": 1,
  "audio": { "device": "default" },
  "pipeline": {
    "vad": { "type": "webrtc", "preset": "balanced" },
    "speech": {
      "type": "openai-chat-audio",
      "baseUrl": "https://openrouter.ai/api/v1",
      "apiKey": "env:OPENROUTER_API_KEY",
      "model": "google/gemini-2.5-flash",
      "prompt": "Translate this audio to English. Reply only with the translated text."
    }
  },
  "outputs": [{ "type": "vrchat-osc" }]
}
```

The classic Whisper pipeline in `examples/config.whisper.jsonc` is implemented. Its transcription endpoint can be local or remote as long as it provides the OpenAI-compatible `/audio/transcriptions` API; translation uses a separate OpenAI-compatible `/chat/completions` endpoint. The two providers may use different endpoints and API keys (or no key for a local unauthenticated transcription server).

`examples/config.voxtral.jsonc` remains valid configuration for the planned Voxtral realtime pipeline, but that pipeline is not executable yet. FoxTrans does not claim compatibility with servers that have not been tested.
