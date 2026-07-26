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
pipeline uses one WebSocket for the application's ordinary lifetime:

```text
NAudio microphone -> continuous PCM16LE stream (including silence)
                  -> persistent VoxtralFox WebSocket
                  -> cumulative transcript.partial
                  -> client-side logical utterances
                  -> bounded full-text translation window
                  -> latest-wins text translation
                  -> configured output sinks
```

VoxtralFox does not construct or use VAD. The transport aggregates microphone
frames into 80 ms network chunks, sends PCM continuously through speech and
silence, and keeps cumulative partial transcripts intact. Logical utterances are
entirely client-side: an utterance settles when its transcript text has not
changed for the configured interval. Duplicate partials, warnings, and other
WebSocket events do not reset that interval. Ordinary pauses neither rotate the
session nor send `input_audio.end`.

The utterance tracker records the exact settled cumulative prefix and extracts
the next lexical suffix without resetting the server transcript. Unexpected
committed-prefix changes start a safe client transcript epoch on the same
WebSocket and invalidate pending results. Translation always receives the
complete newest source window, bounded by Unicode text elements; it never
receives a token or character delta and never carries settled utterances into
the next one.

Realtime translation has one active provider request and at most one pending
candidate. New partials replace that pending slot. Minimum interval, changed
words, punctuation, maximum interval, and settlement determine eligibility.
Results are checked against the current epoch, utterance, revision, and bounded
source before publication, so stale results never reach outputs. A new logical
utterance best-effort cancels the previous utterance's request without creating
parallel calls.

Output sinks remain responsible for output-specific policy. In particular,
`VrChatOscOutput` keeps the newest 144 user-perceived text elements; source
windows, translator requests, and console output are not subject to the VRChat
limit.

Application shutdown best-effort sends `session.cancel`, consumes the cancellation
acknowledgment or close when available, and disposes the socket. An unexpected
disconnect is fatal. Automatic reconnect and audio replay are deferred because a
new connection creates a new transcript epoch whose reliability semantics need to
be explicit. Pauses never rotate the transport session.

Keep related contracts and small models together in meaningful files; do not create
one file for every small record or interface.

Configuration records describe user intent and are resolved into the small runtime
settings each adapter needs. One `config.jsonc` describes one pipeline; its kind is
inferred from its components. Semantic validation, including environment-backed
secrets, completes before microphone, HTTP, or UDP resources are created. Future
provider configuration types can therefore be valid before their adapters exist.
Providers never read configuration files or environment variables themselves.
