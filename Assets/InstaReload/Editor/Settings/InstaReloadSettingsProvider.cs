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
                    EditorGUILayout.PropertyField(
                        serialized.FindProperty("autoApplyPlayModeSettings"),
                        new GUIContent("Auto-apply Play Mode settings"));

                    EditorGUILayout.HelpBox(
                        "Toggle the Dispatcher category to show or hide runtime dispatch diagnostics.",
                        MessageType.None);

                    EditorGUILayout.HelpBox(
                        "InstaReload patches method bodies during Play Mode to avoid domain reloads. Structural changes still require a restart.",
                        MessageType.Info);

                    var configured = InstaReloadPlayModeSettings.IsConfigured(out var details);
                    EditorGUILayout.HelpBox(
                        configured
                            ? "Play Mode settings are configured for InstaReload."
                            : $"Play Mode settings need updates: {details}.",
                        configured ? MessageType.Info : MessageType.Warning);

                    if (GUILayout.Button("Apply Recommended Play Mode Settings"))
                    {
                        if (InstaReloadPlayModeSettings.ApplyRecommendedSettings())
                        {
                            InstaReloadLogger.Log(InstaReloadLogCategory.UI, "Applied recommended Play Mode settings.");
                        }
                    }

                    var changed = serialized.ApplyModifiedProperties();
                    if (changed && EditorApplication.isPlaying)
                    {
                        HotReloadDispatcher.ConfigureLogging(
                            settings.EnabledLogCategories,
                            settings.EnabledLogLevels);
                    }
                    if (changed && settings.AutoApplyPlayModeSettings)
                    {
                        InstaReloadPlayModeSettings.ApplyRecommendedSettings();
                    }
                },
                keywords = new HashSet<string>(new[] { "InstaReload", "Hot Reload", "Reload", "Patch" })
            };
        }
    }
}
