# FoxTrans

FoxTrans is a GUI-first live voice translator for VRChat. The primary
`FoxTrans.exe` opens an Avalonia desktop studio for direct audio translation,
classic Whisper-style transcription followed by text translation, and persistent
VoxtralFox realtime transcription. The same production pipelines remain available
for automation and diagnostics through the secondary `FoxTrans.Cli.exe`.

## Requirements

- Windows x64
- .NET SDK 10.0.302 or a later .NET 10 feature-band SDK (selected through `global.json`)
- Visual Studio is not required. VS Code with C# Dev Kit works directly with `FoxTrans.slnx`.

## Desktop Live Studio

Launch `FoxTrans.exe` with no arguments. FoxTrans opens the desktop application
without a console window, loads or creates the normal working-directory
`config.jsonc`, and waits for **Start**. Microphone capture never begins merely
because the window opened.

The Live page keeps the parts of a translation session visible together:

- an audio-reactive voice orb driven by the real mono PCM16LE stream;
- listening, speech, processing, completion, stopping, and failure states;
- the resolved microphone and the actual configured pipeline;
- source and translated text, including intermediate and settled state;
- one Start/Stop action whose state follows the reusable runtime controller.

The orb uses smoothed RMS, short-term peak, clipping state, speech activity, and
12 normalized spectral bands. Audio analysis is latest-only and allocation
bounded: it does not modify PCM, retain raw audio, record audio, enqueue visual
history, or wait for the desktop. The renderer consumes the newest available
frame at display cadence and slows while the window is inactive.

The compact pipeline flow is derived from the resolved execution plan rather
than a decorative graph:

```text
Direct:    Microphone -> WebRTC VAD -> Audio LLM -> output(s)
Classic:   Microphone -> WebRTC VAD -> Speech to text
                                      -> Translator -> output(s)
Realtime:  Microphone -> Voxtral -> Utterance -> Translator -> output(s)
```

Nodes show concurrent activity, completion, warning/quarantine, and failure.
Connector motion indicates the typed data moving between stages and wraps with
the available width. It does not change layout size and does not create a timer
per stage.

Source and translation updates reveal new Unicode grapheme clusters smoothly.
A stable common prefix is retained, corrections replace only the unstable
suffix, rapid newer partials coalesce, and catch-up mode guarantees prompt
convergence. Surrogate pairs, combining sequences, and ZWJ emoji families are
never split. GUI text is not truncated to VRChat's output-specific 144-element
limit.

The narrow navigation rail contains:

- **Live** — the active voice, flow, source, and translation workspace;
- **Pipeline** — the loaded topology, redacted effective configuration, and
  validation problems;
- **Settings** — system/dark/light appearance, reduced motion, launch page, and
  window-placement behavior.

The Pipeline page is deliberately inspection-only in product session 8.0. Its
**Open config folder** action opens the real configuration location; it does not
present editing controls that cannot save. The complete typed visual pipeline
builder is the next product phase.

Window size, position, selected page, appearance, and reduced-motion choice are
stored separately in
`%LOCALAPPDATA%\FoxTrans\desktop-preferences.json`. Pipeline/provider settings and
credentials remain exclusively in `config.jsonc`. Missing or invalid
configuration is shown inside the application, the window stays usable, and the
Pipeline page remains accessible. A runtime failure similarly leaves readable
detail in the window and permits Start again after the underlying problem is
fixed. Closing the window waits for bounded runtime shutdown and resource
disposal.

## Secondary CLI live reporter

`FoxTrans.Cli.exe run` turns the same resolved configuration into a view-only
terminal pipeline. Configured stages render from top to bottom:

```text
Direct:    Microphone
              |
           WebRTC VAD
              |
           Audio LLM
              |
           VRChat OSC

Classic:   Microphone
              |
           WebRTC VAD
              |
           Speech to text
              |
           Text translator
              |
           VRChat OSC

Realtime:  Microphone
              |
           Voxtral streaming
              |
           Logical utterance
              |
           Text translator
              |
           VRChat OSC
```

The rich TUI is view-only. Configure FoxTrans in config.jsonc; there are no
runtime menus, keyboard commands, mouse controls, selection, scrolling, or
configuration editing. Active stages pulse, labelled data markers travel down
the connectors, and every stage card shows effective non-secret configuration
and live state without requiring selection. Multiple outputs are stacked
vertically and retain separate runtime status. Credentials are reduced to
`configured: yes/no`, prompts to `configured/not configured`, and endpoint
userinfo, query, and fragment values are removed.

```text
+ FOXTRANS ----------------------------------------------- 00:03:42 +
| Classic transcription + translation       RUNNING      errors 0 |
+---------------------------------------------------------------+
| [1] MICROPHONE                                                  |
| CONFIG  Device 0: Microphone (USB Audio Device)               |
| LIVE    [*] LISTENING  [########............] -24 dB           |
|                              | PCM audio                      |
|                              v                                |
| [2] WEBRTC VAD                                                 |
| CONFIG  natural-speech | start 240 | end 1000 | minimum 1200 |
| LIVE    [o] RECORDING  current phrase 2.18 s                 |
|                              | speech segment                |
|                              v                                |
| [3] SPEECH TO TEXT                                             |
| CONFIG  whisper-large-v3 | JSON base64 WAV | ru              |
| LIVE    [+] PROCESSING  1.36 s                                |
|                              | transcript                    |
|                              v                                |
| [4] TEXT TRANSLATOR                                            |
| LIVE    [O] COMPLETED  823 ms                                 |
|                              | translation update            |
|                              v                                |
| [5] VRCHAT OSC                                                 |
| CONFIG  127.0.0.1:9000 | typing enabled                      |
| LIVE    DELIVERED  4 ms                                       |
+ SOURCE -------------------------------------------------------+
| Я проверяю классический режим перевода.                       |
+ RESULT -------------------------------------------------------+
| I am testing the classic translation mode.                    |
+---------------------------------------------------------------+
```

Terminal UI selection is explicit when needed:

```powershell
FoxTrans.Cli.exe run --ui auto
FoxTrans.Cli.exe run --ui rich
FoxTrans.Cli.exe run --ui plain
```

`auto` is the default. It uses the rich live display only for usable,
non-redirected interactive stdout, regardless of stdin, and otherwise emits
bounded timestamped plain-text events. `rich` requests the live display but
safely falls back to plain output with one warning if terminal capabilities or
dimensions are insufficient. `plain` never emits ANSI control sequences, moves
the cursor, or clears the screen, so it is suitable for files, pipes, and CI.
Ctrl+C remains the normal operating-system cancellation path.

The rich UI supports standard Windows `cmd.exe`, PowerShell, and Windows
Terminal. Essential structure uses ASCII-safe borders and remains usable at
80x25; full, normal, compact, and tiny layouts are selected automatically on
resize. There are no replacement keyboard controls: the terminal only
visualizes execution. Redirected output remains available through `--ui plain`.

## Build and publish

Clone the repository and publish the desktop and CLI into their isolated
directories:

```powershell
git clone https://github.com/MrShitFox/FoxTrans.git
cd FoxTrans
dotnet publish FoxTrans.Desktop -c Release
dotnet publish FoxTrans.Cli -c Release
```

The primary desktop artifact is:

```text
FoxTrans.Desktop\bin\Release\net10.0\win-x64\publish\FoxTrans.exe
```

The secondary CLI artifact is:

```text
FoxTrans.Cli\bin\Release\net10.0\win-x64\publish\FoxTrans.Cli.exe
```

Both are self-contained Windows x64 single-file executables. The .NET runtime,
managed dependencies, and native WebRTC VAD dependency are bundled, so a target
computer does not need a separately installed .NET Runtime. `FoxTrans.exe` uses
the Windows GUI subsystem; `FoxTrans.Cli.exe` remains an ordinary console
application.

## First run and configuration

Run `FoxTrans.exe`. It creates `config.jsonc` and `foxtrans.schema.json` in its
current working directory when needed and opens the desktop with an in-app
configuration notice. JSON comments and trailing commas are supported. Configure
the key through an environment reference such as `env:OPENROUTER_API_KEY`,
enable OSC in VRChat (`Options -> OSC -> Enable`), return to FoxTrans, and press
**Start**.

`config.jsonc` is local configuration and is intentionally ignored by Git because
it can contain an API key. Do not commit it. Existing legacy `config.json` files
are safely migrated to `config.jsonc` and retained as `config.legacy.json`; the
CLI exits after migration for review, while the desktop keeps the review flow
inside the application.

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
advanced terminal use. Running `FoxTrans.Cli.exe` without a command is the same
as `run`:

```powershell
FoxTrans.Cli.exe
FoxTrans.Cli.exe run
FoxTrans.Cli.exe check
FoxTrans.Cli.exe devices
FoxTrans.Cli.exe run --dry-run
FoxTrans.Cli.exe run --ui plain
FoxTrans.Cli.exe check --config C:\Configs\foxtrans.jsonc
FoxTrans.Cli.exe --help
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
FoxTrans.Cli.exe devices
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
configuration failures, secret redaction, navigation, themes, and rendered
layouts at 1440x900, 1180x760, 960x640, and 800x600.

External microphone/provider tests remain optional and depend on
already-authorized local credentials and reachable endpoints.
