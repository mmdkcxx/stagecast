# Stagecast

Windows 11 tool that renders a web page at a custom resolution (e.g. 2520x1080 for an LED videotron) independent of window size, and sends it to a chosen monitor, NDI and OMT, with audio. Internal tool.

- `docs/` planning: overview (`PLAN.md`), per-stage feasibility (`PLAN_CROSSCHECK.md`), phases (`PLAN_PHASES.md`), UI/UX (`PLAN_UI.md`)
- `design/ui/` main UI mockup sources (Design canvas `.dc.html`)
- `design/storyboard/` operator journey storyboard sources
- `spike/` throwaway spikes proving the WebView2 + capture path; build and run with `spike/run.sh` (WSL -> Windows)

Status: spike S0 passes (WPF skeleton builds and opens). Next: S1.
