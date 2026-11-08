using Killdg.DeveloperTools;
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace InfrastructureDecay
{
    [DefaultExecutionOrder(10010)]
    [BepInDependency(DeveloperSettings.PluginId, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin("com.killdg.infrastructuredecay", "Infrastructure Decay", "1.0.6.1")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private static Plugin? instance;
        private readonly MapLightCache lights = new();
        private readonly OutageSchedule schedule = new();
        private readonly Harmony harmony = new("com.killdg.infrastructuredecay");
        private GameWorld? world;
        private ConfigEntry<bool> enabledSetting = null!;
        private ConfigEntry<bool> blockLights = null!;
        private ConfigEntry<bool> blockExtracts = null!;
        private ConfigEntry<bool> blockOther = null!;
        private ConfigEntry<bool> test = null!;
        private ConfigEntry<bool> testSurge = null!;
        private ConfigEntry<int> recoveredChance = null!;
        private ConfigEntry<int> firstMinimum = null!;
        private ConfigEntry<int> firstMaximum = null!;
        private ConfigEntry<int> repeatMinimum = null!;
        private ConfigEntry<int> repeatMaximum = null!;
        private ConfigEntry<int> durationMinimum = null!;
        private ConfigEntry<int> durationMaximum = null!;
        private ConfigEntry<int> nightChance = null!;
        private ConfigEntry<int> dayChance = null!;
        private ConfigEntry<int> remainingBrightness = null!;
        private ConfigEntry<string> status = null!;
        private bool controlling;
        private float nextDecision;
        private float nextMessage;
        private PropertyInfo? rouletteLabActive;
        private PropertyInfo? rouletteDecayActive;
        private bool rouletteResolved;
        private bool firstEvent = true;
        private bool recoveredSurge;
        private int pattern;
        private float flickerSeconds;
        private ConfigEntry<bool> manualSourceTests = null!;
        private ConfigEntry<bool> restoreSources = null!;
        private ConfigEntry<string> sourceCounts = null!;
        private readonly Dictionary<LightSourceGroup, ConfigEntry<bool>> sourceTests =
            new Dictionary<LightSourceGroup, ConfigEntry<bool>>();
        private bool testingSources;
        private Func<LightSourceGroup, float>? sourceTestGain;
        private float nextSourceStatus;

        internal static bool PowerOut => instance?.controlling == true && !instance.testingSources
            && instance.schedule.Active && !instance.recoveredSurge;
        internal static bool BlockLightControls => PowerOut && instance!.blockLights.Value;
        internal static bool BlockExtractControls => PowerOut && instance!.blockExtracts.Value;
        internal static bool BlockOtherPoweredControls => PowerOut && instance!.blockOther.Value;
        internal static bool IsLabs => instance?.world?.LocationId == "laboratory";
        internal static void Trace(string text) => instance?.Logger.LogInfo(text);

        private void Awake()
        {
            DeveloperSettings.Initialize("Infrastructure Decay");
            instance = this;
            enabledSetting = Config.Bind("1. General",
                 "Enabled",
                 true,
                 "Power outages during day and night raids, including Labs and Factory. Press F1 for these settings.");
            blockLights = Config.Bind("1. General",
                 "Disable light switches during outages",
                 true,
                 "Blocks native switches connected to lamp fixtures. Restores access when power returns.");
            blockExtracts = Config.Bind("1. General",
                 "Disable powered exit controls during outages",
                 true,
                 "Blocks native extraction switches and their linked switch chains, including Labs exit buttons. "
                    + "An extraction already activated is not cancelled.");
            blockOther = Config.Bind("1. General",
                 "Disable other electrical buttons during outages",
                 true,
                 "Blocks other native electrical switches while the power is out. Ordinary manual doors remain usable.");
            firstMinimum = Seconds("First outage earliest", 180, 0, 1800);
            firstMaximum = Seconds("First outage latest", 300, 0, 1800);
            durationMinimum = Seconds("Outage duration minimum", 30, 5, 600);
            durationMaximum = Seconds("Outage duration maximum", 120, 5, 600);
            repeatMinimum = Seconds("Repeat roll earliest", 180, 10, 1800);
            repeatMaximum = Seconds("Repeat roll latest", 360, 10, 1800);
            nightChance = Config.Bind("2. Timing", "Night power event chance percent", 60,
                new ConfigDescription("Chance at each scheduled window during 20:00-06:00, including the opening window.",
                    new AcceptableValueRange<int>(0, 100)));
            dayChance = Config.Bind("2. Timing", "Day power event chance percent", 20,
                new ConfigDescription("Chance at each scheduled window outside night hours, including the opening window.",
                    new AcceptableValueRange<int>(0, 100)));
            recoveredChance = Config.Bind("3. Appearance",
                 "Recovered surge chance percent",
                 35,
                 new ConfigDescription("After a successful repeat roll, this chance produces a few flickers and recovery instead of an "
                    + "outage. The first successful automatic event remains a full outage. Recovered surges leave "
                    + "powered controls usable.", new AcceptableValueRange<int>(0, 100)));
            remainingBrightness = Config.Bind("3. Appearance",
                 "Remaining light percent",
                 0,
                 new ConfigDescription("Zero switches controllable light output off. Higher values make outages less dark. Baked lighting is unchanged.",
                 new AcceptableValueRange<int>(0,
                 50)));
            test = DeveloperSettings.Bind(Config,
                 "4. Testing",
                 "Trigger one outage",
                 false,
                 "Momentary action in an eligible raid. Resets itself after use.");
            testSurge = DeveloperSettings.Bind(Config,
                 "4. Testing",
                 "Trigger recovered surge",
                 false,
                 "Momentary flicker-only test. Uses one of six patterns and leaves power controls usable.");
            status = DeveloperSettings.Bind(Config,
                 "4. Testing",
                 "Status",
                 "Waiting for an eligible raid",
                 "Local diagnostic status only; does not control gameplay.");
            manualSourceTests = DeveloperSettings.Bind(Config, "5. Light source tests", "Manual source tests", false,
                "In raid: suspend automatic outages and use the source toggles below. These tests only change "
                    + "visuals; exit controls stay powered. Turn off to restore sources and restart the normal outage "
                    + "schedule.");
            restoreSources = DeveloperSettings.Bind(Config, "5. Light source tests", "Restore all sources", false,
                "Momentary action: clear all source tests and return to automatic operation.");
            sourceCounts = DeveloperSettings.Bind(Config, "5. Light source tests", "Discovered source counts", "No raid scanned",
                "Counts are controllable outputs/material slots, not physical lamps. A zero indicates no "
                    + "matching source found in this map scan.");
            BindSourceTest(LightSourceGroup.OrdinaryLights, "01 - Darken ordinary lights",
                "Unity point/spot lights: emitted illumination, without changing lamp meshes.");
            BindSourceTest(LightSourceGroup.CulledLights, "02 - Darken native managed lights",
                "Lights whose intensity is controlled by Tarkov's culling component.");
            BindSourceTest(LightSourceGroup.AreaLights, "03 - Darken area and advanced lights",
                "Native area/advanced light output, including managed area lights.");
            BindSourceTest(LightSourceGroup.FixtureEmission, "04 - Darken fixture glow",
                "Recognized emission channels on map fixture material slots.");
            BindSourceTest(LightSourceGroup.MonitorEmission, "05 - Darken monitors and screens",
                "Screen/monitor/display/LCD emission, excluding animated displays and named signs.");
            BindSourceTest(LightSourceGroup.AnimatedDisplays, "06 - Darken animated displays",
                "Animated emissive shader output, such as the parking LCD and animated monitor glow.");
            BindSourceTest(LightSourceGroup.NeonSigns, "07 - Darken neon and signs",
                "Recognized emission on named neon/sign objects. Ordinary non-glowing sign textures remain unchanged.");
            BindSourceTest(LightSourceGroup.ExperimentalUnlitScreens, "08 - Darken experimental unlit screens",
                "Named unlit screen materials exposing a color tint. Diagnostic only; excluded from automatic "
                    + "outages. May not cover every projection/video shader.");
            ClearSourceTests();
            sourceTestGain = group => sourceTests[group].Value ? 0 : 1;

            ResolveRouletteLighting();
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Infrastructure Decay 1.0.6.1 loaded. Local client only; no network access. F1 opens settings.");
        }

        private void ResolveRouletteLighting()
        {
            // Optional metadata lookup only; no RR binary reference. BepInEx may
            // construct this plugin before RR, so wait for a real instance rather
            // than dereferencing an uninitialized entry during Awake.
            if (rouletteResolved || !Chainloader.PluginInfos.TryGetValue(
                "com.killdg.raidroulette.testing", out var roulette) || roulette.Instance == null)
            {
                return;
            }

            var assembly = roulette.Instance.GetType().Assembly;
            rouletteLabActive = assembly.GetType("RaidRoulette.TestingClient.LabWorkDirector")?
                .GetProperty("Active", BindingFlags.Static | BindingFlags.NonPublic);
            rouletteDecayActive = assembly.GetType("RaidRoulette.TestingClient.InfrastructureDecayDirector")?
                .GetProperty("Active", BindingFlags.Static | BindingFlags.NonPublic);
            rouletteResolved = true;
        }

        private void BindSourceTest(LightSourceGroup group, string name, string description)
        {
            sourceTests.Add(group, DeveloperSettings.Bind(Config, "5. Light source tests", name, false,
                "Turn this group dark while Manual source tests is enabled. " + description
                + " Resets between raids."));
        }

        private void ClearSourceTests()
        {
            if (manualSourceTests != null)
            {
                manualSourceTests.Value = false;
                restoreSources.Value = false;
            }

            foreach (ConfigEntry<bool> entry in sourceTests.Values)
            {
                entry.Value = false;
            }

            testingSources = false;
        }

        private ConfigEntry<int> Seconds(string name, int value, int minimum, int maximum)
        {
            return Config.Bind("2. Timing", name + " seconds", value,
                new ConfigDescription("Saved timing in real raid seconds. Reversed minimum/maximum values are normalized.",
                     new AcceptableValueRange<int>(minimum,
                     maximum)));
        }

        private static float Between(ConfigEntry<int> minimum, ConfigEntry<int> maximum)
        {
            return Random.Range(Math.Min(minimum.Value, maximum.Value), Math.Max(minimum.Value, maximum.Value));
        }

        private void Update()
        {
            ResolveRouletteLighting();
            var current = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (current is HideoutGameWorld)
            {
                current = null;
            }
            if (!ReferenceEquals(world, current))
            {
                Reset();
                world = current;
            }

            var game = Singleton<AbstractGame>.Instantiated ? Singleton<AbstractGame>.Instance : null;
            bool running = current != null && game?.GameTimer?.StartDateTime != null
                && game.Status is not (GameStatus.Stopped or GameStatus.Stopping or GameStatus.SoftStopping);
            bool night = TODSkyProvider.IsAvailable &&
                (TODSkyProvider.Instance.Cycle.Hour >= 20 || TODSkyProvider.Instance.Cycle.Hour < 6);
            bool rouletteOwnsLights = running && (rouletteLabActive?.GetValue(null) is true || rouletteDecayActive?.GetValue(null) is true);
            if (!enabledSetting.Value || !running
                || rouletteOwnsLights)
            {
                if (controlling)
                {
                    Reset();
                }
                status.Value = rouletteOwnsLights ? "Raid Roulette owns lighting" : "Waiting for an eligible raid";
                return;
            }

            controlling = true;
            lights.Discover();
            if (restoreSources.Value)
            {
                ClearSourceTests();
                lights.Restore();
                schedule.Reset();
                firstEvent = true;
            }

            if (testingSources != manualSourceTests.Value)
            {
                lights.Restore();
                schedule.Reset();
                testingSources = manualSourceTests.Value;
                firstEvent = true;
            }

            if (Time.unscaledTime >= nextSourceStatus)
            {
                nextSourceStatus = Time.unscaledTime + 2;
                sourceCounts.Value = lights.DescribeGroups();
            }

            if (testingSources)
            {
                test.Value = false;
                testSurge.Value = false;
                status.Value = "Manual source tests: selected groups dark; automatic outages suspended";
                return;
            }
            if (Time.unscaledTime < nextDecision)
            {
                return;
            }
            nextDecision = Time.unscaledTime + 0.1f;
            double now = game!.GameTimer.PastTime.TotalSeconds;
            bool changed;
            bool manualOutage = test.Value;
            bool manualSurge = testSurge.Value;
            if (manualOutage || manualSurge)
            {
                test.Value = false;
                testSurge.Value = false;
                schedule.Start(now, Between(durationMinimum, durationMaximum));
                changed = true;
            }
            else
            {
                changed = schedule.Tick(now, Between(firstMinimum, firstMaximum),
                    Between(repeatMinimum, repeatMaximum), Between(durationMinimum, durationMaximum),
                    (night ? nightChance.Value : dayChance.Value) / 100d, Random.value);
            }

            if (changed)
            {
                if (schedule.Active)
                {
                    recoveredSurge = manualSurge || (!manualOutage && !firstEvent && Random.value < recoveredChance.Value / 100f);
                    firstEvent = false;
                    pattern = Random.Range(0,
                         recoveredSurge ? PowerFlickerPatterns.RecoveredCount : PowerFlickerPatterns.OutageCount);
                    flickerSeconds = recoveredSurge ? Random.Range(0.8f, 1.5f) : Random.Range(0.65f, 1.3f);
                    if (recoveredSurge)
                    {
                        schedule.Start(now, flickerSeconds);
                    }
                }
                // Power return is restored by LateUpdate through the same
                // time-budgeted sweep, avoiding a full restore pass in one frame.
                Logger.LogInfo($"[ID-POWER] active={schedule.Active} recoveredSurge={recoveredSurge} "
                    + $"pattern={pattern + 1} outputs={lights.OutputCount} "
                    + $"remaining={Math.Max(0, schedule.Ends - now):0}s");
            }
            status.Value = schedule.Active
                ? $"{(recoveredSurge ? "Recovering surge" : "Power out")}: {Math.Ceiling(schedule.Ends - now)} seconds"
                : "Power available";
        }

        private void LateUpdate()
        {
            if (!controlling)
            {
                return;
            }

            if (testingSources)
            {
                lights.Drive(1, sourceTestGain);
                return;
            }

            if (!schedule.Active)
            {
                if (!lights.Idle)
                {
                    lights.Drive(1);
                }
                return;
            }
            var game = Singleton<AbstractGame>.Instance;
            double elapsed = game.GameTimer.PastTime.TotalSeconds - schedule.Started;
            float gain = PowerFlickerPatterns.Gain(pattern, recoveredSurge, elapsed / flickerSeconds);
            if (!recoveredSurge)
            {
                float floor = remainingBrightness.Value / 100f;
                gain = floor + (1 - floor) * gain;
            }
            lights.Drive(gain);
        }

        internal static void ExplainBlockedControl()
        {
            if (instance == null || Time.unscaledTime < instance.nextMessage)
            {
                return;
            }
            instance.nextMessage = Time.unscaledTime + 3;
            EFT.UI.ConsoleScreen.Log("Infrastructure Decay: no electrical power. Try again when the lights return.");
        }

        private void Reset()
        {
            controlling = false;
            lights.Dispose();
            schedule.Reset();
            ClearSourceTests();
            firstEvent = true;
            recoveredSurge = false;
            nextDecision = 0;
        }

        private void OnDestroy()
        {
            Reset();
            harmony.UnpatchSelf();
            instance = null;
        }
    }
}
