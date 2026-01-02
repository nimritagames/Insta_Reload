using Nimrita.InstaReload;
using Nimrita.InstaReload.Editor;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor.UI
{
    public class InstaReloadWindow : EditorWindow
    {
        private const float LabelWidth = 140f;
        private Vector2 _scrollPosition;
        private static InstaReloadWindow _instance;
        private static bool _stylesInitialized;
        private static bool _stylesProSkin;
        private static GUIStyle _headerTitleStyle;
        private static GUIStyle _headerSubtitleStyle;
        private static GUIStyle _sectionHeaderStyle;
        private static GUIStyle _cardStyle;
        private static GUIStyle _keyLabelStyle;
        private static GUIStyle _valueLabelStyle;

        [MenuItem("Window/InstaReload Settings")]
        public static void ShowWindow()
        {
            _instance = GetWindow<InstaReloadWindow>("InstaReload");
            _instance.minSize = new Vector2(420, 360);
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

            EnsureStyles();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawHeader();
            EditorGUILayout.Space(12);

            DrawStatusCard(settings);
            EditorGUILayout.Space(12);

            DrawSettingsCard(settings);
            EditorGUILayout.Space(12);

            DrawPlayModeAutomation(settings);
            EditorGUILayout.Space(12);

            DrawSetupGuidance(settings);
            EditorGUILayout.Space(12);

            if (EditorApplication.isPlaying)
            {
                DrawStatistics();
                EditorGUILayout.Space(12);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            var headerRect = GUILayoutUtility.GetRect(0f, 70f, GUILayout.ExpandWidth(true));
            var headerBg = EditorGUIUtility.isProSkin
                ? new Color(0.14f, 0.14f, 0.16f)
                : new Color(0.92f, 0.93f, 0.96f);
            var accent = EditorGUIUtility.isProSkin
                ? new Color(0.28f, 0.86f, 0.56f)
                : new Color(0.16f, 0.62f, 0.36f);

            EditorGUI.DrawRect(headerRect, headerBg);
            EditorGUI.DrawRect(new Rect(headerRect.x, headerRect.yMax - 2f, headerRect.width, 2f), accent);

            var titleRect = new Rect(headerRect.x + 14f, headerRect.y + 12f, headerRect.width - 28f, 26f);
            var subtitleRect = new Rect(headerRect.x + 14f, headerRect.y + 38f, headerRect.width - 28f, 18f);

            GUI.Label(titleRect, "InstaReload", _headerTitleStyle);
            GUI.Label(subtitleRect, "Hot Reload for Unity", _headerSubtitleStyle);
        }

        private void DrawStatusCard(InstaReloadSettings settings)
        {
            BeginCard();
            DrawSectionHeader("Status");

            var statusColor = settings.Enabled
                ? (EditorApplication.isPlaying ? new Color(0.35f, 0.85f, 0.35f) : new Color(0.95f, 0.75f, 0.2f))
                : new Color(0.6f, 0.6f, 0.6f);
            var statusText = settings.Enabled
                ? (EditorApplication.isPlaying ? "Active" : "Ready")
                : "Disabled";

            DrawKeyValueRow("Hot Reload", statusText, statusColor);
            DrawKeyValueRow("Mode", EditorApplication.isPlaying ? "Play Mode" : "Edit Mode");

            var configured = InstaReloadPlayModeSettings.IsConfigured(out var details);
            DrawKeyValueRow(
                "Play Mode Options",
                configured ? "Configured" : $"Needs updates ({details})",
                configured ? new Color(0.35f, 0.85f, 0.35f) : new Color(0.95f, 0.65f, 0.25f));
            DrawKeyValueRow("Auto Apply", settings.AutoApplyPlayModeSettings ? "On" : "Off");

            if (settings.Enabled && !EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to start hot reloading.", MessageType.Info);
            }
            else if (!settings.Enabled)
            {
                EditorGUILayout.HelpBox("Hot reload is disabled. Enable it below to use.", MessageType.Warning);
            }

            EndCard();
        }

        private void DrawSettingsCard(InstaReloadSettings settings)
        {
            BeginCard();
            DrawSectionHeader("Settings");

            EditorGUI.BeginChangeCheck();

            settings.Enabled = EditorGUILayout.Toggle(
                new GUIContent("Enable Hot Reload", "Enable or disable hot reload during Play Mode"),
                settings.Enabled);

            settings.EnabledLogLevels = (InstaReloadLogLevel)EditorGUILayout.EnumFlagsField(
                new GUIContent("Log Levels", "Select which log levels are emitted"),
                settings.EnabledLogLevels);

            settings.EnabledLogCategories = (InstaReloadLogCategory)EditorGUILayout.EnumFlagsField(
                new GUIContent("Log Categories", "Select which log categories are emitted"),
                settings.EnabledLogCategories);

            EditorGUILayout.HelpBox(
                "Toggle the Dispatcher category to show or hide runtime dispatch diagnostics.",
                MessageType.None);

            if (EditorGUI.EndChangeCheck())
            {
                SaveSettings(settings);
                if (EditorApplication.isPlaying)
                {
                    HotReloadDispatcher.ConfigureLogging(
                        settings.EnabledLogCategories,
                        settings.EnabledLogLevels);
                }
            }

            EndCard();
        }

        private void DrawPlayModeAutomation(InstaReloadSettings settings)
        {
            BeginCard();
            DrawSectionHeader("Play Mode Automation");

            EditorGUI.BeginChangeCheck();
            settings.AutoApplyPlayModeSettings = EditorGUILayout.Toggle(
                new GUIContent(
                    "Auto-apply Play Mode settings",
                    "Automatically enable Enter Play Mode Options and disable Reload Domain/Scene"),
                settings.AutoApplyPlayModeSettings);
            if (EditorGUI.EndChangeCheck())
            {
                SaveSettings(settings);
                if (settings.AutoApplyPlayModeSettings)
                {
                    InstaReloadPlayModeSettings.ApplyRecommendedSettings();
                }
            }

            var configured = InstaReloadPlayModeSettings.IsConfigured(out var details);
            EditorGUILayout.HelpBox(
                configured
                    ? "Play Mode settings are configured for InstaReload."
                    : $"Play Mode settings need updates: {details}.",
                configured ? MessageType.Info : MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Apply Recommended Play Mode Settings", GUILayout.Width(260f)))
                {
                    if (InstaReloadPlayModeSettings.ApplyRecommendedSettings())
                    {
                        InstaReloadLogger.Log(InstaReloadLogCategory.UI, "Applied recommended Play Mode settings.");
                    }
                }
            }

            EndCard();
        }

        private void DrawSetupGuidance(InstaReloadSettings settings)
        {
            var configured = InstaReloadPlayModeSettings.IsConfigured(out _);
            if (settings.AutoApplyPlayModeSettings && configured)
            {
                BeginCard();
                DrawSectionHeader("Setup");
                EditorGUILayout.HelpBox(
                    "Play Mode settings are automatically configured. No manual setup needed.",
                    MessageType.Info);
                EndCard();
                return;
            }

            BeginCard();
            DrawSectionHeader("Setup");

            var foldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            if (EditorGUILayout.Foldout(SessionState.GetBool("InstaReload.ShowInstructions", true), "Setup Instructions", foldoutStyle))
            {
                SessionState.SetBool("InstaReload.ShowInstructions", true);

                EditorGUILayout.HelpBox(
                    "For hot reload to work properly, configure Unity's Play Mode settings. Use Play Mode Automation above to apply these automatically.",
                    MessageType.Info);

                EditorGUILayout.Space(5);

                EditorGUILayout.LabelField("1. Open Edit -> Project Settings -> Editor", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("2. Enable 'Enter Play Mode Options'", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("3. Disable 'Reload Domain'", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("4. Disable 'Reload Scene'", EditorStyles.wordWrappedLabel);

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

            EndCard();
        }

        private void DrawStatistics()
        {
            BeginCard();
            DrawSectionHeader("Session");

            var snapshot = InstaReloadSessionMetrics.GetSnapshot();

            DrawKeyValueRow("Play Mode Time", $"{EditorApplication.timeSinceStartup:F1}s");
            DrawKeyValueRow("Assemblies", "Automatic");
            DrawKeyValueRow("Status", GetStatusText(snapshot));

            if (!string.IsNullOrEmpty(snapshot.LastFileName))
            {
                DrawKeyValueRow("Last File", snapshot.LastFileName);
            }

            DrawKeyValueRow(
                "Compile Last",
                $"{FormatDuration(snapshot.LastCompileMs)} ({FormatCompilePath(snapshot.LastCompilePath)})");
            DrawKeyValueRow(
                "Compile Avg",
                $"Fast {FormatDuration(snapshot.FastCompileAverageMs)} ({snapshot.FastCompileCount}) | Slow {FormatDuration(snapshot.SlowCompileAverageMs)} ({snapshot.SlowCompileCount})");
            DrawKeyValueRow(
                "Patch Last",
                $"{FormatDuration(snapshot.LastPatchMs)} | Ok {snapshot.PatchSuccessCount} / Fail {snapshot.PatchFailureCount} (Attempts {snapshot.PatchAttemptCount})");
            DrawKeyValueRow(
                "Last Patch",
                $"Patched {snapshot.LastPatchedCount}, Dispatched {snapshot.LastDispatchedCount}, Trampolines {snapshot.LastTrampolineCount}, Skipped {snapshot.LastSkippedCount}, Errors {snapshot.LastErrorCount}");

            if (!string.IsNullOrEmpty(snapshot.LastErrorSummary))
            {
                EditorGUILayout.HelpBox($"Last Error: {snapshot.LastErrorSummary}", MessageType.Error);
            }

            EndCard();
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private static void EnsureStyles()
        {
            if (_stylesInitialized && _stylesProSkin == EditorGUIUtility.isProSkin)
            {
                return;
            }

            _stylesInitialized = true;
            _stylesProSkin = EditorGUIUtility.isProSkin;

            _headerTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 20,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = EditorGUIUtility.isProSkin ? Color.white : new Color(0.1f, 0.1f, 0.1f) }
            };

            _headerSubtitleStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = EditorGUIUtility.isProSkin ? new Color(0.8f, 0.8f, 0.8f) : new Color(0.3f, 0.3f, 0.3f) }
            };

            _sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12
            };

            _cardStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(12, 12, 10, 12)
            };

            _keyLabelStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11
            };

            _valueLabelStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11
            };
        }

        private static void BeginCard()
        {
            EditorGUILayout.BeginVertical(_cardStyle);
        }

        private static void EndCard()
        {
            EditorGUILayout.EndVertical();
        }

        private static void DrawSectionHeader(string title)
        {
            EditorGUILayout.LabelField(title, _sectionHeaderStyle);
            EditorGUILayout.Space(4);
        }

        private static void DrawKeyValueRow(string label, string value)
        {
            DrawKeyValueRow(label, value, null);
        }

        private static void DrawKeyValueRow(string label, string value, Color? valueColor)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var previous = GUI.contentColor;
                EditorGUILayout.LabelField(label, _keyLabelStyle, GUILayout.Width(LabelWidth));
                if (valueColor.HasValue)
                {
                    GUI.contentColor = valueColor.Value;
                }
                EditorGUILayout.LabelField(value, _valueLabelStyle);
                GUI.contentColor = previous;
            }
        }

        private static void SaveSettings(InstaReloadSettings settings)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        private static string GetStatusText(InstaReloadSessionSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(snapshot.StatusDetail))
            {
                return snapshot.StatusDetail;
            }

            return snapshot.Status.ToString();
        }

        private static string FormatDuration(double ms)
        {
            return ms > 0 ? $"{ms:F0}ms" : "--";
        }

        private static string FormatCompilePath(InstaReloadCompilePath path)
        {
            switch (path)
            {
                case InstaReloadCompilePath.Fast:
                    return "fast";
                case InstaReloadCompilePath.Slow:
                    return "slow";
                default:
                    return "n/a";
            }
        }
    }
}
