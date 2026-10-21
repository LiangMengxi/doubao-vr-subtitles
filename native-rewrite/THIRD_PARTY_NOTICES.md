# Third-party components

The original application implementation is licensed under GNU GPL version 3 only; see `../LICENSE`. Third-party components below retain their own licenses.

- OpenVR SDK: Valve, BSD 3-Clause license. Pinned to commit `91825305130f446f82054c1ec3d416321ace0072`. The runtime DLL and exact `OpenVR-LICENSE.txt` are included with the application.
- .NET runtime: Microsoft and contributors. Runtime license and third-party notices are copied from the exact NuGet runtime package into `out/app/licenses/` when publishing.
- Windows App SDK / WinUI 3: Microsoft. `license.txt` and `NOTICE.txt` from Microsoft.WindowsAppSDK 1.7.250606001 are copied into `out/app/licenses/` when publishing.
- CMake, MSVC, Windows SDK and SDK build tools are build-time dependencies. They remain in the local dependency cache and are not copied into the application payload by this project.

The application icon is reused from this project's original assets. No Python runtime, Python packages, or Tcl/Tk resources are bundled in this rewrite.

The PICO microphone feature independently implements a WASAPI packet-consumption approach inspired by Kill11/PicoStreamingMicrophoneKeeper. No source code, icons or binaries from that repository are included. No open-source license was declared there when checked on 2026-10-21. See `../docs/REFERENCES.md` for the acknowledgment and scope.
