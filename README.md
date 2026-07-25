# FoxTrans

FoxTrans is a lightweight real-time AI voice translator for VRChat. It captures microphone audio, filters it with WebRTC VAD, sends it to an OpenRouter-compatible API, and sends the translated text to VRChat over OSC.

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

Run `FoxTrans.exe` once. It creates a default `config.json` in its current working directory and exits. Add your OpenRouter API key to that file, enable OSC in VRChat (`Options -> OSC -> Enable`), then run the executable again.

`config.json` is local configuration and is intentionally ignored by Git because it can contain an API key. Do not commit it.

```json
{
  "Api": {
    "Key": "sk-or-v1-YOUR_API_KEY",
    "Endpoint": "https://openrouter.ai/api/v1/chat/completions",
    "Model": "google/gemini-2.5-flash",
    "Prompt": "Translate this audio to English. Reply ONLY with the final translated text, no quotes or explanations."
  },
  "Vad": {
    "MinSpeechFrames": 12,
    "MinSilenceFrames": 50,
    "PreRollFrames": 30,
    "MinPhraseLengthMs": 1200
  },
  "Osc": {
    "IpAddress": "127.0.0.1",
    "Port": 9000,
    "EnableTypingIndicator": true
  }
}
```
