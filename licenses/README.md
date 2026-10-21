# Current license sources

- `../LICENSE`: GNU GPL version 3 text for this project's original implementation, using SPDX identifier `GPL-3.0-only`. It is copied to application payloads as `licenses/GPL-3.0.txt` alongside the application notice.

- `InnoSetup-LICENSE.txt`: license text for the installer compiler and bootstrapper. Copied into the release payload by `native-rewrite/package-release.ps1`.
- `OpenVR-LICENSE.txt`: Valve OpenVR SDK BSD 3-Clause license. The application also includes the exact license from the pinned SDK during its build.
- .NET and Windows App SDK licenses and notices are copied from the pinned runtime packages when publishing; see `../THIRD_PARTY_NOTICES.md`.

Legacy Python and Tcl/Tk license material remains in the v0.2.4 Git tag and local backups. It is not used by the current application.
