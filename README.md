# Stagecast

Windows 11 tool that renders a web page at a custom resolution (e.g. 2520x1080 for an LED videotron) independent of window size, and sends it to a chosen monitor, NDI and OMT, with audio. Internal tool.

## Layout

```
.
├── src/                      application code (empty until phase A1)
│   └── (planned) Stagecast.App, Stagecast.Core, Stagecast.Outputs
├── tests/                    test projects (empty for now)
├── spikes/                   throwaway spikes proving the WebView2 + capture path
│   └── Stagecast.Spike/
├── docs/
│   ├── PLAN.md               overview and architecture
│   ├── PLAN_CROSSCHECK.md    per-stage feasibility and licensing
│   ├── PLAN_PHASES.md        spikes S0-S12 and app phases A1-A9
│   ├── PLAN_UI.md            UI/UX plan
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

## Status

Spike S0 passes (WPF skeleton builds and opens). Next: S1.
