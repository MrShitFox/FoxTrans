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
pipeline uses one WebSocket while a server session remains healthy:

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

The microphone stream is enumerated exactly once. A `RealtimeAudioPump` owns that
enumeration and outlives individual WebSocket generations. It routes frames to one
bounded active-session channel (250 frames, nominally five seconds for the NAudio
20 ms cadence). A full active channel is fatal and explicit.

`VoxtralConnectionSupervisor` creates a fresh one-session
`VoxtralFoxTranscriber` for each connection attempt. Ordinary speech pauses remain
on the same socket. After a transport failure, the pump keeps draining the live
microphone while there is no ready session. Those frames are deliberately
discarded, counted, and reported as one approximate audio gap; they are never
buffered for replay. Frames routed to an attempt but not confirmed sent are also
included in the gap.

Each successful reconnect starts a new server session, monotonic connection
generation, client transcript epoch, and logical utterance state. Connection loss
invalidates pending and active translations, suppresses stale completion, clears
old source context, and forces typing false without manufacturing a settled
translation. The last translated chatbox text remains subject to the existing
output policy.

Retry classification is conservative: network failures, unexpected closes,
shutdown/busy/backend/internal/idle-timeout errors, and capacity exhaustion are
transient; configuration, authorization, audio, sequence, malformed-protocol, and
compatibility errors are fatal. Reconnect backoff is deterministic at 1, 2, 4, 8,
then 10 seconds. Health is checked before every retry, and only one attempt/session
exists at a time. Cancellation interrupts health, handshake, or backoff.

Initial `/health` and first-session failures remain startup failures rather than
entering an endless retry loop. Automatic retry begins only after at least one
`session.created`. Application shutdown best-effort sends `session.cancel`,
consumes the acknowledgment or close when available, and disposes the socket.
Pauses never rotate the transport session.

CLI parsing, device selection, dry-run plans, and readiness formatting remain
outside providers. `check` may call only the documented Voxtral `/health`; it does
not invent health endpoints or send inference requests to OpenAI-compatible
providers.

Keep related contracts and small models together in meaningful files; do not create
one file for every small record or interface.

Configuration records describe user intent and are resolved into the small runtime
settings each adapter needs. One `config.jsonc` describes one pipeline; its kind is
inferred from its components. Semantic validation, including environment-backed
secrets, completes before microphone, HTTP, or UDP resources are created. Future
provider configuration types can therefore be valid before their adapters exist.
Providers never read configuration files or environment variables themselves.
