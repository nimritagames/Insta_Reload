using Nimrita.InstaReload;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor.UI
{
    internal static class InstaReloadMenuItems
    {
        [MenuItem("InstaReload/Open Settings Window", priority = 0)]
        private static void OpenSettingsWindow()
        {
            InstaReloadWindow.ShowWindow();
        }

        [MenuItem("InstaReload/Enable Hot Reload", priority = 20)]
        private static void EnableHotReload()
        {
            var settings = InstaReloadSettings.GetOrCreateSettings();
            settings.Enabled = true;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            InstaReloadLogger.Log(InstaReloadLogCategory.UI, "Hot reload enabled");
        }

        [MenuItem("InstaReload/Enable Hot Reload", true)]
        private static bool ValidateEnableHotReload()
        {
            var settings = InstaReloadSettings.GetOrCreateSettings();
            return settings != null && !settings.Enabled;
        }

        [MenuItem("InstaReload/Disable Hot Reload", priority = 21)]
        private static void DisableHotReload()
        {
            var settings = InstaReloadSettings.GetOrCreateSettings();
            settings.Enabled = false;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            InstaReloadLogger.Log(InstaReloadLogCategory.UI, "Hot reload disabled");
        }

        [MenuItem("InstaReload/Disable Hot Reload", true)]
        private static bool ValidateDisableHotReload()
        {
            var settings = InstaReloadSettings.GetOrCreateSettings();
            return settings != null && settings.Enabled;
        }

        [MenuItem("InstaReload/Open Project Settings (Editor)", priority = 40)]
        private static void OpenProjectSettings()
        {
            SettingsService.OpenProjectSettings("Project/Editor");
        }

        [MenuItem("InstaReload/Documentation/About InstaReload", priority = 60)]
        private static void ShowAbout()
        {
            EditorUtility.DisplayDialog(
                "About InstaReload",
                "InstaReload - Hot Reload for Unity\n\n" +
                "A custom hot reload system that allows you to modify code during Play Mode " +
                "without exiting Play Mode.\n\n" +
                "Features:\n" +
                "• Instant method body updates\n" +
                "• Project-wide support\n" +
                "• Visual feedback\n" +
                "• Smart compatibility checks\n\n" +
                "Created with MonoMod & Mono.Cecil",
                "OK"
            );
        }
    }
}
