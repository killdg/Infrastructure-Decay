using System;
using System.Collections.Generic;
using System.Diagnostics;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace InfrastructureDecay
{
    // Brightness only: do not change lamp power state, enabled state, destruction,
    // GameObject activation, quest switches, baked lightmaps or player equipment.
    internal sealed partial class MapLightCache
    {
        private interface ITarget
        {
            int Id { get; }

            void Apply(float gain);
            void Restore();
        }

        private sealed class Scalar(Object owner, int id, Func<float> read, Action<float> write) : ITarget
        {
            public int Id => id;

            private bool owned;
            private float native;
            private float applied;
            public void Apply(float gain)
            {
                if (owner == null)
                {
                    return;
                }

                float value = read();
                if (!owned || Mathf.Abs(value - applied) > 0.0001f)
                {
                    native = value;
                }

                float next = native * gain;
                if (Mathf.Abs(next - value) > 0.0001f)
                {
                    write(next);
                }

                applied = next;
                owned = true;
            }

            public void Restore()
            {
                if (owned && owner != null && Mathf.Abs(read() - applied) < 0.0001f && Mathf.Abs(native - applied) > 0.0001f)
                {
                    write(native);
                }

                owned = false;
            }
        }

        private sealed class Colored(Object owner, int id, Func<Color> read, Action<Color> write) : ITarget
        {
            public int Id => id;

            private bool owned;
            private Color native;
            private Color applied;
            public void Apply(float gain)
            {
                if (owner == null)
                {
                    return;
                }

                var value = read();
                if (!owned || value != applied)
                {
                    native = value;
                }

                var next = new Color(native.r * gain, native.g * gain, native.b * gain, native.a);
                if (value != next)
                {
                    write(next);
                }

                applied = next;
                owned = true;
            }

            public void Restore()
            {
                if (owned && owner != null && read() == applied && native != applied)
                {
                    write(native);
                }

                owned = false;
            }
        }

        private readonly struct Entry
        {
            internal Entry(ITarget target, LightSourceGroup group)
            {
                Target = target;
                Group = group;
            }

            internal ITarget Target { get; }
            internal LightSourceGroup Group { get; }
        }

        // Light outputs (a few thousand) are cheap scalar reads and can be
        // overridden by native flicker every frame, so they are driven every
        // LateUpdate. Material slots are far more numerous (Streets: ~90k) and
        // each check costs several native property-block calls, so they are
        // driven by a time-budgeted sweep instead of a full pass per frame.
        private readonly List<Entry> lightTargets = new();
        private readonly List<Entry> slotTargets = new();
        private readonly HashSet<int> ids = new();
        private readonly HashSet<int> drivenLights = new();
        private readonly HashSet<string> materialSlots = new HashSet<string>();
        private readonly MaterialPropertyBlock probe = new MaterialPropertyBlock();
        private readonly float[] groupGains = new float[8];
        private readonly Stopwatch sweepClock = new Stopwatch();
        private int darkSlotsSkipped;

        // Sweep state for material slots.
        private const int SteadySlotsPerFrame = 512;
        private const double ChangeBudgetMilliseconds = 2.0;
        private const float GlowThreshold = 0.0001f;
        private bool groupMode;
        private float driveGain = 1;
        private bool dimmed;
        private int sweepCursor;
        private int sweepLeft;
        private readonly Stack<Transform> pending = new();
        private readonly HashSet<int> scenes = new();
        private readonly MapMaterialSurvey materialSurvey = new MapMaterialSurvey();
        private bool initialized;

        private int nodes;
        private int lamps;
        // Native field/property identifiers for SPT 4.1.6 / EFT 0.16.9.
        // The private multiplier has no public getter, so reflection reads it for
        // restoration. Shader names address emissive brightness on map fixtures.
        // Renaming these strings would break the native integration.
        private static readonly System.Reflection.FieldInfo AreaMultiplier = AccessTools.Field(typeof(CullingAdvancedLightObject),
             "_intensityMultiplier");
        private static readonly int EmissionVisibility = Shader.PropertyToID("_EmissionVisibility");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        internal void Discover()
        {
            if (!initialized)
            {
                initialized = true;
                SceneManager.sceneLoaded += SceneLoaded;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Enqueue(SceneManager.GetSceneAt(i));
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            var clock = Stopwatch.StartNew();
            int budget = 0;
            while (pending.Count > 0 && budget++ < 96)
            {
                var t = pending.Pop();
                if (t == null)
                {
                    continue;
                }

                if (t.name.StartsWith("ROT_", StringComparison.Ordinal)
                    || t.GetComponent<Player>() != null
                    || t.GetComponent<Camera>() != null
                    || t.GetComponent<LootItem>() != null)
                {
                    continue;
                }

                for (int i = 0; i < t.childCount; i++)
                {
                    pending.Push(t.GetChild(i));
                }

                try
                {
                    Inspect(t);
                }
                catch (Exception e)
                {
                    Plugin.Trace("[ID-LIGHTS] skipped node=" + t.name + " reason=" + e.GetBaseException().Message);
                }

                nodes++;
                if (budget % 8 == 0 && clock.Elapsed.TotalMilliseconds >= 1.5)
                {
                    break;
                }
            }

            if (pending.Count == 0)
            {
                Plugin.Trace($"[ID-LIGHTS] cache-ready nodes={nodes} lamps={lamps} controllableOutputs={OutputCount} "
                    + $"(lights={lightTargets.Count} glowingSlots={slotTargets.Count} darkSlotsSkipped={darkSlotsSkipped}); "
                    + "map lights only");
            }
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode) => Enqueue(scene);
        private void Enqueue(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || !scenes.Add(scene.handle))
            {
                return;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                pending.Push(root.transform);
            }
        }

        private void Add(ITarget target, LightSourceGroup group = LightSourceGroup.OrdinaryLights)
        {
            if (ids.Add(target.Id))
            {
                lightTargets.Add(new Entry(target, group));
            }
        }

        private void Driven(int id)
        {
            drivenLights.Add(id);
            // Only light outputs carry Light instance ids; material slots never match.
            for (int i = lightTargets.Count - 1; i >= 0; i--)
            {
                if (lightTargets[i].Target.Id == id)
                {
                    lightTargets[i].Target.Restore();
                    lightTargets.RemoveAt(i);
                    ids.Remove(id);
                }
            }
        }

        private void Inspect(Transform t)
        {
            if (Killdg.DeveloperTools.DeveloperSettings.Available)
            {
                materialSurvey.Inspect(t);
            }
            var culling = t.GetComponent<CullingLightObject>();
            if (culling != null && culling._light != null && culling._light.type != LightType.Directional)
            {
                Driven(culling._light.GetInstanceID());
                Add(new Scalar(
                        culling,
                        culling.GetInstanceID(),
                        () => culling.IntensityMultiplier,
                        v =>
                {
                    culling.IntensityMultiplier = v;
                    culling.UpdateIntensity();
                }), LightSourceGroup.CulledLights);
            }

            var areaCulling = t.GetComponent<CullingAdvancedLightObject>();
            if (areaCulling != null && areaCulling._light != null && AreaMultiplier != null)
            {
                Driven(areaCulling._light.GetInstanceID());
                Add(new Scalar(
                        areaCulling,
                        areaCulling.GetInstanceID(),
                        () => (float)AreaMultiplier.GetValue(areaCulling),
                        v =>
                {
                    areaCulling.IntensityMultiplier = v;
                    areaCulling.UpdateIntensity();
                }), LightSourceGroup.AreaLights);
            }

            var light = t.GetComponent<Light>();
            if (light != null && light.type != LightType.Directional && !drivenLights.Contains(light.GetInstanceID()))
            {
                Add(new Scalar(light, light.GetInstanceID(), () => light.intensity, v => light.intensity = v));
            }

            var area = t.GetComponent<BaseLight>();
            if (area != null && !drivenLights.Contains(area.GetInstanceID()))
            {
                Add(new Scalar(area,
                     area.GetInstanceID(),
                     () => area.m_Intensity,
                     v => area.m_Intensity = v),
                     LightSourceGroup.AreaLights);
            }

            var custom = t.GetComponent<AdvancedLight>();
            if (custom != null && custom.LightType != AdvancedLight.LightTypeEnum.Directional)
            {
                Add(new Colored(custom,
                     custom.GetInstanceID(),
                     () => custom.Color,
                     v => custom.Color = v),
                     LightSourceGroup.AreaLights);
            }

            var lamp = t.GetComponent<LampController>();
            if (lamp != null)
            {
                lamps++;
                foreach (var entry in lamp._materialsWithEmission ?? Array.Empty<MaterialEmission>())
                {
                    RegisterEmission(entry);
                }

                foreach (var entry in lamp._legacyMaterialsWithEmission ?? Array.Empty<MaterialColor>())
                {
                    RegisterEmission(entry);
                }
            }

            // Read material slots once during the bounded traversal. A material
            // must expose a recognized emission channel, or be a named unlit
            // screen for the isolated experimental test. No ordinary albedo tint
            // is changed, and player/camera subtrees are excluded above.
            Renderer renderer = t.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                RegisterMaterialSlots(renderer);
            }
        }

        private void RegisterEmission(MaterialData entry)
        {
            if (entry?.Renderer == null)
            {
                return;
            }

            var material = entry.Material;
            if (material == null)
            {
                return;
            }

            // Lamp fixture slots stay registered even while dark: the lamp can be
            // switched on later, and its glow must then follow the outage.
            RegisterMaterialSlots(
                entry.Renderer,
                entry is MaterialColor colorEntry ? colorEntry.ParemeterName : null,
                fixture: true);
        }

        internal int OutputCount => lightTargets.Count + slotTargets.Count;

        // True when no output is dimmed and no restoring sweep is pending.
        internal bool Idle => !dimmed && sweepLeft == 0;

        internal string DescribeGroups()
        {
            var counts = new int[groupGains.Length];
            foreach (Entry entry in lightTargets)
            {
                counts[(int)entry.Group]++;
            }

            foreach (Entry entry in slotTargets)
            {
                counts[(int)entry.Group]++;
            }

            var descriptions = new List<string>();
            foreach (LightSourceGroup group in Enum.GetValues(typeof(LightSourceGroup)))
            {
                descriptions.Add(group + "=" + counts[(int)group]);
            }

            return string.Join("; ", descriptions);
        }

        internal void Drive(float gain, Func<LightSourceGroup, float>? groupGain = null)
        {
            // Resolve the requested state once per frame and detect a change.
            bool changed;
            if (groupGain == null)
            {
                changed = groupMode || gain != driveGain;
                groupMode = false;
                driveGain = gain;
                dimmed = gain < 1;
            }
            else
            {
                changed = !groupMode;
                groupMode = true;
                dimmed = false;
                for (int index = 0; index < groupGains.Length; index++)
                {
                    float value = groupGain((LightSourceGroup)index);
                    changed |= value != groupGains[index];
                    groupGains[index] = value;
                    dimmed |= value < 1;
                }
            }

            for (int index = lightTargets.Count - 1; index >= 0; index--)
            {
                DriveEntry(lightTargets, index);
            }

            // A state change (flicker edge, outage start or end) sweeps every
            // slot once within a per-frame time budget, continuing next frame if
            // needed. While the state is steady, a small rolling share is
            // re-asserted so native emission changes are adopted or re-darkened.
            if (changed)
            {
                sweepLeft = slotTargets.Count;
            }

            int steady = Math.Min(SteadySlotsPerFrame, slotTargets.Count);
            int processed = 0;
            sweepClock.Restart();
            while (slotTargets.Count > 0)
            {
                if (sweepLeft > 0)
                {
                    if (processed > 0 && processed % 64 == 0
                        && sweepClock.Elapsed.TotalMilliseconds >= ChangeBudgetMilliseconds)
                    {
                        break;
                    }

                    sweepLeft--;
                }
                else if (steady-- <= 0)
                {
                    break;
                }

                if (sweepCursor >= slotTargets.Count)
                {
                    sweepCursor = 0;
                }

                if (DriveEntry(slotTargets, sweepCursor))
                {
                    sweepCursor++;
                }

                processed++;
            }
        }

        private float GainFor(LightSourceGroup group)
        {
            if (groupMode)
            {
                return groupGains[(int)group];
            }

            return group == LightSourceGroup.ExperimentalUnlitScreens ? 1 : driveGain;
        }

        // Returns false when the entry was removed from the list.
        private bool DriveEntry(List<Entry> list, int index)
        {
            Entry entry = list[index];
            try
            {
                float selectedGain = GainFor(entry.Group);
                if (selectedGain >= 1)
                {
                    entry.Target.Restore();
                }
                else
                {
                    entry.Target.Apply(selectedGain);
                }

                return true;
            }
            catch (Exception error)
            {
                try
                {
                    entry.Target.Restore();
                }
                catch
                {
                    // An unloaded scene output cannot be restored.
                }
                ids.Remove(entry.Target.Id);
                list.RemoveAt(index);
                Plugin.Trace("[ID-LIGHTS] unavailable output removed: " + error.GetBaseException().Message);
                return false;
            }
        }

        internal void Restore()
        {
            RestoreAll(lightTargets);
            RestoreAll(slotTargets);
            groupMode = false;
            driveGain = 1;
            dimmed = false;
            sweepLeft = 0;
        }

        private static void RestoreAll(List<Entry> list)
        {
            foreach (Entry entry in list)
            {
                try
                {
                    entry.Target.Restore();
                }
                catch (Exception error)
                {
                    Plugin.Trace("[ID-LIGHTS] restore skipped: " + error.GetBaseException().Message);
                }
            }
        }
        internal void Dispose()
        {
            Restore();
            if (initialized)
            {
                SceneManager.sceneLoaded -= SceneLoaded;
            }

            lightTargets.Clear();
            slotTargets.Clear();
            ids.Clear();
            drivenLights.Clear();
            materialSlots.Clear();
            sweepCursor = 0;
            darkSlotsSkipped = 0;
            pending.Clear();
            scenes.Clear();
            materialSurvey.Reset();
            initialized = false;
            lamps = 0;
            nodes = 0;
        }
    }
}
