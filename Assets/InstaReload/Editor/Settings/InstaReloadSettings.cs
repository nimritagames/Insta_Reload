using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor
{
    internal sealed class InstaReloadSettings : ScriptableObject
    {
        private const string SettingsPath = "Assets/InstaReload/Editor/Settings/InstaReloadSettings.asset";

        [SerializeField] private bool enabled = true;
        [SerializeField] private bool verboseLogging = false;

        internal bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        internal bool VerboseLogging
        {
            get => verboseLogging;
            set => verboseLogging = value;
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

            return settings;
        }
    }
}
