using Nimrita.InstaReload;
using UnityEngine;

namespace Nimrita.InstaReload.Editor
{
    internal static class InstaReloadLogger
    {
        private const string Prefix = "[InstaReload] ";

        internal static void Log(string message)
        {
            Log(InstaReloadLogLevel.Info, InferCategory(message), message);
        }

        internal static void LogVerbose(string message)
        {
            Log(InstaReloadLogLevel.Verbose, InferCategory(message), message);
        }

        internal static void LogWarning(string message)
        {
            Log(InstaReloadLogLevel.Warning, InferCategory(message), message);
        }

        internal static void LogError(string message)
        {
            Log(InstaReloadLogLevel.Error, InferCategory(message), message);
        }

        internal static void Log(InstaReloadLogCategory category, string message)
        {
            Log(InstaReloadLogLevel.Info, category, message);
        }

        internal static void LogVerbose(InstaReloadLogCategory category, string message)
        {
            Log(InstaReloadLogLevel.Verbose, category, message);
        }

        internal static void LogWarning(InstaReloadLogCategory category, string message)
        {
            Log(InstaReloadLogLevel.Warning, category, message);
        }

        internal static void LogError(InstaReloadLogCategory category, string message)
        {
            Log(InstaReloadLogLevel.Error, category, message);
        }

        private static void Log(InstaReloadLogLevel level, InstaReloadLogCategory category, string message)
        {
            if (!ShouldLog(level, category))
            {
                return;
            }

            var formattedMessage = Prefix + EnsureCategoryPrefix(category, message);
            switch (level)
            {
                case InstaReloadLogLevel.Warning:
                    Debug.LogWarning(formattedMessage);
                    break;
                case InstaReloadLogLevel.Error:
                    Debug.LogError(formattedMessage);
                    break;
                default:
                    Debug.Log(formattedMessage);
                    break;
            }
        }

        private static bool ShouldLog(InstaReloadLogLevel level, InstaReloadLogCategory category)
        {
            var settings = InstaReloadSettings.GetOrCreateSettings();
            if (settings == null)
            {
                return true;
            }

            if (!settings.IsLogLevelEnabled(level))
            {
                return false;
            }

            return settings.IsLogCategoryEnabled(category);
        }

        private static InstaReloadLogCategory InferCategory(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return InstaReloadLogCategory.General;
            }

            if (message.Contains("[Roslyn]"))
            {
                return InstaReloadLogCategory.Roslyn;
            }

            if (message.Contains("[FileDetector]"))
            {
                return InstaReloadLogCategory.FileDetector;
            }

            if (message.Contains("[Patcher]"))
            {
                return InstaReloadLogCategory.Patcher;
            }

            if (message.Contains("[Suppressor]"))
            {
                return InstaReloadLogCategory.Suppressor;
            }

            if (message.Contains("[ChangeAnalyzer]"))
            {
                return InstaReloadLogCategory.ChangeAnalyzer;
            }

            if (message.Contains("[Dispatcher]"))
            {
                return InstaReloadLogCategory.Dispatcher;
            }

            if (message.Contains("[UI]"))
            {
                return InstaReloadLogCategory.UI;
            }

            return InstaReloadLogCategory.General;
        }

        private static string EnsureCategoryPrefix(InstaReloadLogCategory category, string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return $"[{category}]";
            }

            if (message[0] == '[')
            {
                return message;
            }

            return $"[{category}] {message}";
        }
    }
}
