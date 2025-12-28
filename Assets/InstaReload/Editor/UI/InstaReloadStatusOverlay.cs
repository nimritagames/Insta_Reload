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
            var badgeSize = new Vector2(150, 30);
            var badgeRect = new Rect(
                sceneView.position.width - badgeSize.x - 10,
                10,
                badgeSize.x,
                badgeSize.y
            );

            var boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTexture(2, 2, new Color(0.2f, 0.2f, 0.2f, 0.8f)) },
                border = new RectOffset(2, 2, 2, 2)
            };

            GUI.Box(badgeRect, "", boxStyle);

            var labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.4f, 0.9f, 0.4f) }
            };

            GUI.Label(badgeRect, "⚡ Hot Reload Active", labelStyle);
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
    }
}
