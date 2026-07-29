# Runtime GPU profiling

Measure the Release build on the target machine, with the dark theme, a
1180x760 focused window, closed settings drawer, and active microphone input.
Use the same microphone and pipeline for every compared run.

```powershell
dotnet publish FoxTrans.Desktop -c Release
wpr -start GPU -filemode
```

Launch `FoxTrans.exe`, start the pipeline, and speak normally for 60 seconds.
Then stop the trace:

```powershell
wpr -stop "$PWD\artifacts\foxtrans-gpu.etl"
```

Open the ETL in Windows Performance Analyzer and inspect GPU Usage for the
FoxTrans process. At the same time, record the GPU value shown for FoxTrans in
Task Manager once per five seconds. The acceptance target is an average of at
most 3% GPU in the 60-second active run. Repeat the capture at 60 Hz and at the
highest refresh rate used by the machine; the active waveform is capped at
30 FPS, so the result should not scale with display refresh rate.
