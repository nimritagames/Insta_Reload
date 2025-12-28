using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor.Roslyn
{
    /// <summary>
    /// Automatically downloads and installs Roslyn DLLs for instant hot reload
    /// </summary>
    internal static class RoslynInstaller
    {
        private const string ROSLYN_VERSION = "4.3.0";
        private static readonly string LibsPath = Path.Combine(Application.dataPath, "InstaReload", "Editor", "Roslyn", "Libs");

        [MenuItem("InstaReload/Install Roslyn Compiler (Required for <300ms Hot Reload)", priority = 100)]
        private static void InstallRoslyn()
        {
            if (IsRoslynInstalled())
            {
                if (EditorUtility.DisplayDialog(
                    "Roslyn Already Installed",
                    "Roslyn compiler is already installed.\n\nDo you want to reinstall?",
                    "Reinstall", "Cancel"))
                {
                    PerformInstallation();
                }
            }
            else
            {
                if (EditorUtility.DisplayDialog(
                    "Install Roslyn Compiler",
                    "This will download and install Microsoft Roslyn compiler.\n\n" +
                    "Size: ~5 MB\n" +
                    "Benefit: <300ms hot reload (instead of 2-3 seconds)\n\n" +
                    "Continue?",
                    "Install", "Cancel"))
                {
                    PerformInstallation();
                }
            }
        }

        private static void PerformInstallation()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Installing Roslyn", "Creating directories...", 0.1f);

                // Create Libs directory
                if (!Directory.Exists(LibsPath))
                {
                    Directory.CreateDirectory(LibsPath);
                }

                var tempPath = Path.Combine(Path.GetTempPath(), "InstaReload_Roslyn");
                if (!Directory.Exists(tempPath))
                {
                    Directory.CreateDirectory(tempPath);
                }

                // Download NuGet packages (including all dependencies)
                var packages = new[]
                {
                    ("Microsoft.CodeAnalysis.CSharp", "4.3.0"),
                    ("Microsoft.CodeAnalysis.Common", "4.3.0"),
                    ("System.Collections.Immutable", "6.0.0"),
                    ("System.Reflection.Metadata", "6.0.0"),
                    ("System.Runtime.CompilerServices.Unsafe", "6.0.0"),
                    ("System.Memory", "4.5.5"),
                    ("System.Text.Encoding.CodePages", "6.0.0")
                };

                for (int i = 0; i < packages.Length; i++)
                {
                    var (packageName, version) = packages[i];
                    var progress = 0.2f + (i * 0.15f);

                    EditorUtility.DisplayProgressBar("Installing Roslyn",
                        $"Downloading {packageName}...", progress);

                    var nupkgPath = Path.Combine(tempPath, $"{packageName}.{version}.nupkg");
                    var url = $"https://www.nuget.org/api/v2/package/{packageName}/{version}";

                    using (var client = new WebClient())
                    {
                        client.DownloadFile(url, nupkgPath);
                    }

                    // Extract DLLs from .nupkg (it's a ZIP file)
                    EditorUtility.DisplayProgressBar("Installing Roslyn",
                        $"Extracting {packageName}...", progress + 0.05f);

                    ExtractDllsFromNupkg(nupkgPath, LibsPath);
                }

                EditorUtility.ClearProgressBar();

                if (IsRoslynInstalled())
                {
                    EditorUtility.DisplayDialog(
                        "Installation Complete!",
                        "Roslyn compiler installed successfully!\n\n" +
                        "⚡ Hot reload will now be <300ms with no Unity popup.\n\n" +
                        "Please restart Unity for changes to take effect.",
                        "OK"
                    );

                    InstaReloadLogger.Log("[Roslyn] Installation complete! Restart Unity to activate.");
                }
                else
                {
                    ShowManualInstructionsWindow();
                }

                // Cleanup
                try
                {
                    Directory.Delete(tempPath, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                InstaReloadLogger.LogError($"[Roslyn] Auto-install failed: {ex.Message}");

                if (EditorUtility.DisplayDialog(
                    "Auto-Install Failed",
                    $"Automatic installation failed:\n{ex.Message}\n\n" +
                    "Would you like manual installation instructions instead?",
                    "Show Instructions", "Cancel"))
                {
                    ShowManualInstructionsWindow();
                }
            }
        }

        private static void ExtractDllsFromNupkg(string nupkgPath, string outputPath)
        {
            using (var archive = ZipFile.OpenRead(nupkgPath))
            {
                foreach (var entry in archive.Entries)
                {
                    // Look for DLLs in lib/netstandard2.0 or lib/netstandard2.1
                    if ((entry.FullName.StartsWith("lib/netstandard2.0/") ||
                         entry.FullName.StartsWith("lib/netstandard2.1/")) &&
                        entry.Name.EndsWith(".dll"))
                    {
                        var destPath = Path.Combine(outputPath, entry.Name);

                        // Skip if already exists
                        if (File.Exists(destPath))
                        {
                            File.Delete(destPath);
                        }

                        entry.ExtractToFile(destPath, true);
                        InstaReloadLogger.Log($"[Roslyn] Extracted: {entry.Name}");
                    }
                }
            }
        }

        private static void ShowManualInstructionsWindow()
        {
            var message = "MANUAL INSTALLATION REQUIRED\n\n" +
                         "Due to Unity restrictions, please follow these steps:\n\n" +
                         "1. Download Roslyn from NuGet.org:\n" +
                         "   • Microsoft.CodeAnalysis.CSharp v4.3.0\n" +
                         "   • Microsoft.CodeAnalysis.Common v4.3.0\n\n" +
                         "2. Extract the .nupkg files (they're ZIP files)\n\n" +
                         "3. Copy these DLLs to:\n" +
                         $"   {LibsPath}\n\n" +
                         "   Required files:\n" +
                         "   • Microsoft.CodeAnalysis.dll\n" +
                         "   • Microsoft.CodeAnalysis.CSharp.dll\n" +
                         "   • System.Collections.Immutable.dll\n" +
                         "   • System.Reflection.Metadata.dll\n\n" +
                         "4. Restart Unity\n\n" +
                         "Full guide: Assets/InstaReload/Editor/Roslyn/ROSLYN_SETUP.md";

            if (EditorUtility.DisplayDialog("Installation Instructions", message, "Open NuGet.org", "Close"))
            {
                Application.OpenURL("https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/4.3.0");
            }

            // Open the Libs folder
            EditorUtility.RevealInFinder(LibsPath);
        }

        public static bool IsRoslynInstalled()
        {
            if (!Directory.Exists(LibsPath))
                return false;

            var requiredDLLs = new[]
            {
                "Microsoft.CodeAnalysis.dll",
                "Microsoft.CodeAnalysis.CSharp.dll"
            };

            foreach (var dll in requiredDLLs)
            {
                var path = Path.Combine(LibsPath, dll);
                if (!File.Exists(path))
                    return false;
            }

            return true;
        }

        [MenuItem("InstaReload/Check Roslyn Status", priority = 101)]
        private static void CheckStatus()
        {
            if (IsRoslynInstalled())
            {
                if (RoslynCompiler.IsAvailable)
                {
                    EditorUtility.DisplayDialog(
                        "Roslyn Status",
                        "✓ Roslyn is installed and working!\n\n" +
                        "Hot reload will be <300ms with no compilation popup.",
                        "OK"
                    );
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Roslyn Status",
                        "⚠ Roslyn DLLs found but not loading properly.\n\n" +
                        "Try restarting Unity.",
                        "OK"
                    );
                }
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Roslyn Status",
                    "✗ Roslyn is NOT installed.\n\n" +
                    "Hot reload will use Unity's compiler (2-3 seconds).\n\n" +
                    "Install Roslyn for <300ms instant hot reload!",
                    "OK"
                );
            }
        }
    }
}
