using System.Collections.Generic;
using UnityEditor;

namespace Nimrita.InstaReload.Editor
{
    internal static class InstaReloadPlayModeSettings
    {
        private const EnterPlayModeOptions RequiredOptions =
            EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

        internal static bool IsConfigured(out string details)
        {
            var issues = new List<string>();

            if (!EditorSettings.enterPlayModeOptionsEnabled)
            {
                issues.Add("Enable Enter Play Mode Options");
            }

            var options = EditorSettings.enterPlayModeOptions;
            if ((options & EnterPlayModeOptions.DisableDomainReload) == 0)
            {
                issues.Add("Disable Reload Domain");
            }

            if ((options & EnterPlayModeOptions.DisableSceneReload) == 0)
            {
                issues.Add("Disable Reload Scene");
            }

            if (issues.Count == 0)
            {
                details = "Configured";
                return true;
            }

            details = string.Join("; ", issues);
            return false;
        }

        internal static bool ApplyRecommendedSettings()
        {
            var changed = false;

            if (!EditorSettings.enterPlayModeOptionsEnabled)
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                changed = true;
            }

            var options = EditorSettings.enterPlayModeOptions;
            var updated = options | RequiredOptions;
            if (updated != options)
            {
                EditorSettings.enterPlayModeOptions = updated;
                changed = true;
            }

            return changed;
        }
    }
}
