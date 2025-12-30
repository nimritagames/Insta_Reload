using System.Collections.Generic;
using Nimrita.InstaReload;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor
{
    internal static class InstaReloadSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/InstaReload", SettingsScope.Project)
            {
                label = "InstaReload",
                guiHandler = _ =>
                {
                    var settings = InstaReloadSettings.GetOrCreateSettings();
                    var serialized = new SerializedObject(settings);
                    serialized.Update();

                    EditorGUILayout.LabelField("Play Mode", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(
                        serialized.FindProperty("enabled"),
                        new GUIContent("Enable InstaReload"));
                    EditorGUILayout.PropertyField(
                        serialized.FindProperty("enabledLogLevels"),
                        new GUIContent("Log Levels"));
                    EditorGUILayout.PropertyField(
                        serialized.FindProperty("enabledLogCategories"),
                        new GUIContent("Log Categories"));

                    EditorGUILayout.HelpBox(
                        "Toggle the Dispatcher category to show or hide runtime dispatch diagnostics.",
                        MessageType.None);

                    EditorGUILayout.HelpBox(
                        "InstaReload patches method bodies during Play Mode to avoid domain reloads. Structural changes still require a restart.",
                        MessageType.Info);

                    var changed = serialized.ApplyModifiedProperties();
                    if (changed && EditorApplication.isPlaying)
                    {
                        HotReloadDispatcher.ConfigureLogging(
                            settings.EnabledLogCategories,
                            settings.EnabledLogLevels);
                    }
                },
                keywords = new HashSet<string>(new[] { "InstaReload", "Hot Reload", "Reload", "Patch" })
            };
        }
    }
}
