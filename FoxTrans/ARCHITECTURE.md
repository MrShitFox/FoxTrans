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

Realtime preset definitions have one typed configuration catalog. A preset
resolves translation cadence, natural-pause settlement, and source-window size
together; individual advanced fields override only their corresponding resolved
value. Logical settlement commits tracker state and finalizes typing, but does
not clear the dashboard or VRChat translation. Settled display state is separate
from the active tracker state: the previous result remains visible until new
source/translation state or a transcript-epoch reset supersedes it.

The utterance tracker records the exact settled cumulative prefix and extracts
the next lexical suffix without resetting the server transcript. Unexpected
committed-prefix changes start a safe client transcript epoch on the same
WebSocket and invalidate pending results. Translation always receives the
complete newest source window, bounded by Unicode text elements; it never
receives a token or character delta and never carries settled utterances into
the next one.

Realtime translation has one active provider request and at most one pending
candidate. New partials replace that pending slot. Eligibility keeps the order
duplicate, settlement, maximum interval, minimum interval, strong punctuation,
then changed words. Strong punctuation is a small explicit set (`. ! ? ; :`,
common fullwidth forms, Arabic question/semicolon forms, and the ellipsis).
Commas and dashes are weak boundaries and fall through to the existing
changed-word rule. Quotes and brackets are not triggers by themselves, while
quotes or brackets following a newly added strong mark do not hide that mark.
An older revision of the current epoch and logical utterance may publish as an
intermediate translation while a newer source is pending. It is superseded, not
obsolete: it is the newest translated result available at that moment. A result
is obsolete only when its epoch or utterance changed, its scheduler lifecycle was
invalidated, or it is not newer than the accepted-output watermark. Accepted
translation revisions therefore move monotonically forward. A new logical
utterance best-effort cancels the previous utterance's request without creating
parallel calls.

The active request keeps an immutable snapshot of the exact source, revision,
epoch, utterance, observation time, and scheduling decision sent to the provider.
Equivalent later metadata may mark that same source as settled without changing
the requested snapshot or issuing duplicate HTTP work. Scheduler locks protect
only internal state transitions. Reporter callbacks and bounded output
submission run outside those locks, and translator scheduling is independent of
output speed. Realtime update cadence is bounded by non-streaming translator
response latency. OpenAI-compatible chat responses are not streamed token by
token.

Realtime output dispatch owns one independent worker per configured sink. Each
worker has at most one `PublishAsync` call in flight, one latest pending
translation, and a hard-bounded control queue. Pending translations are
deliberately replaced while a sink is busy; typing transitions retain sequence
order around the newest surviving translation. A slow sink cannot serialize
another sink or the translator. Translation commands retain transcript epoch,
utterance, revision, settlement, and internal sequence identity until the sink
boundary.

A new logical utterance or transcript epoch removes obsolete pending
translations and best-effort cancels an obsolete in-flight translation while
preserving required typing transitions. Realtime publication has a fixed
two-second internal timeout. A sink that still ignores cancellation after the
short grace period is quarantined for that scheduler lifetime: its bounded
pending state is cleared, it receives no concurrent or repeated calls, and other
sinks continue. Dispatcher shutdown stops acceptance, invalidates translations,
attempts typing false, cancels active calls, and waits only for a bounded period.
Output resources remain owned and disposed exactly once by the composition root.

Output sinks remain responsible for output-specific policy. In particular,
`VrChatOscOutput` keeps the newest 144 user-perceived text elements; source
windows, translator requests, and console output are not subject to the VRChat
limit.

Connection attempts have explicit prepared and active states. A prepared attempt
has a reader but receives no PCM. The validated `session.created` event is the
readiness boundary that activates its route. The first microphone enumeration
therefore begins only after initial readiness. On reconnect the same enumeration
remains open while the replacement attempt is prepared and completes its
handshake.

At the realtime-pump boundary, arbitrary capture callbacks are packetized in byte
order into 20 ms mono PCM16LE frames: 320 samples and 640 bytes at 16000 Hz.
Callback size does not define buffering duration. A small carry buffer holds less
than one normalized frame, format changes and non-block-aligned input fail
explicitly, and normal source completion emits a block-aligned final short frame.
Application cancellation may discard the incomplete carry.

The active route holds 250 normalized frames: exactly 5000 ms and 160000 PCM
bytes. Writing waits asynchronously for up to 100 ms when that five-second route
is full, so the capture callback is never blocked and brief consumer scheduling
jitter is tolerated. A sustained sender stall then fails explicitly with
connection generation, buffered duration, frames, and bytes; no audio is dropped
or retained without a bound.

`VoxtralConnectionSupervisor` creates a fresh one-session
`VoxtralFoxTranscriber` for each connection attempt. Ordinary speech pauses remain
on the same socket. After a transport failure, the pump keeps draining the live
microphone while there is no ready session. Those frames are deliberately
discarded, counted, and reported as one approximate audio gap; they are never
buffered for replay. Frames routed to an attempt but not confirmed sent are also
included in the gap. Reconnect backoff, health checks, WebSocket setup, and the
wait for the new `session.created` are all inside that same gap. Send accounting
is scoped to its attempt, so a late callback from an ended generation cannot alter
the next generation.

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
