# Open Media Transport (OMT) binaries

Unmodified Windows x64 binaries from the official release
https://github.com/openmediatransport/libomtnet/releases/tag/v1.0.0.19 (`OpenMediaTransport.Binaries.Release.v1.0.0.19.zip`, `Libraries/Winx64`), MIT licensed (see `LICENSE.txt`).

| file | sha256 |
|---|---|
| libomtnet.dll | a3cad1c4cc6ae873a214031423781522933a5862d1005a663be8242b0741afdd |
| libvmx.dll | effd2eb33a85689da940ab6744cf8931448445144c5d7991ac50908800af19be |

`libomtnet.dll` is referenced by `Stagecast.Rendering`; `libvmx.dll` (the VMX video codec) is copied next to the executable by `Stagecast.App`.
To update: download a newer release, replace both files, update the table and the version above.
