# Startup performance

Startup tracing is enabled only when `FOXTRANS_STARTUP_TRACE=1`. Each run
appends one row to
the platform local application-data folder: `%LOCALAPPDATA%\FoxTrans\startup-trace.csv`
on Windows and normally `~/.local/share/FoxTrans/startup-trace.csv` on Linux.
Durations use the process start
time as their origin, so they include native hosting work before `Main`.

## Baseline (2026-07-29)

The baseline is the Release, self-contained, composite ReadyToRun publish
before the startup and publishing changes. Runs used the same executable and
working directory. The first row is reported separately; warm values are the
median of the following nine runs.

| Mark | First run, ms | Warm median, ms |
|---|---:|---:|
| `main` | 139.893 | 58.175 |
| `app-init` | 603.596 | 491.379 |
| `services` | 691.647 | 573.887 |
| `vm` | 703.789 | 584.805 |
| `window-ctor` | 798.212 | 677.746 |
| `opened` | 868.049 | 747.941 |
| `first-frame` | 1156.266 | 1016.287 |
| `initialized` | 1230.489 | 1095.533 |

| Acceptance metric | Baseline | Trimmed single-file | Target |
|---|---:|---:|---:|
| Warm `first-frame` | 1016.287 ms | 803.579 ms | Materially lower |
| First-run `first-frame` | 1156.266 ms | 917.114 ms | No large cold-start spike |
| Publish size | 147.15 MiB | 61.77 MiB | Below baseline |
| Publish file count | 227 | 1 | 1 |

`first-frame` waits for the first Avalonia composition batch to report that it
was rendered. `DispatcherPriority.Loaded` is used only when compositor
instrumentation is unavailable.

## Stage 0 decisions

### Window transparency

The same executable was run ten times in each mode during the startup study.

| Mode | First measured run, ms | Warm median, ms |
|---|---:|---:|
| Transparent | 1156.266 | 1016.287 |
| Opaque | 1036.911 | 1021.964 |

Opaque rendering did not improve the warm startup median, so it was not
selected for that startup-only change. The transparent window remains the
default; high-refresh-rate visual work is controlled by the capped waveform
scheduler instead. The `FOXTRANS_OPAQUE_WINDOW` experiment has been removed.

### Fluent theme

A temporary build without `<FluentTheme />` produced a 1003.646 ms warm
median, a 12.641 ms improvement. Visual capture showed missing or unusable
control chrome, including the settings header and action area. The theme
therefore remains enabled.

## Reproducing measurements

Publish first:

```powershell
dotnet publish FoxTrans.Desktop -c Release
```

Set `FOXTRANS_STARTUP_TRACE=1`, launch `FoxTrans.exe` on Windows or `FoxTrans`
on Linux ten times, and close each
run after initialization. Then inspect:

```powershell
Import-Csv "$env:LOCALAPPDATA\FoxTrans\startup-trace.csv" |
    Measure-Object first_frame_ms -Average -Minimum -Maximum
```

Delete or archive the CSV between independently compared builds so rows cannot
be mixed.

## Stage 1 measurements

These measurements use the same composite ReadyToRun publication mode as the
baseline so startup-path changes can be compared without changing the
publication format at the same time.

| Variant | Warm `window-ctor`, ms | Warm `first-frame`, ms | Warm `initialized`, ms |
|---|---:|---:|---:|
| Win32/Skia/HarfBuzz with eager live view | 624.999 | 957.874 | 1029.761 |
| Deferred live view | 609.382 | 864.891 | 944.372 |
| Deferred live view, `Segoe UI` only | n/a | 869.131 | 948.827 |

Deferring `LiveStudioView` improved the target metric by 92.983 ms, so the
deferred path remains. Reducing the font fallback chain was 4.240 ms slower and
was reverted.

`SavedPlacementIsAppliedBeforeWindowOpens` also verifies headlessly that a
non-default size, position, and `WindowStartupLocation.Manual` are already set
while the window is still invisible.

## Trimmed single-file result

The selected distribution is a self-contained, uncompressed, trimmed
single-file executable with ReadyToRun enabled. The first run used an empty
working directory and therefore also created `config.jsonc` and
`foxtrans.schema.json`. The embedded schema was byte-for-byte equal to the
committed file.

| Mark | First run, ms | Warm median, ms |
|---|---:|---:|
| `main` | 71.555 | 41.869 |
| `app-init` | 559.923 | 461.934 |
| `services` | 582.959 | 486.111 |
| `vm` | 591.463 | 495.058 |
| `window-ctor` | 667.303 | 571.068 |
| `opened` | 726.675 | 618.295 |
| `first-frame` | 917.114 | 803.579 |
| `initialized` | 1015.706 | 898.932 |

On the first run the .NET host extracted exactly four native dependencies
(Skia, HarfBuzz, ANGLE, and WebRTC VAD), totalling 18.01 MiB, into its bundle
cache. Warm runs reused that cache. The warm target metric is about 21% faster
than the original composite ReadyToRun publish while reducing the delivery to
one file.
