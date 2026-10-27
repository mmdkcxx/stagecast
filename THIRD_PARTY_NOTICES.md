# Third-party notices

Stagecast is an internal tool. It uses the components below; their licenses stay with their owners.

| Component | Use | License / notice |
|---|---|---|
| Microsoft Edge WebView2 (SDK package and Runtime) | renders the web pages | Microsoft WebView2 SDK license; the Runtime is a Windows component |
| .NET 8 and WPF (included in the self-contained build) | application platform | MIT |
| Vortice.Windows (Direct3D11, DXGI, Mathematics) | GPU frame handling | MIT |
| NAudio.Wasapi | playing audio on a local device | MIT |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | logging | Apache-2.0 |
| Open Media Transport: `libomtnet.dll`, `libvmx.dll` (in `third_party/omt`) | OMT output for vMix | MIT, copyright Open Media Transport Contributors (`third_party/omt/LICENSE.txt`) |
| NDI® runtime (not included; installed by the user from https://ndi.video) | NDI output | NDI® is a registered trademark of Vizrt NDI AB. Stagecast loads the NDI runtime installed on the machine and does not redistribute it. |

Exact package versions are in the `.csproj` files.
