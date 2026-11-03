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

        private readonly List<ITarget> targets = new();
        private readonly HashSet<int> ids = new();
        private readonly HashSet<int> drivenLights = new();
        private readonly HashSet<string> materialSlots = new HashSet<string>();
        private readonly Dictionary<ITarget, LightSourceGroup> groups = new Dictionary<ITarget, LightSourceGroup>();
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
                Plugin.Trace($"[ID-LIGHTS] cache-ready nodes={nodes} lamps={lamps} controllableOutputs={targets.Count}; map lights only");
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
                targets.Add(target);
                groups.Add(target, group);
            }
        }

        private void Driven(int id)
        {
            drivenLights.Add(id);
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                if (targets[i].Id == id)
                {
                    targets[i].Restore();
                    groups.Remove(targets[i]);
                    targets.RemoveAt(i);
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

            RegisterMaterialSlots(
                entry.Renderer,
                entry is MaterialColor colorEntry ? colorEntry.ParemeterName : null);
        }

        internal int OutputCount => targets.Count;

        internal string DescribeGroups()
        {
            var counts = new Dictionary<LightSourceGroup, int>();
            foreach (LightSourceGroup group in groups.Values)
            {
                counts[group] = counts.TryGetValue(group, out int count) ? count + 1 : 1;
            }

            var descriptions = new List<string>();
            foreach (LightSourceGroup group in Enum.GetValues(typeof(LightSourceGroup)))
            {
                counts.TryGetValue(group, out int count);
                descriptions.Add(group + "=" + count);
            }

            return string.Join("; ", descriptions);
        }

        internal void Drive(float gain, Func<LightSourceGroup, float>? groupGain = null)
        {
            for (int index = targets.Count - 1; index >= 0; index--)
            {
                try
                {
                    LightSourceGroup group = groups[targets[index]];
                    if (groupGain == null && group == LightSourceGroup.ExperimentalUnlitScreens)
                    {
                        targets[index].Restore();
                    }
                    else
                    {
                        float selectedGain = groupGain == null ? gain : groupGain(group);
                        if (selectedGain >= 1)
                        {
                            targets[index].Restore();
                        }
                        else
                        {
                            targets[index].Apply(selectedGain);
                        }
                    }
                }
                catch (Exception error)
                {
                    try
                    {
                        targets[index].Restore();
                    }
                    catch
                    {
                        // An unloaded scene output cannot be restored.
                    }
                    ids.Remove(targets[index].Id);
                    groups.Remove(targets[index]);
                    targets.RemoveAt(index);
                    Plugin.Trace("[ID-LIGHTS] unavailable output removed: " + error.GetBaseException().Message);
                }
            }
        }

        internal void Restore()
        {
            foreach (var target in targets)
            {
                try
                {
                    target.Restore();
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

            targets.Clear();
            ids.Clear();
            drivenLights.Clear();
            materialSlots.Clear();
            groups.Clear();
            pending.Clear();
            scenes.Clear();
            materialSurvey.Reset();
            initialized = false;
            lamps = 0;
            nodes = 0;
        }
    }
}
