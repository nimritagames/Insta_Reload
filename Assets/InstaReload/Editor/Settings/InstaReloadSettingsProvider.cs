using System.Collections.Generic;
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
                        serialized.FindProperty("verboseLogging"),
                        new GUIContent("Verbose Logging"));

                    EditorGUILayout.HelpBox(
                        "InstaReload patches method bodies during Play Mode to avoid domain reloads. Structural changes still require a restart.",
                        MessageType.Info);

                    serialized.ApplyModifiedProperties();
                },
                keywords = new HashSet<string>(new[] { "InstaReload", "Hot Reload", "Reload", "Patch" })
            };
        }
    }
}
