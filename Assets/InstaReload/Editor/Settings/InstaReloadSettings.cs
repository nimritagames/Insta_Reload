using Nimrita.InstaReload;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor
{
    internal sealed class InstaReloadSettings : ScriptableObject
    {
        private const string SettingsPath = "Assets/InstaReload/Editor/Settings/InstaReloadSettings.asset";
        private const int LogSettingsVersion = 1;

        [SerializeField] private bool enabled = true;
        [SerializeField, HideInInspector] private bool verboseLogging = false;
        [SerializeField] private InstaReloadLogLevel enabledLogLevels = InstaReloadLogLevel.Info | InstaReloadLogLevel.Warning | InstaReloadLogLevel.Error;
        [SerializeField] private InstaReloadLogCategory enabledLogCategories = InstaReloadLogCategory.All;
        [SerializeField, HideInInspector] private int logSettingsVersion = 0;

        internal bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        internal bool VerboseLogging
        {
            get => IsLogLevelEnabled(InstaReloadLogLevel.Verbose);
            set
            {
                if (value)
                {
                    EnabledLogLevels |= InstaReloadLogLevel.Verbose;
                }
                else
                {
                    EnabledLogLevels &= ~InstaReloadLogLevel.Verbose;
                }

                verboseLogging = value;
            }
        }

        internal InstaReloadLogLevel EnabledLogLevels
        {
            get => enabledLogLevels;
            set => enabledLogLevels = value;
        }

        internal InstaReloadLogCategory EnabledLogCategories
        {
            get => enabledLogCategories;
            set => enabledLogCategories = value;
        }

        internal bool IsLogLevelEnabled(InstaReloadLogLevel level)
        {
            return (enabledLogLevels & level) != 0;
        }

        internal bool IsLogCategoryEnabled(InstaReloadLogCategory category)
        {
            return (enabledLogCategories & category) != 0;
        }

        internal static InstaReloadSettings GetOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<InstaReloadSettings>(SettingsPath);
            if (settings == null)
            {
                settings = CreateInstance<InstaReloadSettings>();
                AssetDatabase.CreateAsset(settings, SettingsPath);
                AssetDatabase.SaveAssets();
            }
            else
            {
                bool dirty = false;
                if (settings.logSettingsVersion < LogSettingsVersion)
                {
                    if (settings.enabledLogLevels == InstaReloadLogLevel.None)
                    {
                        settings.enabledLogLevels = InstaReloadLogLevel.Info | InstaReloadLogLevel.Warning | InstaReloadLogLevel.Error;
                        if (settings.verboseLogging)
                        {
                            settings.enabledLogLevels |= InstaReloadLogLevel.Verbose;
                        }

                        dirty = true;
                    }

                    if (settings.enabledLogCategories == InstaReloadLogCategory.None ||
                        settings.enabledLogCategories == InstaReloadLogCategory.Default)
                    {
                        settings.enabledLogCategories = InstaReloadLogCategory.All;
                        dirty = true;
                    }

                    settings.logSettingsVersion = LogSettingsVersion;
                    dirty = true;
                }

                if (dirty)
                {
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                }
            }

            return settings;
        }
    }
}
