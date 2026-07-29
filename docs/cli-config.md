# CLI and JSONC configuration reference

FoxTrans uses one `config.jsonc` to describe one active translation pipeline.
The Desktop editor writes the same configuration. The CLI is optional and aimed
at advanced users who want headless hosts, automation, diagnostics, or direct
editing of advanced settings.

Use the [Desktop guide](desktop.md) for normal setup. This page is the complete
reference for the file and the command line.

## CLI commands

The executable is `FoxTrans.Cli.exe` on Windows and `FoxTrans.Cli` on Linux.
Running it with no command is the same as `run`.

```text
FoxTrans.Cli[.exe] [run] [--config PATH] [--dry-run]
FoxTrans.Cli[.exe] check [--config PATH]
FoxTrans.Cli[.exe] devices
FoxTrans.Cli[.exe] --help
```

| Command | Purpose |
| --- | --- |
| `run` | Starts the configured translation pipeline. In an interactive ANSI terminal it shows the read-only Live Studio; redirected or small terminals receive bounded plain-text events. Stop with `Ctrl+C`. |
| `run --dry-run` | Validates, resolves the selected microphone and execution plan, and prints the plan without opening capture, OSC, HTTP, or WebSocket resources. Secrets are redacted. |
| `check` | Performs the same local validation without recording. With Voxtral, it safely calls only `GET /health`; OpenAI-compatible endpoints are syntax-checked but never probed with an inference request. |
| `devices` | Lists microphone inputs without starting capture. It cannot be combined with `--config`. |

`--config PATH` loads that exact file. Relative paths are resolved from the
current working directory; a missing path or a directory is an error, and the
CLI does not fall back to another config.

Exit codes are stable:

| Code | Meaning |
| ---: | --- |
| `0` | Successful run/check, or normal `Ctrl+C` cancellation. |
| `1` | Runtime or provider failure. |
| `2` | Command-line or configuration error. |
| `3` | Dependency readiness failure, such as a selected microphone or Voxtral server not being ready. |

## Where configuration lives

Without `--config`, FoxTrans uses the current directory:

1. It loads `config.jsonc` if it exists and makes sure
   `foxtrans.schema.json` exists beside it.
2. If only legacy `config.json` exists, it creates `config.jsonc`, preserves the
   old file as `config.legacy.json`, and asks the CLI user to review it before a
   later run.
3. If neither file exists, it creates a default `config.jsonc` and
   `foxtrans.schema.json`, then asks the CLI user to set the API key before a
   later run.

Creation and migration happen only in this default workflow. An explicit
`--config` path must already exist. A relative `$schema` path is resolved beside
the selected config file, not beside the executable.

JSONC comments and trailing commas are accepted. Unknown settings are rejected,
so a typo fails early instead of silently doing nothing. Keep this header in a
root config:

```jsonc
{
  "$schema": "./foxtrans.schema.json",
  "version": 1,
  // audio, pipeline, and outputs follow
}
```

`effectiveAudio`, `effectivePipeline`, and `effectiveOutputs` are derived
read-only shapes that can appear in older generated files. Do not author them;
configure `audio`, `pipeline`, and `outputs` instead.

## API keys and provider endpoints

Every `apiKey` field is optional because local endpoints can be unauthenticated.
For a protected endpoint, a normal local config can hold the key directly:

```jsonc
"apiKey": "paste-your-api-key-here"
```

Do not commit or share a config that contains a real key. If you specifically
want the secret outside the file, FoxTrans also supports an optional environment
reference:

```jsonc
"apiKey": "env:OPENROUTER_API_KEY"
```

FoxTrans resolves `env:NAME` before any microphone, HTTP, WebSocket, or UDP
resource starts. A missing or empty variable is a validation error. Any value
without the `env:` prefix is used as the inline API key from the local config.

Set a variable in the shell that launches FoxTrans:

```powershell
$env:OPENROUTER_API_KEY = "replace-with-your-key"
```

```bash
export OPENROUTER_API_KEY='replace-with-your-key'
```

For OpenAI-compatible providers, give the server base URL. FoxTrans adds the
correct path only when it is absent:

- Audio LLM and text translation use `/chat/completions`.
- Classic transcription uses `/audio/transcriptions`.

For example, `https://openrouter.ai/api/v1` is accepted for a chat provider,
and a URL already ending in `/chat/completions` is left unchanged. Use an
absolute provider URL and the model name expected by that service. FoxTrans does
not choose a model or invent a health endpoint for an OpenAI-compatible service.

## Pipeline combinations

Exactly one `pipeline.speech.type` selects the pipeline. `outputs` must contain
at least one output.

| `pipeline.speech.type` | Pipeline | Required companions | Must be absent |
| --- | --- | --- | --- |
| `openai-chat-audio` | Audio LLM | `pipeline.vad` | `pipeline.translation`, `pipeline.realtime` |
| `openai-transcription` | Whisper + LLM | `pipeline.vad`, `pipeline.translation` | `pipeline.realtime` |
| `voxtral-fox` | Voxtral + LLM | `pipeline.realtime`, `pipeline.translation` | `pipeline.vad` |

The following examples are also available as files:

- [Audio LLM example](../examples/config.multimodal.jsonc)
- [Whisper + LLM example](../examples/config.whisper.jsonc)
- [Voxtral + LLM example](../examples/config.voxtral.jsonc)

### Audio LLM — direct audio translation

This path segments speech locally, sends each WAV phrase to an
OpenAI-compatible chat/audio provider, and publishes the returned translation.
There is no separate transcription or text-translation provider.

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
      "apiKey": "paste-your-api-key-here",
      "model": "google/gemini-2.5-flash",
      "prompt": "Translate this audio to English. Reply only with the translated text."
    }
  },
  "outputs": [{ "type": "vrchat-osc" }]
}
```

Required speech fields are `baseUrl`, `model`, and `prompt`. `apiKey` is
optional. The prompt should name the desired target language and request only
the final text so the VRChat chatbox does not receive commentary.

### Whisper + LLM — transcription followed by translation

This path segments speech locally, sends WAV audio to an OpenAI-compatible
transcription server, then sends the recognized text to an OpenAI-compatible
chat provider. The two providers may use different endpoints and keys.

```jsonc
{
  "$schema": "./foxtrans.schema.json",
  "version": 1,
  "audio": { "device": "default" },
  "pipeline": {
    "vad": { "type": "webrtc", "preset": "natural-speech" },
    "speech": {
      "type": "openai-transcription",
      "baseUrl": "http://127.0.0.1:8000/v1",
      // Omit apiKey for a local unauthenticated server.
      "model": "whisper-1",
      "language": "ru",
      "requestFormat": "multipart"
    },
    "translation": {
      "type": "openai-chat",
      "baseUrl": "https://openrouter.ai/api/v1",
      "apiKey": "paste-your-api-key-here",
      "model": "google/gemini-2.5-flash",
      "prompt": "Translate the text into English. Reply only with the translation."
    }
  },
  "outputs": [{ "type": "vrchat-osc" }]
}
```

Required transcription fields are `baseUrl` and `model`; `language` and
`apiKey` are optional. `language` is passed through when present and is useful
when the transcription service supports a source-language hint.

`requestFormat` selects the upload wire format:

| Value | Default | Use it for |
| --- | --- | --- |
| `multipart` | Yes | Standard OpenAI-compatible file uploads to `/audio/transcriptions`. |
| `json` | No | Providers such as OpenRouter STT that expect a base64 WAV in `input_audio.data`. |

The text-translation object always has `type: "openai-chat"`; `baseUrl`,
`model`, and `prompt` are required and `apiKey` is optional.

### Voxtral + LLM — continuous realtime translation

This path keeps one authenticated WebSocket session open, sends continuous mono
16 kHz PCM16LE (including silence), receives cumulative transcript updates, and
translates the newest bounded source window. The server must be a compatible
Voxtral realtime server, such as a deployment based on
[MrShitFox/voxtral.cpp](https://github.com/MrShitFox/voxtral.cpp).

```jsonc
{
  "$schema": "./foxtrans.schema.json",
  "version": 1,
  "audio": { "device": "default" },
  "pipeline": {
    "speech": {
      "type": "voxtral-fox",
      "baseUrl": "http://192.168.2.136:8080",
      "apiKey": "paste-your-voxtral-api-key-here",
      "delayMs": 240
    },
    "translation": {
      "type": "openai-chat",
      "baseUrl": "https://openrouter.ai/api/v1",
      "apiKey": "paste-your-api-key-here",
      "model": "google/gemini-2.5-flash",
      "prompt": "Translate the text into English. Reply only with the translation."
    },
    "realtime": { "preset": "responsive" }
  },
  "outputs": [{ "type": "vrchat-osc" }]
}
```

`baseUrl` must be an absolute HTTP or HTTPS server URL without user info.
FoxTrans derives `GET /health` and the `ws://` or `wss://`
`/v1/realtime/transcription` endpoint from that server. `delayMs` defaults to
`240` and must be one of:

```text
80, 160, 240, 320, 400, 480, 560, 640, 720, 800, 880, 960, 1040, 1120, 1200, 2400
```

`pipeline.realtime` and the text `translation` provider are required. Do not
add VAD: realtime Voxtral receives continuous audio and settles logical
utterances client-side, without ending or recreating the server session after an
ordinary pause.

## Root, audio, and output settings

| Path | Type/default | Notes |
| --- | --- | --- |
| `$schema` | string, `./foxtrans.schema.json` | Editor schema location. Relative paths resolve beside this config. |
| `version` | integer, `1` | Only version `1` is supported. |
| `audio.device` | string, `default` | Microphone selection; see below. |
| `outputs` | non-empty array | Each element currently has `type: "vrchat-osc"`. |
| `outputs[].address` | IPv4 `host:port`, `127.0.0.1:9000` | Literal IPv4 address plus port `1`–`65535`; host names are not accepted. |
| `outputs[].typingIndicator` | boolean, `true` | Sends VRChat chatbox typing OSC while appropriate. |

The audio device can be selected four ways:

```jsonc
"audio": { "device": "default" }                     // OS default, or first input
"audio": { "device": "1" }                           // Number from `FoxTrans.Cli devices`
"audio": { "device": "Microphone (USB Audio Device)" } // Exact name, case-insensitive
"audio": { "device": "USB Audio" }                   // Unique name substring, case-insensitive
```

Ambiguous names, unknown names, unavailable devices, and inputs that cannot
open as mono 16 kHz PCM16LE fail visibly. Run `FoxTrans.Cli devices` after
connecting or changing microphones.

## WebRTC VAD settings

`pipeline.vad` is required for Audio LLM and Whisper + LLM, and has one
supported type:

```jsonc
"vad": {
  "type": "webrtc",
  "preset": "natural-speech",
  "startAfterMs": null,
  "stopAfterMs": null,
  "preRollMs": null,
  "minimumPhraseMs": null,
}
```

Set an override only when the preset is close but not quite right. Each
override changes only its own field and must be an integer from `1` to `60000`
milliseconds.

| Preset | Best for | Start | End pause | Pre-roll | Minimum phrase | WebRTC mode |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| `short-phrases` | Fast short segments | 160 ms | 600 ms | 400 ms | 800 ms | Aggressive |
| `natural-speech` | Ordinary conversation; default | 240 ms | 1000 ms | 600 ms | 1200 ms | VeryAggressive |
| `long-phrases` | Longer statements and pauses | 400 ms | 1400 ms | 800 ms | 1600 ms | VeryAggressive |

The old names `responsive`, `balanced`, and `strict` remain readable for
compatibility and map to `short-phrases`, `natural-speech`, and `long-phrases`.
They emit a warning; use the canonical names in new files.

## Realtime scheduling settings

`pipeline.realtime` applies only to `voxtral-fox`. It controls translation
cadence, natural-pause settlement, and source-window size; it is not VAD.

```jsonc
"realtime": {
  "preset": "balanced",
  "minimumIntervalMs": null,
  "maximumIntervalMs": null,
  "minimumChangedWords": null,
  "newUtteranceAfterMs": null,
  "maxSourceCharacters": null,
}
```

| Preset | Minimum interval | Maximum interval | Changed words | New utterance after | Source window |
| --- | ---: | ---: | ---: | ---: | ---: |
| `responsive` | 250 ms | 700 ms | 2 | 2500 ms | 800 |
| `balanced` | 350 ms | 1000 ms | 3 | 3000 ms | 1000 |
| `economical` | 700 ms | 1800 ms | 5 | 4000 ms | 1400 |

`balanced` is the default. `responsive` is a good starting point for live
VRChat translation; `economical` reduces provider request frequency. Explicit
overrides must stay within these validation limits:

| Field | Allowed range | Meaning |
| --- | ---: | --- |
| `minimumIntervalMs` | 50–10000 ms | Minimum time between eligible translation requests. |
| `maximumIntervalMs` | 50–30000 ms | Forces an update interval; it must be at least the resolved minimum. |
| `minimumChangedWords` | 1–100 | Amount of changed source needed for the ordinary update rule. |
| `newUtteranceAfterMs` | 250–30000 ms | Time since meaningful cumulative-transcript change before client-side settlement. It does not reconnect Voxtral. |
| `maxSourceCharacters` | 64–20000 | Unicode text-element bound for each full source window sent to the translator. |

Strong new punctuation can also make a new source revision eligible after the
minimum interval. Requests always contain the newest complete bounded source
window, never a token or character delta. The scheduler keeps one active request
and at most one latest pending candidate, so a slow provider does not build an
unbounded backlog.

## Validation and safe diagnostics

Run this before a live session, especially after editing JSONC:

```powershell
FoxTrans.Cli.exe check --config .\config.jsonc
FoxTrans.Cli.exe run --dry-run --config .\config.jsonc
```

On Linux, replace `FoxTrans.Cli.exe` with `./FoxTrans.Cli` and use normal shell
paths. `check` never sends audio, OSC messages, OpenAI-compatible inference
requests, or a Voxtral WebSocket session. `run --dry-run` likewise avoids
runtime resources. Neither command prints API keys, prompts, endpoint user info,
queries, or fragments in its resolved-plan display.

For live runtime recovery behavior and the Desktop UI, see the
[Desktop guide](desktop.md). For source builds and self-contained CLI binaries,
see the [build guide](build.md).
