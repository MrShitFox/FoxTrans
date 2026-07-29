# FoxTrans

FoxTrans is a GUI-first live voice translator for VRChat. The primary
`FoxTrans.exe` (Windows) or `FoxTrans` (Linux) opens an Avalonia desktop studio for direct audio translation,
classic Whisper-style transcription followed by text translation, and persistent
VoxtralFox realtime transcription. The same production pipelines remain available
for automation and diagnostics through `FoxTrans.Cli.exe` (Windows) or
`FoxTrans.Cli` (Linux).

## Requirements

- Windows x64 or Ubuntu 22.04+ x64 (and compatible desktop distributions)
- .NET SDK 10.0.302 or a later .NET 10 feature-band SDK (selected through `global.json`)
- CMake plus a C compiler: Visual Studio Build Tools on Windows or `build-essential cmake` on Ubuntu
- Visual Studio is not required. VS Code with C# Dev Kit works directly with `FoxTrans.slnx`.

## Desktop Live Studio

Launch the platform executable with no arguments. The desktop application opens
one continuous near-black Live surface; Windows uses the GUI subsystem without a
console window. Its custom title bar provides drag, double-click maximize/restore,
minimize, maximize, close, and resizable edges while retaining bounded graceful
runtime shutdown.

The compact identity at the top left comes from the resolved configuration:
application name, current pipeline, and current model or model pair. **Settings**
and the runtime-aware **Start/Stop** action sit at the top right. There is no
application navigation rail, dashboard, or permanent pipeline graph.

The central voice waveform is a compact, translucent rail rendered with standard
Avalonia primitives. Twelve rounded bars use RMS, peak, clipping, and the twelve
separate spectral bands already produced by the audio path. Speech changes bar
height with a fast attack and slower release. A visual-only automatic gain stage
tracks the microphone noise floor and active-speech reference, so quiet and loud
microphones converge to the same useful display range without changing recorded
audio. Processing uses a restrained sweep, success and error use one bounded
pulse, and the resting state stays static. It uses only standard Avalonia drawing
primitives and no visual assets.

Typed runtime state produces visibly different Idle, Listening, Speech,
Processing, Success, Error, and Stopping modes. The microphone test uses the real
configured input and the same feature path without making provider requests.
Audio analysis remains latest-only and allocation bounded: it does not modify or
retain PCM, queue visual history, or make the pipeline wait for the desktop.

Current recognition and translation appear beneath the waveform without cards.
Recognition is secondary and translation is brighter and larger. Audio LLM mode
hides the recognition section completely and rebalances the translation instead
of leaving an empty placeholder. Classic and Voxtral modes show recognition.
Unicode text keeps its stable grapheme prefix, reveals only the new suffix,
crossfades corrections, coalesces newer partials, accelerates when behind, and
converges within a bound. New logical utterances softly retire the old text before
the new value begins; combining sequences, surrogate pairs, and ZWJ emoji are
never split.

**Settings** opens a dimming right-side drawer over Live. Its internal sections
cover:

- **Pipeline**: Audio LLM, Whisper + LLM, or Voxtral + LLM, with only the
  relevant guided fields shown;
- **Audio**: device refresh, real microphone test, batch/direct VAD preset and
  optional timing overrides, or Voxtral realtime behavior;
- **Providers**: endpoints, models, language, request format, delay/realtime
  controls, prompts, and credentials for the selected mode;
- **Output**: all configured VRChat OSC outputs, including add, remove, reorder,
  address, enabled state, and typing indicator;
- **Appearance**: system, dark, or light theme plus reduced motion;
- **Advanced**: config path, raw config/folder access, version, and sanitized
  diagnostics.

Credentials may be stored as an `env:VARIABLE_NAME` reference or as a masked
inline value. Inline mode warns that the literal is written to the local file,
and an unchanged hidden inline credential is preserved. Credentials never appear
in the Live header or sanitized diagnostics.

**Save** validates through the existing typed configuration model, writes
deterministic canonical JSONC through a temporary file, keeps one
`config.jsonc.bak` before replacing an existing file, reloads and resolves the
result, and refreshes the Live header. Invalid fields remain local to the drawer
and the old file is untouched. While the runtime is active the action is
explicitly **Save & Restart**; FoxTrans never silently restarts an open
microphone.

Appearance, reduced motion, and window placement remain separate in the platform
local application-data folder (`%LOCALAPPDATA%\FoxTrans` on Windows and normally
`~/.local/share/FoxTrans` on Linux); UI-only preferences are not
added to the pipeline schema. Missing or invalid configuration keeps the shell
open and opens the editor, so ordinary setup and repair can be completed without
hand-editing JSON. Raw JSONC remains available as an advanced escape hatch.

## Secondary CLI Live Studio

`FoxTrans.Cli[.exe] run` renders the same resolved configuration as a compact,
view-only version of the Desktop Live Studio. The header identifies the active
mode and models, while the centre shows microphone activity, live status,
current recognition (when available), and current translation.

```text
 FOXTRANS CLI
 Whisper + LLM
 whisper-large-v3  gpt-4.1-mini

 ╭ LIVE ───────────────────────────────────────────────────────╮
 │ [############........]  MIC LEVEL -24 dB                    │
 │ Hearing you                                                  │
 │ Listening to the current phrase                              │
 ╰─────────────────────────────────────────────────────────────╯
 ╭ CURRENT RECOGNITION ────────────────────────────────────────╮
 │ Я проверяю классический режим перевода.                      │
 ╰─────────────────────────────────────────────────────────────╯
 ╭ CURRENT TRANSLATION ────────────────────────────────────────╮
 │ I am testing the classic translation mode.                   │
 ╰─────────────────────────────────────────────────────────────╯
```

The rich TUI is view-only. Configure FoxTrans in `config.jsonc`; there are no
runtime menus, keyboard commands, mouse controls, selection, scrolling, or
configuration editing. It displays no credentials, prompts, endpoint userinfo,
query strings, or fragments. The newest warning or error appears in a separate
read-only panel at the bottom of the screen.

`run` has one live interface: Live Studio. It uses an interactive,
non-redirected terminal and otherwise emits bounded timestamped plain-text
events for files, pipes, and CI. Live Studio requires at least 80 columns by
24 rows; a smaller terminal shows a resize message and begins rendering when
the window is enlarged. Ctrl+C remains the normal operating-system cancellation
path.

The rich UI supports standard Windows terminals and Linux ANSI terminals. It
uses lightweight Spectre.Console rendering only; no Desktop/Avalonia runtime is
included in the CLI. There are no replacement keyboard controls: the terminal
only visualizes execution.

## Build and publish

Clone the repository and build both distribution archives on the matching host:

```powershell
git clone https://github.com/MrShitFox/FoxTrans.git
cd FoxTrans
pwsh -NoProfile -File ./publish.ps1
```

On Linux, use:

```bash
bash ./publish.sh
```

Windows writes `FoxTrans-Desktop-win-x64.zip` and
`FoxTrans-Cli-win-x64.zip`; Linux writes
`FoxTrans-Desktop-linux-x64.tar.gz` and `FoxTrans-Cli-linux-x64.tar.gz` under
`artifacts/release`. Each archive contains exactly one executable:

```text
Windows: FoxTrans.exe / FoxTrans.Cli.exe
Linux:   FoxTrans / FoxTrans.Cli
```

Both applications are trimmed, uncompressed self-contained single-file
ReadyToRun builds. No .NET runtime is required by end users. Native dependencies
are bundled and extracted by the .NET host into its per-user cache when required.
Linux still requires the operating system's graphics stack and an accessible
PipeWire/PulseAudio or ALSA microphone service; it is not a universally static
binary.

For an unpackaged local publish, run:

```powershell
dotnet publish FoxTrans.Desktop -c Release -r win-x64 --self-contained true
dotnet publish FoxTrans.Cli -c Release -r win-x64 --self-contained true
```

Replace `win-x64` with `linux-x64` when publishing on Linux. Both artifacts are
self-contained single-file executables and do not need a separately installed
.NET Runtime. Windows desktop uses the GUI subsystem; the CLI remains an
ordinary console application on both platforms.

## First run and configuration

Run the desktop executable. It creates `config.jsonc` and `foxtrans.schema.json` in its
current working directory when needed and opens the settings drawer. Choose one
of the three pipelines, select the microphone, complete the visible provider and
output fields, and press **Save**. Environment-backed credentials are
recommended: choose **Environment variable** and enter a name such as
`OPENROUTER_API_KEY`. Enable OSC in VRChat (`Options -> OSC -> Enable`), close
settings, and press **Start**.

`config.jsonc` is local configuration and is intentionally ignored by Git because
it can contain an API key. Do not commit it. Existing legacy `config.json` files
are safely migrated to `config.jsonc` and retained as `config.legacy.json`; the
CLI exits after migration for review, while the desktop keeps the review flow
inside the application. JSON comments and trailing commas are accepted when
loading. A GUI Save intentionally rewrites the file as deterministic canonical
JSONC, uses a temporary file for replacement, and keeps one
`config.jsonc.bak`. Use **Advanced > Open raw config** only when an ordinary
setting is not exposed by the guided editor.

```jsonc
{
  "$schema": "./foxtrans.schema.json",
  "version": 1,
  "audio": { "device": "default" },
  "pipeline": {
    "vad": { "type": "webrtc", "preset": "natural-speech" },
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

The classic batch pipeline uses VAD, speech transcription, then text translation.
Its transcription endpoint can be local or remote as long as it provides the
OpenAI-compatible `/audio/transcriptions` API; translation uses a separate
OpenAI-compatible `/chat/completions` endpoint. The two providers may use
different endpoints and API keys (or no key for a local unauthenticated
transcription server).

`requestFormat` selects the transcription request encoding. `multipart` remains
the compatibility default for OpenAI-compatible and local Whisper file uploads.
OpenRouter STT should use `json`, which sends a base64-encoded WAV in
`input_audio.data`:

```jsonc
"speech": {
  "type": "openai-transcription",
  "baseUrl": "https://openrouter.ai/api/v1",
  "apiKey": "env:OPENROUTER_API_KEY",
  "model": "openai/whisper-large-v3",
  "language": "ru",
  "requestFormat": "json"
}
```

Use `env:NAME` references for secrets. This setting applies only to classic
transcription; direct audio LLM and Voxtral realtime pipelines do not consume it.

## VAD phrase presets

Direct audio and classic batch pipelines use WebRTC VAD phrase presets; Voxtral
realtime uses no VAD. These presets control speech start/end confirmation,
pre-roll, minimum phrase duration, and WebRTC operating mode.

| Preset | Use | Start | End pause | Pre-roll | Minimum phrase | WebRTC mode |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| `short-phrases` | quick short segments | 160 ms | 600 ms | 400 ms | 800 ms | Aggressive |
| `natural-speech` | ordinary conversation, default | 240 ms | 1000 ms | 600 ms | 1200 ms | VeryAggressive |
| `long-phrases` | longer statements and pauses | 400 ms | 1400 ms | 800 ms | 1600 ms | VeryAggressive |

An explicit `startAfterMs`, `stopAfterMs`, `preRollMs`, or `minimumPhraseMs`
overrides only its matching preset field. Legacy VAD names `responsive`,
`balanced`, and `strict` are accepted with a warning and map respectively to
`short-phrases`, `natural-speech`, and `long-phrases`; newly generated and
migrated configurations always use canonical names.

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
meaningfully changing for the configured interval, FoxTrans settles a
client-side logical utterance; later speech starts another logical utterance
without reconnecting, ending audio, or resetting the server transcript. The last
settled source and translation remain displayed while typing turns off.

Every translation request contains the complete newest bounded source window,
never a token or character delta. During long continuous speech that window
slides forward so old source context leaves from the beginning while the newest
speech remains. Translation requests use a latest-wins scheduler with one active
request and at most one pending newest candidate. A completed translation may
trail the newest source revision and still publish as an intermediate update
while speech continues; only the newest pending source is translated next.
Results from an old utterance or transcript epoch are discarded, and accepted
translation revisions never move backward. Update frequency is limited by model
and API response time because chat-completion responses are not streamed token by
token. VRChat OSC independently keeps the newest 144 user-perceived characters of
a translation; console and other future outputs retain the full translation.

New `.`, `!`, `?`, `;`, and `:` marks (including common fullwidth and
right-to-left equivalents) can trigger an update after the minimum interval.
Closing quotes or brackets do not hide a terminal mark. Commas and dashes do not
force an update by themselves; they use the normal changed-word threshold.

Realtime outputs are isolated from each other. Each sink has one active call and
keeps only its newest pending translation, while typing transitions remain
ordered. A slow output may skip superseded intermediate translations without
slowing translation requests or faster outputs. A publication that exceeds the
internal two-second timeout is cancelled; a sink that ignores cancellation is
quarantined for the rest of that realtime run, while other outputs continue.

## Realtime translation presets

Realtime presets affect translation scheduling and logical transcript handling,
not WebRTC VAD or phrase segmentation. They apply only under
`pipeline.realtime` in the Voxtral realtime pipeline:

Realtime presets provide these initial beta defaults:

| Preset | Minimum interval | Maximum interval | Changed words | New utterance after | Source window |
| --- | ---: | ---: | ---: | ---: | ---: |
| `responsive` | 250 ms | 700 ms | 2 | 2500 ms | 800 |
| `balanced` | 350 ms | 1000 ms | 3 | 3000 ms | 1000 |
| `economical` | 700 ms | 1800 ms | 5 | 4000 ms | 1400 |

Advanced per-field overrides are `minimumIntervalMs`, `maximumIntervalMs`,
`minimumChangedWords`, `newUtteranceAfterMs`, and `maxSourceCharacters`.
`responsive` is recommended for live VRChat translation: it updates frequently
while tolerating ordinary thinking pauses. `balanced` remains the default, and
`economical` reduces request frequency. `newUtteranceAfterMs` measures time since
the cumulative transcript last meaningfully changed; it is not an audio/VAD
silence timer and does not reconnect Voxtral. Explicit values override only their
matching preset field, so one override never requires repeating the other four.
See the example configuration for placement and environment-backed key references.

## Command line

The CLI remains available for headless servers, automation, CI, diagnostics, and
advanced terminal use. Running `FoxTrans.Cli[.exe]` without a command is the same
as `run`:

```powershell
FoxTrans.Cli[.exe]
FoxTrans.Cli[.exe] run
FoxTrans.Cli[.exe] check
FoxTrans.Cli[.exe] devices
FoxTrans.Cli[.exe] run --dry-run
FoxTrans.Cli[.exe] check --config PATH
FoxTrans.Cli[.exe] --help
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
FoxTrans.Cli[.exe] devices
```

Choose the default input, a numeric index encoded as text, an exact
case-insensitive name, or a unique case-insensitive substring:

```jsonc
"audio": { "device": "default" }
"audio": { "device": "1" }
"audio": { "device": "Microphone (USB Audio Device)" }
```

Ambiguous or unknown names and invalid indices fail visibly. FoxTrans resolves
the selection before opening the portable native audio capture backend and uses
the resolved opaque device identifier.
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

## Testing

`dotnet test` runs the core/CLI regression suite and Avalonia headless desktop
suite. Runtime-controller tests cover serialized start/stop, cancellation,
failure recovery, all three production session kinds, bounded shutdown, and
exactly-once resource disposal. Direct multimodal and classic batch tests cross
real localhost HTTP boundaries with the production WAV packer, providers,
orchestration, and output fan-out. Realtime tests exercise cumulative partials,
the real tracker/source window and scheduler, bounded per-output dispatch,
settlement, utterance/epoch invalidation, reconnect, timeouts, and shutdown.

Deterministic PCM tests cover silence, amplitudes, clipping, signed samples,
multiple sine frequencies, normalized bands, input immutability, latest-only
publication, unsupported formats, and fixed memory. Desktop tests cover the pure
bounded reducer, operation correlation and epochs, 10,000 coalesced partials,
all voice modes with fake time, reduced motion, Unicode streaming text,
configuration failures, three-mode GUI save/reload, backup and atomic replace,
secret redaction, custom chrome, overlay settings, themes, and rendered layouts
at 1440x900, 1180x760, and the supported 900x620 minimum.

External microphone/provider tests remain optional and depend on
already-authorized local credentials and reachable endpoints.

The CI matrix runs the complete test suite and self-contained publish smoke test
on Windows x64 and Ubuntu 22.04 x64. Native VAD coverage processes libfvad's
vendored PCM16 reference audio in every WebRTC operating mode; capture tests use
fake sources for callback normalization, cancellation, queue overflow, and
exactly-once disposal without requiring a microphone.
