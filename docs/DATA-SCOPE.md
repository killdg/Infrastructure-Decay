# Network and file scope

## In-game plugin

- No HTTP, sockets, telemetry, analytics, update checker or asset/code downloads.
- No shell/process launching, services, scheduled jobs, file watchers or background workers.
- No profile, inventory or system-file editing.
- BepInEx owns `BepInEx/config/com.killdg.infrastructuredecay.cfg` and normal local log output.
- Optional DG Testing Tools stores visible diagnostic preferences in its own BepInEx configuration. Without it, private defaults remain memory-only; the adapter constructs a path under the config directory but never saves that defaults file.
- Ordinary logs report outage state, pattern, duration and output counts. With optional developer tools, a bounded material survey may log up to 100 local map material/shader identifiers and 32 shader property names per material. Nothing is uploaded.

## Repository tooling

Build scripts read an explicitly supplied local SPT test installation as compilation references. Release scripts write only this repository's ignored `artifacts` directory and read local Git metadata. Installing is a separate manual copy of the BepInEx folder. GitHub publication is performed by the maintainer's GitHub CLI outside the game plugin; no GitHub access or credentials are embedded in the mod.
