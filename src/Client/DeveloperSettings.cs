using System;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace Killdg.DeveloperTools
{
    // Source-linked adapter, not a binary dependency. Public mods only reference
    // BepInEx. When the optional tools plugin is installed, its Config owns the
    // visible developer entries; the provider retains the native engine actions.
    // There is no network, IPC, file watcher, worker, or assembly download.
    internal static class DeveloperSettings
    {
        internal const string PluginId = "com.killdg.testingtools";
        private static string provider = "Mod";
        private static ConfigFile? runtimeDefaults;

        // Configuration Manager reads public metadata fields by name. This tag
        // keeps migration entries out of the original mod's settings card.
        private sealed class ConfigurationManagerAttributes
        {
            public bool Browsable = false;
        }

        internal static bool Available => Chainloader.PluginInfos.ContainsKey(PluginId);

        internal static void Initialize(string providerName)
        {
            provider = providerName;
        }

        internal static ConfigEntry<T> Bind<T>(
            ConfigFile original,
            string section,
            string key,
            T defaultValue,
            string description)
        {
            return Bind(original, section, key, defaultValue, new ConfigDescription(description));
        }

        internal static ConfigEntry<T> Bind<T>(
            ConfigFile original,
            string section,
            string key,
            T defaultValue,
            ConfigDescription description)
        {
            if (!Chainloader.PluginInfos.TryGetValue(PluginId, out PluginInfo? info)
                || info?.Instance == null)
            {
                // Do not bind old developer entries to the public plugin Config,
                // and do not load their saved test flags. Private defaults keep
                // engine code callable without exposing or activating test tools.
                // This ConfigFile is memory-only: automatic saves are disabled
                // before the first Bind, and Save is never called.
                if (runtimeDefaults == null)
                {
                    string path = Path.Combine(Path.GetDirectoryName(original.ConfigFilePath)!,
                        typeof(DeveloperSettings).Assembly.GetName().Name + ".developer-defaults.cfg");
                    runtimeDefaults = new ConfigFile(path, false);
                    runtimeDefaults.SaveOnConfigSet = false;
                    runtimeDefaults.Clear();
                }

                return runtimeDefaults.Bind(section, key, defaultValue,
                    new ConfigDescription(description.Description,
                        description.AcceptableValues, new ConfigurationManagerAttributes()));
            }

            ConfigEntry<T> legacy = original.Bind(section, key, defaultValue,
                new ConfigDescription(description.Description,
                    description.AcceptableValues, new ConfigurationManagerAttributes()));
            ConfigFile tools = info.Instance.Config;
            string toolsSection = provider + " / " + section;
            bool alreadyRegistered = tools.ContainsKey(new ConfigDefinition(toolsSection, key));
            ConfigEntry<T> visible = tools.Bind(toolsSection, key, defaultValue, description);
            if (!alreadyRegistered)
            {
                visible.Value = legacy.Value;
            }

            legacy.Value = visible.Value;
            bool synchronizing = false;
            visible.SettingChanged += (_, _) =>
            {
                if (synchronizing)
                {
                    return;
                }

                synchronizing = true;
                try
                {
                    legacy.Value = visible.Value;
                }
                finally
                {
                    synchronizing = false;
                }
            };
            legacy.SettingChanged += (_, _) =>
            {
                if (synchronizing)
                {
                    return;
                }

                synchronizing = true;
                bool save = tools.SaveOnConfigSet;
                tools.SaveOnConfigSet = false;
                try
                {
                    // Status/roster refreshes must not write the tools config on
                    // every frame. User edits through F1 still save normally.
                    visible.Value = legacy.Value;
                }
                finally
                {
                    tools.SaveOnConfigSet = save;
                    synchronizing = false;
                }
            };

            return legacy;
        }
    }
}
