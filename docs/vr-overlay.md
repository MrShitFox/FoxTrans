# SteamVR overlay

FoxTrans can show Live Studio's pace information in SteamVR as a transparent,
display-only HUD locked to the headset just below the natural forward gaze. Its
typography and status language match Live Studio, while the layout keeps the
periphery clear for VR rather than reproducing a desktop window.

## Setup

1. Install SteamVR and complete its normal headset setup.
2. Start FoxTrans and open **Settings → VR overlay**.
3. Leave **Enable overlay** on (it is enabled by default), then start SteamVR.

FoxTrans does not require SteamVR at startup. While it is closed, Settings shows
**Waiting for SteamVR** and the app makes a cheap availability check every three
seconds (every thirty seconds after ten unsuccessful checks). Starting SteamVR
later attaches the overlay automatically. If SteamVR closes, translation keeps
running and FoxTrans reconnects when it is available again.

## Placement and content

The default HUD is 1.35 m wide, 1.15 m forward, level with the HMD, and 0.05 m
below its origin. This keeps the large translation near the central lower gaze
instead of placing it at the edge of the headset's field of view. Adjust width,
distance, pitch, yaw, vertical offset, and opacity in **Settings → VR overlay**.
Changes are applied without restarting the runtime.

The header, voice status, recognition, and translation can each be hidden.
Recognition is automatically omitted for the Audio LLM pipeline, just as it is
in Live Studio. The overlay has no SteamVR laser input, hit testing, or
controller controls. Translation display uses the same Unicode-safe 144 text
element formatter as the VRChat chatbox output.

## Troubleshooting

- **Waiting for SteamVR:** start SteamVR and make sure the headset is detected.
  FoxTrans will retry automatically; no app restart is required.
- **Connection needs attention:** SteamVR rejected an overlay connection. Close
  any stale FoxTrans overlay in SteamVR, then let FoxTrans retry, or toggle the
  master switch off and on.
- **Panel is uncomfortable or obscures content:** increase distance, reduce
  width, or lower the pitch/vertical offset.
- **Fuzzy or bright panel edge:** verify that the current build is being used.
  The native bridge converts Avalonia's premultiplied BGRA render target to the
  straight-alpha RGBA format required by `SetOverlayRaw`.
- **Translation area stays on its prompt:** inspect
  `%LOCALAPPDATA%\FoxTrans\vr-overlay-state.txt`. It records only event counts
  and text lengths, never recognized or translated content.

## Performance check

Use the [GPU profiling protocol](gpu-performance.md) with SteamVR connected and
speaking for 60 seconds. The desktop-only target remains ≤3% average GPU with
the overlay disabled. Record the overlay-enabled figure with the machine, GPU,
headset refresh rate, panel options, and pipeline used; lower the maximum
overlay frame rate if that figure is out of line with the desktop-only result.

FoxTrans samples state at a bounded cadence and renders only when semantic
content changes. Recognition and translation are projected directly from the
immutable runtime snapshot. The VR surface deliberately has no waveform or
continuous audio animation, because continuously replacing a raw OpenVR image
causes visible shimmer in some headsets. Raw image loads are serialized: the
previous compositor image remains visible until OpenVR reports that the next
`SetOverlayRaw` image has finished loading.

| Overlay state | Maximum state sampling rate |
| --- | --- |
| SteamVR not connected | No rendering |
| Connected | 12 fps by default |

The configured rate controls state sampling, not continuous texture rendering.

## Editing the HUD safely

`FoxTrans.Desktop/Styles/VrOverlayDesign.axaml` is the single edit surface for
VR typography, spacing, outline thickness, safe areas, and block geometry.
`VrOverlayView.axaml` contains structure and bindings only. The host must not
bind or submit continuous audio/animation frames: a texture is rendered only
after a semantic view-model revision. `VrOverlayRenderTests` locks these
invariants, including the rule that an audio-only change cannot cause another
OpenVR texture submission.
