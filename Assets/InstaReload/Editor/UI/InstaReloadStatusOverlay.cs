using System.Collections.Generic;
using Nimrita.InstaReload.Editor;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor.UI
{
    [InitializeOnLoad]
    internal static class InstaReloadStatusOverlay
    {
        private static string _lastMessage = "";
        private static float _messageTime = 0f;
        private static float _messageDuration = 3f;
        private static Color _messageColor = Color.green;

        static InstaReloadStatusOverlay()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        public static void ShowMessage(string message, bool isSuccess = true)
        {
            _lastMessage = message;
            _messageTime = (float)EditorApplication.timeSinceStartup;
            _messageColor = isSuccess ? new Color(0.3f, 0.8f, 0.3f) : new Color(0.9f, 0.6f, 0.2f);
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!EditorApplication.isPlaying)
                return;

            var settings = InstaReloadSettings.GetOrCreateSettings();
            if (settings == null || !settings.Enabled)
                return;

            Handles.BeginGUI();

            // Status badge in top-right corner
            DrawStatusBadge(sceneView);

            // Temporary message (fades out)
            if (!string.IsNullOrEmpty(_lastMessage))
            {
                float elapsed = (float)EditorApplication.timeSinceStartup - _messageTime;
                if (elapsed < _messageDuration)
                {
                    DrawTemporaryMessage(sceneView, elapsed);
                }
                else
                {
                    _lastMessage = "";
                }
            }

            Handles.EndGUI();
        }

        private static void DrawStatusBadge(SceneView sceneView)
        {
            var snapshot = InstaReloadSessionMetrics.GetSnapshot();
            var lines = BuildStatusLines(snapshot);
            if (lines.Count == 0)
            {
                return;
            }

            const float padding = 6f;
            const float lineHeight = 16f;
            var badgeWidth = Mathf.Min(320f, sceneView.position.width - 20f);
            var badgeHeight = padding * 2 + lineHeight * lines.Count;
            var badgeRect = new Rect(
                sceneView.position.width - badgeWidth - 10,
                10,
                badgeWidth,
                badgeHeight
            );

            var boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTexture(2, 2, new Color(0.2f, 0.2f, 0.2f, 0.8f)) },
                border = new RectOffset(2, 2, 2, 2)
            };

            GUI.Box(badgeRect, "", boxStyle);

            var headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = Color.white }
            };

            var labelStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };

            var statusStyle = new GUIStyle(labelStyle)
            {
                normal = { textColor = GetStatusColor(snapshot.Status) }
            };

            var contentRect = new Rect(
                badgeRect.x + padding,
                badgeRect.y + padding,
                badgeRect.width - padding * 2,
                lineHeight);

            GUI.Label(contentRect, lines[0], headerStyle);

            for (int i = 1; i < lines.Count; i++)
            {
                contentRect.y += lineHeight;
                var style = i == 1 ? statusStyle : labelStyle;
                GUI.Label(contentRect, lines[i], style);
            }
        }

        private static void DrawTemporaryMessage(SceneView sceneView, float elapsed)
        {
            var messageSize = new Vector2(300, 40);
            var messageRect = new Rect(
                (sceneView.position.width - messageSize.x) / 2,
                sceneView.position.height - messageSize.y - 50,
                messageSize.x,
                messageSize.y
            );

            // Fade out animation
            float alpha = Mathf.Clamp01(1f - (elapsed / _messageDuration));
            var bgColor = new Color(_messageColor.r, _messageColor.g, _messageColor.b, alpha * 0.9f);

            var boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTexture(2, 2, bgColor) },
                border = new RectOffset(3, 3, 3, 3)
            };

            GUI.Box(messageRect, "", boxStyle);

            var labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, alpha) },
                wordWrap = true
            };

            GUI.Label(messageRect, _lastMessage, labelStyle);

            // Force repaint for animation
            sceneView.Repaint();
        }

        private static Texture2D MakeTexture(int width, int height, Color color)
        {
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            var texture = new Texture2D(width, height);
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static List<string> BuildStatusLines(InstaReloadSessionSnapshot snapshot)
        {
            var lines = new List<string>
            {
                "InstaReload",
                $"Status: {GetStatusText(snapshot)}"
            };

            var workerLine = Roslyn.InstaReloadWorkerClient.GetStatusLine();
            if (!string.IsNullOrEmpty(workerLine))
            {
                lines.Add(workerLine);
            }

            if (!string.IsNullOrEmpty(snapshot.LastFileName))
            {
                lines.Add($"File: {snapshot.LastFileName}");
            }

            if (snapshot.LastCompileMs > 0 || snapshot.FastCompileCount > 0 || snapshot.SlowCompileCount > 0)
            {
                lines.Add($"Compile: {FormatDuration(snapshot.LastCompileMs)} ({FormatCompilePath(snapshot.LastCompilePath)})");
                lines.Add($"Compile avg: fast {FormatDuration(snapshot.FastCompileAverageMs)} ({snapshot.FastCompileCount}), slow {FormatDuration(snapshot.SlowCompileAverageMs)} ({snapshot.SlowCompileCount})");
            }
            else
            {
                lines.Add("Compile: --");
            }

            if (snapshot.PatchAttemptCount > 0 || snapshot.LastPatchMs > 0)
            {
                lines.Add($"Patch: {FormatDuration(snapshot.LastPatchMs)} | ok {snapshot.PatchSuccessCount} / fail {snapshot.PatchFailureCount}");
                lines.Add($"Last patch: p{snapshot.LastPatchedCount} d{snapshot.LastDispatchedCount} t{snapshot.LastTrampolineCount} s{snapshot.LastSkippedCount} e{snapshot.LastErrorCount}");
            }
            else
            {
                lines.Add("Patch: --");
            }

            if (!string.IsNullOrEmpty(snapshot.LastErrorSummary))
            {
                lines.Add($"Error: {TrimText(snapshot.LastErrorSummary, 80)}");
            }

            return lines;
        }

        private static string GetStatusText(InstaReloadSessionSnapshot snapshot)
        {
            var status = string.IsNullOrEmpty(snapshot.StatusDetail)
                ? snapshot.Status.ToString()
                : snapshot.StatusDetail;

            if (!string.IsNullOrEmpty(snapshot.LastAssemblyName) &&
                snapshot.Status == InstaReloadOperationStatus.Patching)
            {
                status = $"{status} ({snapshot.LastAssemblyName})";
            }

            return status;
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

        private static string TrimText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text;
            }

            return text.Substring(0, maxLength - 3) + "...";
        }

        private static Color GetStatusColor(InstaReloadOperationStatus status)
        {
            switch (status)
            {
                case InstaReloadOperationStatus.Compiling:
                    return new Color(1f, 0.8f, 0.2f);
                case InstaReloadOperationStatus.Patching:
                    return new Color(0.9f, 0.6f, 0.2f);
                case InstaReloadOperationStatus.Succeeded:
                    return new Color(0.3f, 0.9f, 0.3f);
                case InstaReloadOperationStatus.Failed:
                    return new Color(0.9f, 0.4f, 0.4f);
                default:
                    return new Color(0.7f, 0.7f, 0.7f);
            }
        }
    }
}
