# Build FoxTrans

FoxTrans contains a reusable .NET runtime, an Avalonia Desktop app, a terminal
CLI, and a small bundled native library for microphone capture and WebRTC VAD.
The normal publish output is self-contained, trimmed, ReadyToRun, and
single-file: end users do not need a .NET runtime installed.

## Platform rule

Build each runtime identifier on the matching host:

- Build `win-x64` on Windows.
- Build `linux-x64` on Linux.

The project enforces this because the native microphone/VAD library is compiled
by CMake for the host runtime. Cross-publishing `win-x64` from Linux or
`linux-x64` from Windows is intentionally rejected.

## Prerequisites

### All build hosts

- Git.
- .NET SDK **10.0.302** or a later .NET 10 feature-band SDK accepted by
  `global.json` (`rollForward: latestFeature`). No .NET workload installation is
  required.
- CMake **3.21+**.
- Internet access for the first NuGet restore.

### Windows x64

Install Visual Studio Build Tools or Visual Studio with the **Desktop development
with C++** workload (MSVC x64/x86 build tools and a Windows SDK). CMake must be
available on `PATH`; the Visual Studio installer, Kitware installer, or `winget`
can provide it.

Open a Developer PowerShell for Visual Studio or a terminal where MSVC and CMake
are already discoverable, then verify:

```powershell
dotnet --version
cmake --version
cl
```

### Ubuntu/Linux x64

On Ubuntu, install the .NET 10 SDK from the supported Ubuntu feed and the native
build toolchain:

```bash
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0 build-essential cmake
```

For the Desktop app to run, install or retain a graphical desktop session and
these usual Avalonia runtime libraries:

```bash
sudo apt-get install -y libx11-6 libice6 libsm6 libfontconfig1
```

The active microphone also needs an accessible PipeWire/PulseAudio or ALSA
service. On Wayland, the current Desktop app uses the usual XWayland
compatibility path. See the official [Ubuntu .NET installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install) and
[Avalonia Linux deployment guide](https://docs.avaloniaui.net/docs/deployment/linux)
if your distribution uses different package names.

Verify the toolchain:

```bash
dotnet --version
cmake --version
cc --version
```

## Restore, build, and test

Clone the repository and run these commands from its root:

```powershell
dotnet restore FoxTrans.slnx --locked-mode
dotnet build FoxTrans.slnx -c Release --no-restore
dotnet test FoxTrans.slnx -c Release --no-restore
```

Use the same commands in Bash on Linux. `dotnet build` automatically invokes
CMake for the matching native library; do not build or copy that library by hand.

## Publish single-file binaries

Each command restores when necessary, compiles the matching native library, and
writes the self-contained output to `artifacts/`. Run Windows commands on
Windows and Linux commands on Linux.

### Desktop (primary app)

```powershell
# Windows x64 — produces artifacts/FoxTrans-Desktop-win-x64/FoxTrans.exe
dotnet publish FoxTrans.Desktop/FoxTrans.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts/FoxTrans-Desktop-win-x64 -p:TrimmerSingleWarn=false
```

```bash
# Linux x64 — produces artifacts/FoxTrans-Desktop-linux-x64/FoxTrans
dotnet publish FoxTrans.Desktop/FoxTrans.Desktop.csproj -c Release -r linux-x64 --self-contained true -o artifacts/FoxTrans-Desktop-linux-x64 -p:TrimmerSingleWarn=false
```

The Windows publish directory contains `FoxTrans.exe`. The Linux desktop binary
is `FoxTrans`; its publish directory additionally contains optional
`foxtrans.desktop.in`, `install-desktop-entry.sh`, and `icons/` assets for
desktop integration. From that output directory, install the launcher for the
current user with:

```bash
sh ./install-desktop-entry.sh
```

Run this again after moving the published binary because the generated launcher
points to that exact copy.

### Optional advanced CLI (headless and diagnostics)

```powershell
# Windows x64 — produces artifacts/FoxTrans-Cli-win-x64/FoxTrans.Cli.exe
dotnet publish FoxTrans.Cli/FoxTrans.Cli.csproj -c Release -r win-x64 --self-contained true -o artifacts/FoxTrans-Cli-win-x64 -p:TrimmerSingleWarn=false
```

```bash
# Linux x64 — produces artifacts/FoxTrans-Cli-linux-x64/FoxTrans.Cli
dotnet publish FoxTrans.Cli/FoxTrans.Cli.csproj -c Release -r linux-x64 --self-contained true -o artifacts/FoxTrans-Cli-linux-x64 -p:TrimmerSingleWarn=false
```

No archive is created by these commands. Package the listed publish directory
only after confirming the executable works on its target OS.

## Maintainer references

- [Architecture](../FoxTrans/ARCHITECTURE.md)
- [Startup performance](startup-performance.md)
- [GPU profiling](gpu-performance.md)
- [Desktop usage](desktop.md)
- [CLI and JSONC configuration](cli-config.md)
