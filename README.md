# FoxTrans

FoxTrans is a lightweight voice translator for VRChat. It supports direct audio
translation and classic Whisper-style transcription followed by text translation.
The beta branch also runs live VoxtralFox transcription and text translation
through one persistent realtime session.

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

## VoxtralFox realtime translation (beta)

The complete VoxtralFox realtime translation path is executable on `beta`.
Configure the server, translation provider, realtime policy, and outputs as shown in
[`examples/config.voxtral.jsonc`](examples/config.voxtral.jsonc):

```jsonc
"speech": {
  "type": "voxtral-fox",
  "baseUrl": "http://192.168.2.136:8080",
  "apiKey": "env:VOXTRAL_API_KEY",
  "delayMs": 240
}
```

FoxTrans checks the unauthenticated `/health` endpoint before opening the
microphone, then maintains one authenticated WebSocket session and continuously
sends mono 16 kHz PCM16LE through speech and silence. No VAD is involved.
Translations update while you speak. When the cumulative transcript stops
changing for the configured interval, FoxTrans settles a client-side logical
utterance; later speech starts another logical utterance without reconnecting,
ending audio, or resetting the server transcript.

Every translation request contains the complete newest bounded source window,
never a token or character delta. During long continuous speech that window
slides forward so old source context leaves from the beginning while the newest
speech remains. Translation requests use a latest-wins scheduler with one active
request and at most one pending newest candidate. Stale results are discarded
before outputs. VRChat OSC independently keeps the newest 144 user-perceived
characters of a translation; console and other future outputs retain the full
translation.

Realtime presets provide these initial beta defaults:

| Preset | Minimum interval | Maximum interval | Changed words | New utterance after | Source window |
| --- | ---: | ---: | ---: | ---: | ---: |
| `responsive` | 250 ms | 650 ms | 2 | 1000 ms | 600 |
| `balanced` | 350 ms | 900 ms | 3 | 1400 ms | 800 |
| `economical` | 650 ms | 1500 ms | 5 | 1800 ms | 1000 |

Advanced per-field overrides are `minimumIntervalMs`, `maximumIntervalMs`,
`minimumChangedWords`, `newUtteranceAfterMs`, and `maxSourceCharacters`.
Explicit values override the selected preset. See the example configuration for
placement and environment-backed key references.

An unexpected Voxtral transport disconnect is still fatal. Automatic reconnect
and audio replay remain deliberately deferred; ordinary speech pauses never
rotate the connection.
