<p align="center">
  <img src="brand/foxtrans-lockup-on-dark.svg" alt="FoxTrans" width="360">
</p>

<p align="center">
  Live VRChat voice translation that stays out of your way.
</p>

<p align="center">
  <a href="https://github.com/MrShitFox/FoxTrans/releases">Releases</a> ·
  <a href="#start-here">Start here</a> ·
  <a href="#docs">Docs</a>
</p>

# FoxTrans

FoxTrans is a desktop-first live voice translator for VRChat. Speak into your
microphone, send the translation to VRChat through OSC, and keep playing.

It does **not** run an AI model on your gaming PC. Locally, FoxTrans handles
microphone capture, lightweight VAD, the interface, and OSC; inference happens
at the OpenAI-compatible, OpenRouter, or self-hosted API endpoint you choose.

## Why FoxTrans?

- ⚡ **Built for live use** — Desktop Live Studio, direct microphone testing,
  and bounded audio processing. An optional CLI is there for advanced users,
  servers, and diagnostics.
- ☁️ **Your provider, your choice** — use OpenRouter, an OpenAI-compatible
  service, or point FoxTrans at your own API.
- 🎙️ **Three translation paths** — direct Audio LLM, Whisper + LLM, or
  continuous Voxtral + LLM realtime transcription.
- 💬 **VRChat-ready** — sends chatbox text over OSC and can control the typing
  indicator.
- 🔐 **Simple local setup** — paste an API key into the config when that is
  easiest; advanced users can also reference an environment variable.
- ✨ **A little polish where it matters** — live waveform, streaming text,
  dark/light themes, reduced motion, and no dashboard clutter.

## Start here

Download the latest build from the
[Releases page](https://github.com/MrShitFox/FoxTrans/releases).

Then:

1. Launch the Desktop app from the folder where you want its `config.jsonc`.
2. In **Settings**, choose a pipeline, microphone, provider, and VRChat OSC
   output; save the configuration.
3. In VRChat, open **Options → OSC** and enable OSC.
4. Press **Start** and speak normally.

Need to build from source or use the optional advanced CLI? Use the guides below.

## Realtime STT option

[MrShitFox/voxtral.cpp](https://github.com/MrShitFox/voxtral.cpp) is one of the
realtime STT engines you can use with FoxTrans's Voxtral pipeline. It is a
heavily reworked fork with fixes plus streaming C and WebSocket APIs.

## Docs

| Guide | What it covers |
| --- | --- |
| [Desktop guide](docs/desktop.md) | First run, Live Studio, settings, VRChat OSC, and troubleshooting. |
| [CLI and JSONC reference](docs/cli-config.md) | Every configuration field, examples, CLI commands, validation, and diagnostics. |
| [Build guide](docs/build.md) | Windows/Linux prerequisites, tests, and self-contained single-file publishes. |

Maintainers can also consult the [architecture notes](FoxTrans/ARCHITECTURE.md),
[startup measurements](docs/startup-performance.md), and
[GPU profiling guide](docs/gpu-performance.md).

## License

FoxTrans is available under the [GNU General Public License v3.0](LICENSE).
