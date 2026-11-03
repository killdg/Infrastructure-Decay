# Infrastructure Decay

**The lights are on. For now.**

Bring an unstable electrical grid to Tarkov. Lights flicker sharply, buildings lose power, and electrical buttons stop responding until the grid recovers. Sometimes a brief surge passes and the lights stay on.

## Features

- Eight blackout flicker patterns and six recovered-surge patterns.
- Daytime and nighttime power interruptions, including Labs and Factory.
- Supported map lights, fixture glow and recognized screen/sign emission dim during outages.
- Optional temporary blocking of light switches, powered exit controls and other electrical buttons.
- Player flashlights and ordinary manual doors remain usable.

## Easy setup

Copy **BepInEx** from the download into your SPT game folder. The mod starts enabled. Press **F1 → Infrastructure Decay** in your Configuration Manager to adjust it. If your installation has no F1 menu provider, edit `BepInEx/config/com.killdg.infrastructuredecay.cfg` while Tarkov is closed.

## Defaults

First roll after **3–5 minutes**, then every **3–6 minutes** after recovery or a failed roll. **60% chance at night / 20% by day**. Blackouts last **30–120 seconds**. After the first successful blackout, **35% of successful events** recover as a brief flicker instead. Control-blocking options are on by default. Settings are saved.

## Requirements and compatibility

For **SPT 4.1.6 / EFT 0.16.9.5.40743**. Client plugin only—no server DLL. No additional gameplay mod required. The F1 Configuration Manager menu provider is not bundled. Optional DG Testing Tools is not required or included.

Developed in an SPT testing installation without Fika or third-party gameplay mods; our own RR/weather/tools plugins were present during some tests. Compatibility with other mods is not guaranteed. **Fika synchronization is not implemented.**

## Experimental release

67 automated checks pass. Live Labs tests showed light suppression and button refusal, but full latest-version button recovery, day/night balance and map coverage still need testing. Baked lighting and unsupported screens may remain bright; this is not a promise of complete darkness. Electrical controls using other native systems may be unaffected. Already-active extractions are not cancelled.

## Source and credits

Created by **killdg**, with **ChatGPT assistance**. Public, readable source and release provenance: [GitHub](https://github.com/killdg/Infrastructure-Decay). No obfuscation, telemetry, network connections or system-file changes in the plugin. MIT license.
