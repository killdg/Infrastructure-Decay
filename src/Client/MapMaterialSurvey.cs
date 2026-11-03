using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace InfrastructureDecay
{
    // Read-only, bounded diagnostics for map displays that remain bright without
    // a Unity Light. This runs within the existing incremental scene traversal;
    // it never creates materials, changes textures, or writes to shared assets.
    internal sealed class MapMaterialSurvey
    {
        private const int MaximumReports = 100;
        private const int MaximumProperties = 32;
        private readonly HashSet<int> reportedMaterials = new HashSet<int>();

        internal void Inspect(Transform transform)
        {
            if (reportedMaterials.Count >= MaximumReports || !IsCandidate(transform.name))
            {
                return;
            }

            Renderer renderer = transform.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                return;
            }

            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null || material.shader == null
                    || reportedMaterials.Count >= MaximumReports
                    || !reportedMaterials.Add(material.GetInstanceID()))
                {
                    continue;
                }

                Shader shader = material.shader;
                StringBuilder properties = new StringBuilder();
                int count = Math.Min(shader.GetPropertyCount(), MaximumProperties);
                for (int index = 0; index < count; index++)
                {
                    if (index > 0)
                    {
                        properties.Append(", ");
                    }

                    properties.Append(shader.GetPropertyName(index));
                    properties.Append(':');
                    properties.Append(shader.GetPropertyType(index));
                }

                // Object/material/shader identifiers are local map asset names.
                // No player identifiers, inventory, files, or network data are read.
                Plugin.Trace("[ID-MATERIAL] object=" + transform.name
                    + " material=" + material.name
                    + " shader=" + shader.name
                    + " properties=[" + properties + "]");
            }
        }

        private static bool IsCandidate(string name)
        {
            return name.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("display", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("panel", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("sign", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("neon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal void Reset()
        {
            reportedMaterials.Clear();
        }
    }
}
