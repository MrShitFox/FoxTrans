# 🌐FoxTrans

**FoxTrans** is a lightning-fast, lightweight real-time AI voice translator built specifically for VRChat.

It listens to your microphone, filters out background noise at the hardware level, translates your speech on the fly using modern multimodal neural networks (via OpenRouter), and sends the translated text directly above your avatar's head using the OSC protocol.

## ✨ Key Features

* **Smart Voice Activity Detection (WebRTC VAD):** A "paranoid" noise filter. The program ignores keyboard typing, mouse clicks, coughs, and sighs. It only captures your actual speech.
* **Direct Translation (Audio-to-Text):** No double latency from classic pipelines (Speech-to-Text -> Text-to-Text). Audio is sent directly to a multimodal model (default is `gemini-2.5-flash`), which immediately returns the translated text in one request.
* **Seamless VRChat Integration:** * Displays a typing indicator (`...`) above your head while the AI is processing the translation.
* **Full UTF-8 Support:** Japanese Kanji, Korean Hangul, Chinese characters, and emojis display perfectly in-game without the dreaded question marks `????` (thanks to a custom, zero-dependency OSC packer).


* **Zero CPU Load:** No empty polling loops. During silence, the program's threads sleep at the OS level, consuming 0% CPU.
* **Monolithic .exe:** Just one portable executable file. No heavy dependencies, no Python installations, and no virtual environment setup required.

## 🚀 Quick Start

1. Download `FoxTrans.exe` (or build it from source).
2. Run the executable once. It will automatically generate a default `config.json` file and close.
3. Open `config.json` in any text editor and paste your [OpenRouter](https://openrouter.ai/) API key.
4. In **VRChat**, open your Action Menu: `Options -> OSC -> Enable`.
5. Run `FoxTrans.exe` again. Start talking!

## ⚙️ Configuration (config.json)

The configuration file allows you to tweak the app's behavior without recompiling the code:

```json
{
  "Api": {
    "Key": "sk-or-v1-YOUR_API_KEY",
    "Endpoint": "https://openrouter.ai/api/v1/chat/completions",
    "Model": "google/gemini-2.5-flash",
    "Prompt": "Translate this audio to English. Reply ONLY with the final translated text, no quotes or explanations."
  },
  "Vad": {
    "MinSpeechFrames": 12,    // Start sensitivity (12 = requires 240ms of continuous voice)
    "MinSilenceFrames": 50,   // Silence duration to end a phrase (50 = 1 second)
    "PreRollFrames": 30,      // History buffer (saves the very beginning of your words)
    "MinPhraseLengthMs": 1200 // Minimum phrase length (anything shorter is ignored as noise)
  },
  "Osc": {
    "IpAddress": "127.0.0.1",
    "Port": 9000,
    "EnableTypingIndicator": true
  }
}

```

*💡 **Pro Tip:** If you want to speak English and have Japanese players understand you, just change the prompt in your config to: `"Translate this audio to Japanese. Reply ONLY with the final translated text..."*

## 🛠️ Building from Source

The project is written in **C# (.NET 10)** and uses the `Publish Single File` feature.

To compile it yourself:

```bash
git clone https://github.com/yourname/FoxTrans.git
cd FoxTrans
dotnet publish -c Release

```

The compiled, clean `.exe` file (without any extra `.dll` or `.pdb` clutter) will be located at:
`bin/Release/net10.0/win-x64/publish/`
