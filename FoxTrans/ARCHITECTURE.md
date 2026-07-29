# FoxTrans architecture

FoxTrans follows **functional core, stateful adapters** across three production
projects:

- `FoxTrans` builds `FoxTrans.Core.dll`, the reusable runtime library. It owns
  configuration and plan resolution, audio capture and segmentation, providers,
  Voxtral transport, scheduling, outputs, typed telemetry, readiness checks, and
  runtime orchestration. It has no Avalonia, window, dispatcher, or console
  rendering dependency.
- `FoxTrans.Desktop` builds `FoxTrans.exe` on Windows and `FoxTrans` on Linux. It owns Avalonia
  startup, explicit desktop composition, views, view models, animation models,
  preferences, bounded GUI reduction, and window lifetime.
- `FoxTrans.Cli` builds `FoxTrans.Cli.exe` on Windows and `FoxTrans.Cli` on Linux. It owns CLI
  parsing and the rich/plain view-only terminal reporter.

Both executables reference the reusable core; neither references the other.
Their `Program.cs` files are small explicit composition roots. There is no DI
container, service locator, generic event bus, generic pipeline framework, or
base provider hierarchy.

Pure or resource-free code handles VAD state transitions, duration calculations,
WAV packing, OSC packet formatting, topology projection, GUI snapshot reduction,
Unicode streaming-text progression, and voice animation state. Classes own the
stateful edges: microphone callbacks, native WebRTC VAD, HTTP, WebSocket, UDP,
bounded queues, desktop lifetime, and terminal lifetime.

## Reusable runtime controller

`IFoxTransRuntime` is the one lifecycle boundary used by both front ends. Its
explicit state machine is `Stopped -> Starting -> Running -> Stopping -> Stopped`,
with `Faulted` for a handled terminal pipeline failure. `StartAsync` accepts a
fully resolved production plan and reporter; `StopAsync` is serialized,
idempotent, graceful, and bounded. Duplicate starts are rejected and duplicate
stops are safe.

`ProductionRuntimePipelineSessionFactory` selects the existing direct, classic,
or realtime implementation. `ProductionRuntimePipelineSession` is the single
place that composes their microphone, VAD or persistent transport, providers,
scheduler, and outputs. The desktop does not reproduce this orchestration.
Runtime lifecycle changes are typed telemetry rather than parsed log messages.

One runtime instance owns at most one session and one run task. Start
cancellation disposes any partially created session. Completion observes provider
failures, disposes the session, emits `Faulted`, and permits a later clean start.
Normal stop cancels the private run token, waits up to the bounded shutdown
interval, disposes exactly once, clears the active plan, and permits restart.
Application disposal performs the same stop path, so a closing desktop never
abandons a microphone, HTTP client, WebSocket, output sink, or realtime
supervisor.

A session that ignores cancellation is retired rather than waited on. When the
bounded shutdown interval expires the runtime emits `Faulted`, stops treating
that run as the active one, and burns its generation. Start, stop, and disposal
therefore never block on a completion that may never arrive, and the retired
run's late completion can neither move runtime state nor reach the presentation
boundary; its own resources are still released exactly once whenever it finally
finishes. A start requested while a prior run is still finishing waits only for
that same bounded interval and then reports a typed failure instead of holding
the lifecycle lock. One stuck session therefore costs the current run, never the
ability to start again or to close the window.

The current direct-audio path is:

```text
miniaudio microphone capture -> bounded normalized frame channel -> WebRTC VAD segmenter
                  -> bounded completed-segment channel
                  -> sequential OpenAI-compatible audio translator
                  -> output sinks (currently VRChat OSC)
```

The classic batch path is also executable:

```text
miniaudio microphone capture -> bounded normalized frame channel -> WebRTC VAD segmenter
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

Providers do not access Avalonia or `Console`; the selected front end owns its
presentation host. Runtime shutdown cancels capture, completes channels, waits
for workers, forces typing off, and disposes native/network resources. The CLI
host separately restores terminal state.

## Typed presentation boundary

The validated `ResolvedExecutionPlan` deterministically produces an immutable,
view-only topology. Stable semantic node and edge IDs describe direct audio,
classic batch, and Voxtral realtime pipelines, including one node per configured
output. This graph is presentation data only; production orchestration remains
the explicit code paths above and is not a generic graph executor.

`IAppReporter` remains the application-to-UI boundary. Application, audio,
provider, scheduler, and output threads publish bounded typed telemetry for audio
levels, segment-queue bookkeeping, stage operations, edge flow, realtime
identity, and output delivery. They never parse presentation strings and never
render. `PipelineTuiReducer` synchronously reduces events into an immutable
bounded snapshot: text has safety limits, recent events form a 100-entry ring,
duplicate warnings coalesce, and renderer objects never enter runtime state.
Pipeline runtime state and UI state are deliberately separate.

### Desktop reducer and UI thread

`DesktopEventBridge` implements `IAppReporter` without touching Avalonia. Its
bounded channel holds at most 256 ordinary events. High-frequency source and
translation partials use replaceable latest slots, and audio frames use a
separate latest-only cell, so neither pipeline work nor an audio callback waits
for the dispatcher. A background pump applies the pure
`DesktopRuntimeReducer` and publishes the newest immutable snapshot.

The main window owns one centralized animation/update clock. At each visible
tick it applies only the newest snapshot and audio frame on the UI thread,
advances both text presenters, updates the voice animation uniforms, and eases
the settings drawer. It uses approximately display cadence while active, lowers
cadence while inactive, performs no render update while hidden/minimized, and
stops the clock when the window closes. There is no task per frame, animation,
audio sample, or grapheme.

Operation identity, transcript epoch, utterance, revision, and runtime lifecycle
remain typed through reduction. Completion from a stale operation cannot
overwrite a newer active operation. Source/translation partials may coalesce but
the newest value always wins. Runtime errors remain in the snapshot and on the
affected typed stage; a fatal failure transitions the controller to `Faulted`
while the window stays open and permits Start again.

The window is closable at all times. Its closing sequence saves placement, runs
one idempotent bounded shutdown, and closes whether that shutdown succeeded,
failed, or exceeded its bound; a repeated close request joins the sequence
already running instead of starting another. Shutdown itself never propagates: a
stop that fails is already reported as a fault, and every owner is then released
independently so one failing disposal cannot leave a microphone or socket
running. The same bounded, non-propagating shutdown runs on the application exit
path, off the dispatcher because Avalonia raises `Exit` synchronously. Every
command reachable from the shell — start/stop, save, device refresh, microphone
test — contains its own failures, because an exception escaping an
`AsyncRelayCommand` is rethrown on the UI synchronization context and would
terminate the application rather than surface a message.

Notices in the corner are transient by default: informational and warning
notices clear themselves after a bounded interval and a fresh Start clears the
previous run's notice. Only errors persist until dismissed, because they
describe state the user still has to act on.

`DesktopSecretRedactor` operates before presentation state is stored. Resolved
credential values, Authorization headers, large base64 payloads, prompts,
endpoint userinfo, and secret query values are not display data. Effective
configuration uses only configured/missing indicators and sanitized endpoint
identity.

### Latest-only audio visualization

`Pcm16AudioFeatureExtractor` observes the existing mono PCM16LE delivery path
without modifying or delaying it. It reuses one normalized 512-sample window,
precomputed spectral coefficients, and fixed-size 12-element result frames.
Roughly every 25 milliseconds it calculates RMS, peak, clipping, speech-active
state, and 12 normalized frequency bands, then replaces the single latest
`AudioVisualFrame`. Unsupported formats report unsupported honestly. Raw PCM is
not retained or recorded.

`VoiceWaveformAnimationModel` converts those features plus a typed dominant
`Idle/Listening/Speech/Processing/Success/Error/Stopping` mode into twelve
bounded bar heights and a small state-effect record. RMS supplies the common
energy envelope while each spectral band remains independent. A visual-only
calibration tracks background level and the active-speech reference, then
normalizes spectral shape relative to the current frame. Different microphone
gains therefore produce comparable visible activity without modifying pipeline
audio. A roughly 45 ms attack and 160 ms release keep speech responsive without
snapping. The model retains only its fixed twelve-value buffer and no PCM.

`VoiceWaveformControl` is the rendering boundary. It uses standard Avalonia
rounded rectangles and two brushes; the surrounding XAML border supplies the
translucent surface, thin border, and one soft shadow. Processing is a bounded
left-to-right highlight, success and error produce one short pulse, and Stopping
fades. Standard Avalonia drawing is the only rendering path.

Reduced motion disables sweeps and pulses while preserving functional amplitude
feedback and short state fades.

### Unicode streaming text

`StreamingTextAnimator` keeps one latest target, not a history of partials. It
segments old and new values with Unicode text elements, retains their longest
stable common prefix, and progressively reveals only a genuinely new suffix.
Small corrections replace the unstable suffix; large replacements use a short
crossfade. Newer updates coalesce, catch-up speed increases behind a pending
target, and the final target is forced to convergence within a bounded interval.
It never starts a task per character and never splits surrogate pairs, combining
sequences, or ZWJ emoji families. A new typed utterance begins a bounded exit
transition for the old source and translation before the empty state and next
grapheme reveal; the prior translation is not retained as a permanent Live
block.

### Desktop composition and preferences

Avalonia starts before configuration resolution. `DesktopApplicationServices`
explicitly constructs preferences, bootstrap resolution, the runtime controller,
and event bridge. A missing default config is created through the normal core
workflow; invalid configuration becomes a desktop bootstrap result rather than
an application exit. The near-black custom-chrome shell is created first and
configuration loading runs after the window opens. The main window always remains
usable; missing or invalid configuration opens the settings drawer and runtime
start is available only after a valid plan exists.

The desktop is one Live composition rather than application navigation. The
resolved pipeline/model identity, waveform rail, typed status, current recognition,
and current translation share one canvas. Recognition is collapsed for direct
Audio LLM plans. Settings is an overlay drawer with its own small section list;
it does not resize or replace Live and contains no duplicate runtime pipeline.

`SettingsViewModel` and its small editor view models project the existing
`FoxTransConfig` records into guided controls for Audio LLM, classic Whisper +
LLM, and Voxtral + LLM. Mode selection controls field visibility but does not
change the running plan before Save. Device testing uses a separately owned
portable native audio source and the same PCM feature extractor, without creating provider
requests. Credential editors preserve the existing literal or `env:NAME`
representation, and secret redaction applies to feedback and diagnostics.

`DesktopConfigurationStore` converts the editor back to the typed config,
validates it with the existing validator/resolver, serializes deterministic
canonical JSONC, writes a sibling temporary file, keeps one stable `.bak`, and
atomically replaces the user file. It then reloads and resolves the written
file before the desktop accepts it. Failure leaves the prior file intact. Save
while active is explicitly Save & Restart: runtime shutdown completes before
the resolved replacement plan starts. Providers remain independent of Avalonia,
and no UI save/render operation runs on an audio or provider path.

The desktop uses compiled XAML bindings with an `x:DataType` on every view.
Reusable theme resources define the dark and light palettes, typography,
spacing, radii, borders, shadows, and state colors. Appearance follows system,
dark, or light preference. Window placement, appearance, and reduced motion live
in a separate local desktop-preferences file and never alter the versioned
pipeline configuration schema.

### CLI reporter

`PipelineTuiHost` owns one latest snapshot, a coalescing invalidation signal, and
one Spectre live-render task. The rich TUI is view-only: it has no input loop,
keyboard map, selected node, details overlay, log toggle, or shutdown callback.
The reducer contains no interaction state. Topology is always rendered vertically
in deterministic `PipelineViewDefinition.Nodes`/`Edges` order; every stage card
shows effective non-secret settings and live state. Pulse and edge animation are
derived from snapshot timestamps and current render time, not stored as state.
`Report` performs only a short state update and signal; it cannot wait for terminal
I/O. One render task owns live refresh, cursor/style lifecycle, resize handling,
and a maximum 6 FPS animation cap. High-frequency partials therefore replace
the visible snapshot rather than queueing frames. Renderer code receives an
`IAnsiConsole` through composition and converts snapshots to escaped Spectre
renderables without touching application state.

Redirected, explicitly plain, non-ANSI, unavailable, or very small terminals use
bounded append-only text events with no clearing or cursor control. An
unrecoverable resize, console-handle, or rich-render exception ends the live
region, restores terminal state, emits one warning, and switches the same host to
plain output without cancelling translation. UI disposal never initiates
application cancellation; Ctrl+C remains owned by the composition root's
existing graceful cancellation path.

Direct and batch paths share capture, segmentation, bounded queues, outputs, and
reporting. Batch network processing is sequential, while capture and VAD continue
and later phrases wait in the bounded completed-segment queue. OpenAI-compatible
providers share only small protocol helpers (authentication, HTTP/error handling,
and chat parsing), not a base-provider hierarchy. HTTP providers receive resolved
runtime settings and never read configuration or environment variables.

Classic transcription request encoding is selected in resolved transcription
settings and is independent of VAD and pipeline orchestration. Multipart file
uploads and JSON/base64 `input_audio` requests use the same WAV packer, HTTP send
path, error handling, and transcription response parser.

`IStreamingTranscriber` owns continuous speech transport. The beta VoxtralFox
pipeline uses one WebSocket while a server session remains healthy:

```text
miniaudio microphone capture -> continuous PCM16LE stream (including silence)
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

VAD phrase presets and realtime translation presets are independent configuration
domains. The typed VAD catalog resolves WebRTC phrase segmentation settings for
direct and classic batch adapters; canonical names are `short-phrases`,
`natural-speech`, and `long-phrases`. Deprecated VAD aliases exist only for
configuration compatibility. Adapters consume resolved settings, never preset
names. Realtime preset definitions have their own typed configuration catalog. A preset
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

## Performance and verification constraints

The audio callback does fixed-buffer analysis and a latest-value exchange only;
it never dispatches to Avalonia. The event channel, reducer histories, realtime
audio routes, text targets, output workers, and completed-segment queues all have
explicit bounds. Neither desktop presentation nor terminal rendering introduces
pipeline backpressure. Raw audio, unlimited transcript history, and historical
visual frames are never retained.

Core/runtime tests use fake sessions to prove restart, cancellation, fault
recovery, bounded completion, and exactly-once disposal for direct, classic, and
realtime resources. PCM feature tests use deterministic silence and sine waves.
Desktop model tests use fake time for every waveform mode and streaming-text
convergence. Avalonia headless tests render the real application at 1440x900,
1180x760, and 900x620 in dark and light themes and exercise custom chrome,
drawer composition, start/stop state, configuration failure, Audio LLM
recognition omission, text wrapping, and secret absence. Configuration tests
round-trip all three modes, dynamic fields, credentials, multiple outputs,
backup/atomic replace, and post-save resolution. Waveform tests cover bar
mapping, separated bands, mode transitions, reduced motion, bounded effects,
and latest-only retained state; screenshot review verifies visual
composition but is not used as a substitute for those correctness tests.
