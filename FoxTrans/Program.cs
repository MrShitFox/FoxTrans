using System.Collections.Concurrent;
using NAudio.Wave;
using WebRtcVadSharp;

Console.OutputEncoding = System.Text.Encoding.UTF8;

AppConfig config = AppConfig.Load();

// --- DASHBOARD UI STATE ---
Console.CursorVisible = false;
string lastTranslation = "None";
string lastSystemMsg = "Ready to rock.";

void DrawUI(string status, ConsoleColor statusColor, string apiStatus, ConsoleColor apiColor)
{
    Console.Clear();

    // Header
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("========================================");
    Console.WriteLine("  FoxTrans");
    Console.WriteLine($"  Model: {config.Api.Model}");
    Console.WriteLine($"  OSC:   {config.Osc.IpAddress}:{config.Osc.Port}");
    Console.WriteLine("========================================\n");

    // Microphone Status
    Console.ForegroundColor = statusColor;
    Console.WriteLine($"[🎙️] Status: {status}");

    // API Status
    Console.ForegroundColor = apiColor;
    Console.WriteLine($"[⚙️] API:    {apiStatus}\n");

    // Last Translation (Always visible)
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[💬] Result: {lastTranslation}\n");

    // System Logs (Flushes, short noises, etc)
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"[SYS] {lastSystemMsg}");

    Console.ResetColor();
}

// --- AUDIO & VAD INIT ---
using var audioQueue = new BlockingCollection<byte[]>();
using var waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 20 };

waveIn.DataAvailable += (sender, e) =>
{
    byte[] frame = new byte[e.BytesRecorded];
    Buffer.BlockCopy(e.Buffer, 0, frame, 0, e.BytesRecorded);
    audioQueue.Add(frame);
};

waveIn.StartRecording();
using var vad = new WebRtcVad { OperatingMode = OperatingMode.VeryAggressive };

int speechCounter = 0;
int silenceCounter = 0;
bool isSpeaking = false;
var preRollBuffer = new Queue<byte[]>();
var currentPhrase = new List<byte>();

DrawUI("Listening...", ConsoleColor.DarkGray, "Waiting...", ConsoleColor.DarkGray);

// --- MAIN SYNCHRONOUS LOOP ---
foreach (byte[] frame in audioQueue.GetConsumingEnumerable())
{
    if (frame.Length != 640) continue;

    bool isSpeech = vad.HasSpeech(frame, SampleRate.Is16kHz, FrameLength.Is20ms);

    if (isSpeech) { speechCounter++; silenceCounter = 0; }
    else { silenceCounter++; speechCounter = 0; }

    if (!isSpeaking)
    {
        preRollBuffer.Enqueue(frame);
        if (preRollBuffer.Count > config.Vad.PreRollFrames) preRollBuffer.Dequeue();

        if (speechCounter >= config.Vad.MinSpeechFrames)
        {
            isSpeaking = true;
            foreach (var pastFrame in preRollBuffer) currentPhrase.AddRange(pastFrame);
            preRollBuffer.Clear();

            DrawUI("Recording...", ConsoleColor.Yellow, "Waiting...", ConsoleColor.DarkGray);
        }
    }
    else
    {
        currentPhrase.AddRange(frame);

        if (silenceCounter >= config.Vad.MinSilenceFrames)
        {
            isSpeaking = false;
            byte[] rawPcmData = currentPhrase.ToArray();
            currentPhrase.Clear();

            double phraseLengthMs = (rawPcmData.Length / 32000.0) * 1000.0;

            if (phraseLengthMs < config.Vad.MinPhraseLengthMs)
            {
                lastSystemMsg = $"Ignored short noise ({phraseLengthMs:F0}ms).";
                DrawUI("Listening...", ConsoleColor.DarkGray, "Waiting...", ConsoleColor.DarkGray);
            }
            else
            {
                DrawUI($"Captured {phraseLengthMs:F0}ms.", ConsoleColor.DarkYellow, "Processing...", ConsoleColor.Magenta);

                await VRChatOsc.SetTypingAsync(true, config.Osc);

                byte[] wavBytes = WavPacker.Pack(rawPcmData);
                string translation = await OpenRouterClient.TranslateAudioAsync(wavBytes, config.Api);

                await VRChatOsc.SetTypingAsync(false, config.Osc);

                if (!string.IsNullOrEmpty(translation))
                {
                    lastTranslation = translation;
                    await VRChatOsc.SendTextAsync(translation, config.Osc);
                }

                int droppedFrames = 0;
                while (audioQueue.TryTake(out _)) { droppedFrames++; }

                speechCounter = 0;
                silenceCounter = 0;
                preRollBuffer.Clear();

                lastSystemMsg = $"Flushed {droppedFrames * 20}ms of audio ignored during processing.";

                DrawUI("Listening...", ConsoleColor.DarkGray, "Waiting...", ConsoleColor.DarkGray);
            }
        }
    }
}