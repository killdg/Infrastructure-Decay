# Native engine integration

## Lighting

`MapLightCache` subscribes to Unity scene loading and traverses map transforms incrementally: at most 96 nodes per update, checking a 1.5 ms elapsed budget every eight nodes. This limits traversal work; individual native calls can still exceed the budget. Outages drive cached outputs in LateUpdate after ordinary light updates. This is not a guarantee of zero frame cost.

Player, camera and loot subtrees are excluded. Return Of Thunderstorms visual roots prefixed `ROT_` are excluded to avoid taking ownership of transient weather visuals. Directional sky lighting is not intentionally dimmed.

Native `CullingLightObject.IntensityMultiplier` and `CullingAdvancedLightObject._intensityMultiplier` address map-light output. The latter has no public getter, so reflection reads its original value for restoration. Native `BaseLight.m_Intensity` and `AdvancedLight.Color` cover supported area lights. These strings refer to the installed EFT 0.16.9 API, not hidden commands.

Recognized shader channels `_EmissionVisibility`, `_EmissionColor` and `_EmissiveColor` control glow. Native `MaterialColor.ParemeterName` (spelling from EFT) can supply a fixture-specific color property. Indexed MaterialPropertyBlocks isolate material slots without mutating shared materials/textures. Original inheritance is restored when the slot had no prior override. Changes made by native lamps/other systems are adopted or preserved rather than overwritten with a stale snapshot.

Named unlit screen `_Color` suppression is experimental and developer-only. It is excluded from automatic outages because it can darken pictures rather than emitted light. Baked lightmaps and unsupported surfaces remain unchanged.

## Powered controls

Harmony patches `WorldInteractiveObject.CanStartInteraction`, `Player.StartInteraction` and `Player.ExecuteInteraction`. They reject only eligible native electrical Switch interactions while a blackout is active. Refusal occurs before native interaction locking, avoiding the old permanent-button lockout. Ordinary doors, quest counters and extraction requirements are not rewritten.

Extraction links are followed through native PreviousSwitch/NextSwitches references, bounded to 32 visited switches. The gate is evaluated at interaction time; recovery removes the refusal automatically. Already-activated extractions are not cancelled. Recovered surges leave controls usable.

## Lifetime and optional integrations

A new GameWorld resets caches, schedules and manual test flags. Returning to menus, disabling the plugin or destroying it restores owned outputs, unsubscribes scene events and removes its Harmony patches on destruction.

The optional RR check reads only the loaded RR assembly's LabWorkDirector.Active and InfrastructureDecayDirector.Active properties. It does not load/download another assembly or use IPC. The optional developer-settings adapter shares BepInEx ConfigEntry objects in process and hides diagnostic controls without the tools plugin.
