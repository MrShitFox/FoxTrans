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

## Command line

Running without a command is the same as `run`:

```powershell
FoxTrans.exe
FoxTrans.exe run
FoxTrans.exe check
FoxTrans.exe devices
FoxTrans.exe run --dry-run
FoxTrans.exe check --config C:\Configs\foxtrans.jsonc
FoxTrans.exe --help
```

`--config PATH` loads that exact file. Relative paths start at the current
working directory. An explicit missing path or directory is an error; FoxTrans
does not fall back to another configuration. First-run creation and legacy
`config.json` migration occur only for the default working-directory
`config.jsonc` workflow. Relative schema files belong beside the selected config.

Exit codes are stable: `0` success or Ctrl+C, `1` runtime/provider failure, `2`
command-line or configuration error, and `3` a dependency readiness failure.
Normal errors are concise and do not print stack traces.

## Microphone selection

List inputs without opening or recording from them:

```powershell
FoxTrans.exe devices
```

Choose the default input, a numeric index encoded as text, an exact
case-insensitive name, or a unique case-insensitive substring:

```jsonc
"audio": { "device": "default" }
"audio": { "device": "1" }
"audio": { "device": "Microphone (USB Audio Device)" }
```

Ambiguous or unknown names and invalid indices fail visibly. FoxTrans resolves
the selection before constructing NAudio and explicitly sets its device number.
If the device disappears or cannot open at mono 16 kHz PCM16LE, startup reports a
device-focused error.

## Dry run and readiness checks

`run --dry-run` parses and validates configuration, resolves environment-backed
secrets, the pipeline, microphone, endpoints, output addresses, and prints the
execution plan. Secret values are never printed. It does not construct the
microphone or UDP output, call HTTP, open a WebSocket, or make an inference
request.

`check` performs the same local validation without recording. Realtime plans
also call the documented unauthenticated Voxtral `GET /health` and validate
readiness, busy state, PCM16LE/16000 Hz/mono capabilities, delay, and the
single-stream lease. Busy and unreachable are reported distinctly.
OpenAI-compatible direct, transcription, and chat endpoints are syntax-checked,
but connectivity is honestly reported as not probed because they have no
standardized non-inference health route. No paid request, audio, WebSocket, or
OSC message is sent.

## Realtime recovery

Normal speech pauses keep the same Voxtral WebSocket. After a transient
network/server failure, FoxTrans keeps the microphone open and creates fresh
Voxtral sessions with deterministic delays of 1, 2, 4, 8, then 10 seconds.
Each successful reconnect starts a new connection generation, transcript epoch,
and logical utterance; translations from the old epoch are cancelled or
suppressed, and typing is forced off until new speech arrives.

Reconnect is not lossless. While no connection is ready, live microphone audio
is drained and counted as an approximate visible gap. It is not retained,
replayed, or presented as current speech after reconnect. A connection attempt
does not receive microphone audio until its validated `session.created`; the
microphone starts only after that boundary on the first connection and remains
open across reconnects.

Capture callbacks are normalized to 20 ms mono PCM16LE frames (640 bytes at
16000 Hz), so device callback size does not change queue capacity. The active
route is bounded to exactly five seconds (250 normalized frames, 160000 PCM
bytes). If a full route cannot make progress within the short bounded write wait,
the failure reports buffered time and bytes rather than dropping audio silently.
Frames routed for a failed attempt but not confirmed sent are included in the
gap.

The first health/session failure remains a clear startup failure. Automatic
recovery begins only after one session reached `session.created`. Authorization,
configuration, audio-format, sequence, malformed protocol, unsupported-delay,
and incompatible-session failures do not retry. Ctrl+C interrupts an active
session, health request, handshake, or backoff and stops the microphone and
typing.

Known limitations: outage audio cannot be recovered; there is no disk or memory
replay spool, no parallel Voxtral session, and no standardized non-inference
connectivity probe for OpenAI-compatible providers.
