# FoxTrans architecture

FoxTrans follows **functional core, stateful adapters**. Pure or resource-free code
handles VAD state transitions, duration calculations, WAV packing, and OSC packet
formatting. Classes own the stateful edges: microphone callbacks, native WebRTC VAD,
HTTP, UDP, bounded queues, and the console.

`Program.cs` is the explicit composition root. There is no DI container, service
locator, generic pipeline framework, or base provider hierarchy.

The current direct-audio path is:

```text
NAudio microphone -> bounded frame channel -> WebRTC VAD segmenter
                  -> bounded completed-segment channel
                  -> sequential OpenAI-compatible audio translator
                  -> output sinks (currently VRChat OSC)
```

The classic batch path is also executable:

```text
NAudio microphone -> bounded frame channel -> WebRTC VAD segmenter
                  -> bounded completed-segment channel
                  -> sequential OpenAI-compatible transcription -> text translation
                  -> output sinks
```

- `IAudioSource` produces typed PCM frames and exposes their format.
- `IAudioSegmenter` turns frames into speech lifecycle updates and completed segments.
- `IAudioTranslator` translates one completed audio segment.
- `IBatchTranscriber` converts one completed audio segment to source text.
- `ITextTranslator` converts complete source text to translated text.
- `IOutputSink` publishes translations and typing changes.
- `IAppReporter` is the only boundary for application diagnostics and UI state.

Both queues are bounded. A full completed-segment queue reports a diagnostic and
waits, applying backpressure instead of dropping a phrase. The microphone callback
cannot wait safely; if its bounded frame queue fills, capture stops with an explicit
overflow error. Audio loss is never silent. HTTP translation runs on a separate
sequential consumer, so it does not pause VAD or clear audio captured meanwhile.

Providers do not access `Console`; `ConsoleUi` owns the dashboard. Shutdown cancels
capture, completes channels, waits for both workers, forces typing off, disposes
native/network resources, and restores console state.

Direct and batch paths share capture, segmentation, bounded queues, outputs, and
reporting. Batch network processing is sequential, while capture and VAD continue
and later phrases wait in the bounded completed-segment queue. OpenAI-compatible
providers share only small protocol helpers (authentication, HTTP/error handling,
and chat parsing), not a base-provider hierarchy. HTTP providers receive resolved
runtime settings and never read configuration or environment variables.

`IStreamingTranscriber` owns continuous speech transport. The beta VoxtralFox
transport uses one WebSocket for the application's ordinary lifetime:

```text
NAudio microphone -> continuous PCM16LE stream (including silence)
                  -> persistent VoxtralFox WebSocket
                  -> cumulative transcript.partial preview
```

VoxtralFox does not construct or use VAD. The transport aggregates microphone
frames into 80 ms network chunks, sends PCM continuously through speech and
silence, and keeps cumulative partial transcripts intact. It deliberately has no
utterance-boundary, transcript-delta, translation-window, or output policy.
Ordinary pauses neither rotate the session nor send `input_audio.end`.

Application shutdown best-effort sends `session.cancel`, consumes the cancellation
acknowledgment or close when available, and disposes the socket. An unexpected
disconnect is fatal. Automatic reconnect and audio replay are deferred because a
new connection creates a new transcript epoch whose reliability semantics need to
be explicit. Realtime translation scheduling and output publication belong to the
next session; the current realtime branch is an honestly labelled source-transcript
transport preview.

Keep related contracts and small models together in meaningful files; do not create
one file for every small record or interface.

Configuration records describe user intent and are resolved into the small runtime
settings each adapter needs. One `config.jsonc` describes one pipeline; its kind is
inferred from its components. Semantic validation, including environment-backed
secrets, completes before microphone, HTTP, or UDP resources are created. Future
provider configuration types can therefore be valid before their adapters exist.
Providers never read configuration files or environment variables themselves.
