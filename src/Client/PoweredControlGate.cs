using System.Collections.Generic;
using EFT.Interactive;
using HarmonyLib;

namespace InfrastructureDecay
{
    internal static class PoweredControlGate
    {
        internal static bool ShouldBlock(WorldInteractiveObject target)
        {
            if (!Plugin.PowerOut || target is not Switch control)
            {
                return false;
            }
            bool lighting = Plugin.BlockLightControls && control.Lamps?.Length > 0;
            bool exit = Plugin.BlockExtractControls &&
                (control.ExfiltrationPoint != null || LinkedToExtraction(control));
            bool other = control.Lamps?.Length is not > 0
                && control.ExfiltrationPoint == null && !LinkedToExtraction(control);
            return lighting || exit || (other && Plugin.BlockOtherPoweredControls);
        }

        private static bool LinkedToExtraction(Switch start)
        {
            // Native exit controls can be a chain: a power/button switch opens a
            // door before another switch enables the extraction point. Follow only
            // explicit scene links, never names or arbitrary quest interactions.
            var pending = new Queue<Switch>();
            var seen = new HashSet<Switch>();
            pending.Enqueue(start);
            while (pending.Count > 0 && seen.Count < 32)
            {
                var control = pending.Dequeue();
                if (control == null || !seen.Add(control))
                {
                    continue;
                }
                if (control.ExfiltrationPoint != null)
                {
                    return true;
                }
                if (control.PreviousSwitch != null)
                {
                    pending.Enqueue(control.PreviousSwitch);
                }
                foreach (var link in control.NextSwitches ?? System.Array.Empty<Switch.SwitchAndOperation>())
                {
                    if (link.Switch != null)
                    {
                        pending.Enqueue(link.Switch);
                    }
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldInteractiveObject), nameof(WorldInteractiveObject.CanStartInteraction))]
    internal static class PoweredControlAvailability
    {
        private static bool Prefix(WorldInteractiveObject __instance, ref bool __result)
        {
            if (!PoweredControlGate.ShouldBlock(__instance))
            {
                return true;
            }
            // Reject eligibility before native LockForInteraction changes the
            // switch into its transient Interacting state. No rollback is needed.
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class PoweredControlEntry
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(EFT.Player), "StartInteraction", new[]
            {
                typeof(WorldInteractiveObject), typeof(InteractionResult), typeof(System.Action)
            });
            yield return AccessTools.Method(typeof(EFT.Player), "ExecuteInteraction", new[]
            {
                typeof(WorldInteractiveObject), typeof(InteractionResult)
            });
        }

        private static bool Prefix(WorldInteractiveObject __0)
        {
            if (!PoweredControlGate.ShouldBlock(__0))
            {
                return true;
            }
            // Covers an action selected immediately before an outage. Stop before
            // MovementState starts/locks an interaction, rather than skipping the
            // final Switch.Interact callback after that lifecycle has begun.
            Plugin.ExplainBlockedControl();
            return false;
        }
    }
}
