using System;
using UnityEngine;

namespace InfrastructureDecay
{
    internal sealed partial class MapLightCache
    {
        // A renderer can have multiple material slots, each with its own glow.
        // Indexed property blocks isolate a screen from the metal frame sharing
        // the same renderer. Shared materials/textures are never modified.
        private sealed class MaterialSlotTarget : ITarget
        {
            private readonly Renderer renderer;
            private readonly Material material;
            private readonly int slot;
            private readonly int property;
            private readonly bool colorProperty;
            private readonly MaterialPropertyBlock original = new MaterialPropertyBlock();
            private readonly MaterialPropertyBlock working = new MaterialPropertyBlock();
            private bool owned;
            private bool originallyEmpty;
            private Color nativeColor;
            private Color appliedColor;
            private float nativeScalar;
            private float appliedScalar;
            private float lastGain;

            public int Id => renderer.GetInstanceID();

            internal MaterialSlotTarget(
                Renderer renderer,
                Material material,
                int slot,
                int property,
                bool colorProperty)
            {
                this.renderer = renderer;
                this.material = material;
                this.slot = slot;
                this.property = property;
                this.colorProperty = colorProperty;
            }

            public void Apply(float gain)
            {
                if (renderer == null || material == null)
                {
                    return;
                }

                renderer.GetPropertyBlock(working, slot);
                if (owned && MatchesApplied() && Mathf.Approximately(lastGain, gain))
                {
                    return;
                }
                if (!owned)
                {
                    renderer.GetPropertyBlock(original, slot);
                    originallyEmpty = original.isEmpty;
                    if (originallyEmpty)
                    {
                        // An indexed block overrides renderer-wide values. Copy
                        // those values first so unrelated native effects survive.
                        renderer.GetPropertyBlock(working);
                    }

                    nativeColor = working.HasColor(property)
                        ? working.GetColor(property)
                        : colorProperty ? material.GetColor(property) : Color.black;
                    nativeScalar = working.HasFloat(property)
                        ? working.GetFloat(property)
                        : colorProperty ? 0 : material.GetFloat(property);
                }
                else if (!MatchesApplied())
                {
                    // A native animation/switch changed this channel. Adopt its
                    // latest value instead of later restoring a stale baseline.
                    if (colorProperty)
                    {
                        nativeColor = working.GetColor(property);
                    }
                    else
                    {
                        nativeScalar = working.GetFloat(property);
                    }
                }

                if (colorProperty)
                {
                    appliedColor = new Color(
                        nativeColor.r * gain,
                        nativeColor.g * gain,
                        nativeColor.b * gain,
                        nativeColor.a);
                    working.SetColor(property, appliedColor);
                }
                else
                {
                    appliedScalar = nativeScalar * gain;
                    working.SetFloat(property, appliedScalar);
                }

                renderer.SetPropertyBlock(working, slot);
                lastGain = gain;
                owned = true;
            }

            private bool MatchesApplied()
            {
                return colorProperty
                    ? working.HasColor(property) && working.GetColor(property) == appliedColor
                    : working.HasFloat(property)
                        && Mathf.Abs(working.GetFloat(property) - appliedScalar) < 0.0001f;
            }

            public void Restore()
            {
                if (!owned || renderer == null)
                {
                    owned = false;
                    return;
                }

                renderer.GetPropertyBlock(working, slot);
                if (MatchesApplied())
                {
                    // A slot with no original override must resume inheriting the
                    // renderer-wide block, rather than freezing its copied values.
                    if (originallyEmpty)
                    {
                        renderer.SetPropertyBlock(null, slot);
                        owned = false;
                        return;
                    }

                    // Retain unrelated properties in an existing indexed block.
                    if (colorProperty)
                    {
                        working.SetColor(property, nativeColor);
                    }
                    else
                    {
                        working.SetFloat(property, nativeScalar);
                    }

                    renderer.SetPropertyBlock(working, slot);
                }

                owned = false;
            }
        }

        private void RegisterMaterialSlots(Renderer renderer, string? customColor = null, bool fixture = false)
        {
            Material[] materials = renderer.sharedMaterials;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                Material material = materials[slot];
                if (material == null)
                {
                    continue;
                }

                string key = renderer.GetInstanceID() + ":" + slot;
                if (materialSlots.Contains(key))
                {
                    continue;
                }

                int property;
                bool color = false;
                LightSourceGroup group = ClassifyMaterial(renderer.name, material);
                if (!string.IsNullOrEmpty(customColor) && material.HasProperty(customColor))
                {
                    property = Shader.PropertyToID(customColor);
                    color = true;
                }
                else if (material.HasProperty(EmissionVisibility))
                {
                    property = EmissionVisibility;
                }
                else if (material.HasProperty(EmissionColor))
                {
                    property = EmissionColor;
                    color = true;
                }
                else if (material.HasProperty("_EmissiveColor"))
                {
                    property = Shader.PropertyToID("_EmissiveColor");
                    color = true;
                }
                else if (IsDisplayName(renderer.name)
                    && material.shader != null
                    && material.shader.name.IndexOf("unlit", StringComparison.OrdinalIgnoreCase) >= 0
                    && material.HasProperty("_Color"))
                {
                    // Experimental only: some unlit screens tint their picture
                    // with _Color. Never apply this to ordinary lit surfaces.
                    property = Shader.PropertyToID("_Color");
                    color = true;
                    group = LightSourceGroup.ExperimentalUnlitScreens;
                }
                else
                {
                    continue;
                }

                // Most map materials expose an emission channel that is black or
                // switched off. Scaling zero changes nothing on screen, but every
                // such slot would still cost native property-block calls on each
                // drive and an indexed block override during outages. Lamp fixture
                // slots are kept regardless, because their glow is switchable.
                // The key is not recorded, so a lamp found later can still claim it.
                if (!fixture && !Glows(renderer, material, slot, property, color))
                {
                    darkSlotsSkipped++;
                    continue;
                }

                materialSlots.Add(key);
                ITarget target = new MaterialSlotTarget(renderer, material, slot, property, color);
                slotTargets.Add(new Entry(target, group));
            }
        }

        // Effective value as rendered: indexed slot block, then renderer-wide
        // block, then the shared material. Discovery-time only.
        private bool Glows(Renderer renderer, Material material, int slot, int property, bool color)
        {
            renderer.GetPropertyBlock(probe, slot);
            if (TryReadGlow(probe, property, color, out bool glows))
            {
                return glows;
            }

            renderer.GetPropertyBlock(probe);
            if (TryReadGlow(probe, property, color, out glows))
            {
                return glows;
            }

            return color
                ? material.GetColor(property).maxColorComponent > GlowThreshold
                : material.GetFloat(property) > GlowThreshold;
        }

        private static bool TryReadGlow(MaterialPropertyBlock block, int property, bool color, out bool glows)
        {
            if (color ? block.HasColor(property) : block.HasFloat(property))
            {
                glows = color
                    ? block.GetColor(property).maxColorComponent > GlowThreshold
                    : block.GetFloat(property) > GlowThreshold;
                return true;
            }

            glows = false;
            return false;
        }

        private static bool IsDisplayName(string name)
        {
            return name.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("lcd", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static LightSourceGroup ClassifyMaterial(string name, Material material)
        {
            if (name.IndexOf("neon", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("sign", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LightSourceGroup.NeonSigns;
            }

            if (material.shader != null
                && material.shader.name.IndexOf("animated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LightSourceGroup.AnimatedDisplays;
            }

            return IsDisplayName(name)
                ? LightSourceGroup.MonitorEmission
                : LightSourceGroup.FixtureEmission;
        }
    }
}
