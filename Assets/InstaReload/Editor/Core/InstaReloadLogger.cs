using UnityEngine;

namespace Nimrita.InstaReload.Editor
{
    internal static class InstaReloadLogger
    {
        private const string Prefix = "[InstaReload] ";

        internal static void Log(string message)
        {
            Debug.Log(Prefix + message);
        }

        internal static void LogVerbose(string message)
        {
            var settings = InstaReloadSettings.GetOrCreateSettings();
            if (settings != null && settings.VerboseLogging)
            {
                Debug.Log(Prefix + message);
            }
        }

        internal static void LogWarning(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        internal static void LogError(string message)
        {
            Debug.LogError(Prefix + message);
        }
    }
}
