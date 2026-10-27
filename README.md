# Stagecast

Windows 11 tool that renders a web page at a custom resolution (e.g. 2520x1080 for an LED videotron) independent of window size, and sends it to a chosen monitor, NDI and OMT, with audio. Internal tool.

## Layout

```
.
├── src/                      application code
│   ├── Stagecast.Core/       pure logic, no Windows APIs (scale math, resolution, presets, configuration)
│   ├── Stagecast.Rendering/  WebView2 composition hosting, Windows.Graphics.Capture, frame pump (Windows only)
│   └── Stagecast.App/        WPF application (Stagecast.exe); output modules join in A4+
├── tests/
│   └── Stagecast.Core.Tests/ xUnit
├── spikes/                   throwaway spikes proving the WebView2 + capture path
│   └── Stagecast.Spike/
├── docs/
│   ├── PLAN.md               overview and architecture
│   ├── PLAN_CROSSCHECK.md    per-stage feasibility and licensing
│   ├── PLAN_PHASES.md        spikes S0-S12 and app phases A1-A9
│   ├── PLAN_UI.md            UI/UX plan
│   ├── TESTING.md            test layers and when each batch runs
│   └── design/               Design canvas sources (ui mockup, operator storyboard)
├── scripts/                  helper scripts (run-spike.sh builds and runs from WSL)
├── Stagecast.sln
├── Directory.Build.props     shared MSBuild settings
├── global.json               pinned .NET SDK
├── .editorconfig
└── .gitattributes
```

## Build and run the spike (from WSL)

```
scripts/run-spike.sh          # copy to C:\dev\stagecast, build, run, print the log
scripts/run-spike.sh build    # build only
```

Requires the Windows .NET 8 SDK and the WebView2 runtime.

## Checks

```
scripts/check.sh typecheck    # full build, nullable analysis, warnings as errors
scripts/check.sh unit         # xUnit tests in tests/
scripts/check.sh smoke        # runs the spike and asserts its log
scripts/check.sh all
```

Run them once per batch of phases, not after every change; see `docs/TESTING.md`.

## Status

Spikes S0-S3 pass (skeleton, one WebView2, composition hosting, viewport at several sizes and simulated display scales). Next: S4 (capture one frame; early decision point).
