using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor.UI
{
    public class InstaReloadWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private static InstaReloadWindow _instance;

        [MenuItem("Window/InstaReload Settings")]
        public static void ShowWindow()
        {
            _instance = GetWindow<InstaReloadWindow>("InstaReload");
            _instance.minSize = new Vector2(400, 300);
        }

        private void OnEnable()
        {
            _instance = this;
        }

        private void OnGUI()
        {
            var settings = InstaReloadSettings.GetOrCreateSettings();
            if (settings == null)
            {
                EditorGUILayout.HelpBox("Failed to load InstaReload settings", MessageType.Error);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            // Header
            EditorGUILayout.Space(10);
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField("⚡ InstaReload", titleStyle, GUILayout.Height(30));
            EditorGUILayout.LabelField("Hot Reload for Unity", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(10);

            // Status Box
            DrawStatusBox(settings);

            EditorGUILayout.Space(10);

            // Settings
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            settings.Enabled = EditorGUILayout.Toggle(
                new GUIContent("Enable Hot Reload", "Enable or disable hot reload during Play Mode"),
                settings.Enabled
            );

            settings.VerboseLogging = EditorGUILayout.Toggle(
                new GUIContent("Verbose Logging", "Show detailed logs for debugging"),
                settings.VerboseLogging
            );

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            EditorGUILayout.Space(10);

            // Instructions
            DrawInstructions();

            EditorGUILayout.Space(10);

            // Statistics (if in Play Mode)
            if (EditorApplication.isPlaying)
            {
                DrawStatistics();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawStatusBox(InstaReloadSettings settings)
        {
            var boxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 10, 10)
            };

            EditorGUILayout.BeginVertical(boxStyle);

            // Status indicator
            var statusColor = settings.Enabled ? (EditorApplication.isPlaying ? Color.green : Color.yellow) : Color.gray;
            var statusText = settings.Enabled
                ? (EditorApplication.isPlaying ? "✓ Active" : "⏸ Waiting for Play Mode")
                : "○ Disabled";

            var statusStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = statusColor },
                fontSize = 14
            };
            EditorGUILayout.LabelField("Status: " + statusText, statusStyle);

            if (settings.Enabled && !EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to start hot reloading", MessageType.Info);
            }
            else if (!settings.Enabled)
            {
                EditorGUILayout.HelpBox("Hot reload is disabled. Enable it above to use.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawInstructions()
        {
            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };

            if (EditorGUILayout.Foldout(SessionState.GetBool("InstaReload.ShowInstructions", true), "Setup Instructions", foldoutStyle))
            {
                SessionState.SetBool("InstaReload.ShowInstructions", true);

                EditorGUILayout.HelpBox(
                    "For hot reload to work properly, you need to configure Unity's Play Mode settings:",
                    MessageType.Info
                );

                EditorGUILayout.Space(5);

                EditorGUILayout.LabelField("1. Open Edit → Project Settings → Editor", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("2. Enable 'Enter Play Mode Options'", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("3. DISABLE 'Reload Domain'", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("4. DISABLE 'Reload Scene'", EditorStyles.wordWrappedLabel);

                EditorGUILayout.Space(5);

                if (GUILayout.Button("Open Project Settings"))
                {
                    SettingsService.OpenProjectSettings("Project/Editor");
                }
            }
            else
            {
                SessionState.SetBool("InstaReload.ShowInstructions", false);
            }
        }

        private void DrawStatistics()
        {
            EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);

            var boxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 10, 10)
            };

            EditorGUILayout.BeginVertical(boxStyle);
            EditorGUILayout.LabelField($"Play Mode Time: {EditorApplication.timeSinceStartup:F1}s");
            EditorGUILayout.LabelField($"Assemblies Monitored: Automatic");
            EditorGUILayout.EndVertical();
        }

        private void OnInspectorUpdate()
        {
            // Repaint to update status in real-time
            Repaint();
        }
    }
}
