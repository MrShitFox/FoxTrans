# FoxTrans Desktop guide

FoxTrans Desktop is the primary way to run live translation. It opens a single
Live Studio surface: configuration stays in a settings drawer instead of being
spread across dashboards and setup windows.

## Before the first run

- Use a supported x64 desktop environment: Windows, or Ubuntu 22.04+ and a
  compatible Linux desktop distribution.
- Enable **Options → OSC → Enable** in VRChat before starting FoxTrans.
- Decide where FoxTrans should keep `config.jsonc`. The Desktop app uses its
  **current working directory**. Starting the executable from its install folder
  keeps the config there; moving the executable or launching it from another
  folder changes that default location.
- Have the provider endpoint, model name, and credential ready. Pasting an API
  key into the local configuration is fine; an environment-variable reference is
  an optional advanced alternative.

The first launch creates `config.jsonc` and `foxtrans.schema.json` in that
working directory, then opens Settings. A missing or invalid configuration never
closes the Desktop app: fix it in the drawer and save it there.

## First setup

1. Open **Settings → Pipeline** and choose one of the modes below.
2. Open **Audio**, refresh devices if necessary, select a microphone, and use
   **Microphone test** to check the real selected input without sending any
   provider request.
3. Open **Providers** and enter only the fields shown for the selected mode.
   Paste the API key if your provider requires one. Advanced users can instead
   choose an environment-variable reference such as `OPENROUTER_API_KEY`.
4. Open **Output**, keep or add a `VRChat OSC` output, and decide whether to
   send the typing indicator.
5. Press **Save**. Close Settings and press **Start**.

If the pipeline is active, the action becomes **Save & Restart**. FoxTrans saves
the new configuration first, stops the active microphone session, and then
starts the resolved replacement plan. It never silently restarts an open
microphone.

## Choose a pipeline

| Mode | Choose it when | What Settings asks for |
| --- | --- | --- |
| **Audio LLM** | Your provider can receive audio and return the final translation directly. | VAD preset, audio-chat endpoint, model, prompt, and optional API key. |
| **Whisper + LLM** | You want a familiar transcription-then-translation path, including a local or remote OpenAI-compatible Whisper service. | VAD preset, transcription endpoint/model/language/request format, then a chat endpoint/model/prompt. |
| **Voxtral + LLM** | You have a compatible continuous Voxtral realtime server and want translations to update while you speak. | Voxtral server/key/delay, realtime cadence preset, then a text-translation endpoint/model/prompt. |

Audio LLM and Whisper + LLM use WebRTC VAD to turn speech into phrases.
Voxtral + LLM streams continuous 16 kHz PCM audio over one WebSocket and does
not use VAD. The detailed field reference and ready-to-copy examples live in
the [CLI and JSONC reference](cli-config.md).

## Live Studio

The central waveform is visual feedback only; it does not alter captured audio
or slow the translation pipeline. The Live surface shows the current runtime
state, microphone activity, source recognition when available, and the current
translation.

- **Audio LLM** shows the translation only because the provider returns it
  directly.
- **Whisper + LLM** and **Voxtral + LLM** show recognition plus translation.
- **Start/Stop** is runtime-aware. A handled failure leaves the app open so the
  configuration can be corrected and started again.
- The title area reflects the resolved pipeline and model or model pair. It
  never shows API keys.

## Settings and local files

### Configuration

The guided controls are the normal way to edit `config.jsonc`. The Advanced
section can reveal the path, open the raw JSONC file, open its folder, show the
application version, and show sanitized diagnostics.

FoxTrans accepts JSON comments and trailing commas when loading. Saving through
Desktop writes deterministic canonical JSONC by using a temporary sibling file
and preserves one previous copy as `config.jsonc.bak`. If validation or writing
fails, the existing config remains untouched.

An old `config.json` is migrated once to `config.jsonc`; its original is kept as
`config.legacy.json`. If both exist, `config.jsonc` wins and FoxTrans warns that
the legacy file is ignored.

Do not commit or share `config.jsonc` when it contains an inline API key. If you
prefer to keep the secret outside the file, advanced users can write
`"apiKey": "env:VARIABLE_NAME"` instead.

### Desktop preferences

Appearance, reduced-motion preference, and window placement are not part of the
translation config. They are stored separately as `desktop-preferences.json` in
the platform local application-data folder:

- Windows: `%LOCALAPPDATA%\FoxTrans\desktop-preferences.json`
- Linux: normally `~/.local/share/FoxTrans/desktop-preferences.json`

Use **Appearance** to follow the system, force dark/light, or reduce motion.
These choices do not change the selected provider, microphone, or OSC output.

## VRChat OSC

FoxTrans sends the translated text to VRChat's chatbox via UDP OSC. The default
address is `127.0.0.1:9000`; change it only when your VRChat/OSC routing needs a
different IPv4 address and port. The typing option sends the normal VRChat
chatbox typing state while work is in progress.

VRChat chatbox text has a practical 144 user-perceived-character limit. When a
translation is longer, FoxTrans keeps the newest readable suffix and prefixes
it with an ellipsis. This does not shorten provider requests or the Desktop
display.

## Common setup problems

| Symptom | What to check |
| --- | --- |
| Settings opens with a configuration error | Read the field-level error, fix the selected provider/output, and save. The old file is retained on a failed save. |
| An environment-backed key is missing | If you chose `env:NAME`, define that variable in the environment that launches FoxTrans, then restart the app. Otherwise paste the API key directly in Providers. |
| No microphone activity | Refresh Audio devices, select the correct input, and run Microphone test. The device must support mono 16 kHz PCM capture. |
| VRChat shows no text | Enable VRChat OSC, confirm the output is enabled, and verify the host/port—normally `127.0.0.1:9000`. |
| Voxtral will not start | Confirm the server is reachable, exposes `GET /health`, accepts mono 16 kHz PCM16LE, and is not busy. Use `FoxTrans.Cli check` for a safe readiness check. |
| A change did not affect the active session | Press **Save & Restart** while the runtime is active; edits do not mutate an already-running pipeline. |

For optional advanced/headless use and the full configuration surface, see the
[CLI and JSONC reference](cli-config.md).
