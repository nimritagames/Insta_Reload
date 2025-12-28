using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nimrita.InstaReload.Editor.Roslyn
{
    /// <summary>
    /// Resolves all assembly references needed for Roslyn compilation
    /// </summary>
    internal static class ReferenceResolver
    {
        private static List<string> _cachedReferences;

        public static List<string> GetAllReferences()
        {
            if (_cachedReferences != null)
                return _cachedReferences;

            var references = new HashSet<string>();

            // 1. Unity Engine assemblies
            AddUnityEngineReferences(references);

            // 2. Unity Editor assemblies (if compiling editor code)
            AddUnityEditorReferences(references);

            // 3. .NET Standard / Framework assemblies
            AddNetStandardReferences(references);

            // 4. Project assemblies (your game code)
            AddProjectReferences(references);

            // 5. Package assemblies
            AddPackageReferences(references);

            _cachedReferences = references.ToList();

            InstaReloadLogger.Log($"[Roslyn] Found {_cachedReferences.Count} assembly references");
            return _cachedReferences;
        }

        private static void AddUnityEngineReferences(HashSet<string> references)
        {
            var unityEnginePath = Path.Combine(EditorApplication.applicationContentsPath, "Managed");

            if (Directory.Exists(unityEnginePath))
            {
                // Core Unity assemblies
                var coreAssemblies = new[]
                {
                    "UnityEngine.dll",
                    "UnityEngine.CoreModule.dll",
                    "UnityEngine.PhysicsModule.dll",
                    "UnityEngine.Physics2DModule.dll",
                    "UnityEngine.UIModule.dll",
                    "UnityEngine.AnimationModule.dll",
                    "UnityEngine.AudioModule.dll",
                    "UnityEngine.InputModule.dll",
                    "UnityEngine.InputLegacyModule.dll"
                };

                foreach (var assembly in coreAssemblies)
                {
                    var path = Path.Combine(unityEnginePath, assembly);
                    if (File.Exists(path))
                        references.Add(path);
                }

                // All UnityEngine modules
                foreach (var dll in Directory.GetFiles(unityEnginePath, "UnityEngine.*.dll"))
                {
                    references.Add(dll);
                }
            }
        }

        private static void AddUnityEditorReferences(HashSet<string> references)
        {
            var unityEnginePath = Path.Combine(EditorApplication.applicationContentsPath, "Managed");

            if (Directory.Exists(unityEnginePath))
            {
                var editorAssemblies = new[]
                {
                    "UnityEditor.dll",
                    "UnityEditor.CoreModule.dll"
                };

                foreach (var assembly in editorAssemblies)
                {
                    var path = Path.Combine(unityEnginePath, assembly);
                    if (File.Exists(path))
                        references.Add(path);
                }
            }
        }

        private static void AddNetStandardReferences(HashSet<string> references)
        {
            // .NET Standard 2.1
            var netStandardPath = Path.Combine(
                EditorApplication.applicationContentsPath,
                "NetStandard", "ref", "2.1.0"
            );

            if (Directory.Exists(netStandardPath))
            {
                foreach (var dll in Directory.GetFiles(netStandardPath, "*.dll"))
                {
                    references.Add(dll);
                }
            }

            // Managed assemblies (System.*, etc.)
            var managedPath = Path.Combine(EditorApplication.applicationContentsPath, "Managed");
            if (Directory.Exists(managedPath))
            {
                foreach (var dll in Directory.GetFiles(managedPath, "System.*.dll"))
                {
                    references.Add(dll);
                }

                // Add core mscorlib
                var mscorlib = Path.Combine(managedPath, "mscorlib.dll");
                if (File.Exists(mscorlib))
                    references.Add(mscorlib);
            }
        }

        private static void AddProjectReferences(HashSet<string> references)
        {
            // Get all compiled project assemblies from Library/ScriptAssemblies
            var scriptAssembliesPath = Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies");

            if (Directory.Exists(scriptAssembliesPath))
            {
                foreach (var dll in Directory.GetFiles(scriptAssembliesPath, "*.dll"))
                {
                    // Skip the assembly we're currently hot reloading
                    var fileName = Path.GetFileName(dll);
                    if (!fileName.StartsWith("Unity") && !fileName.Contains("Editor"))
                    {
                        references.Add(dll);
                    }
                }
            }
        }

        private static void AddPackageReferences(HashSet<string> references)
        {
            // Unity packages (TextMeshPro, etc.)
            var packagesPath = Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies");

            if (Directory.Exists(packagesPath))
            {
                foreach (var dll in Directory.GetFiles(packagesPath, "Unity.*.dll"))
                {
                    references.Add(dll);
                }
            }
        }

        public static void ClearCache()
        {
            _cachedReferences = null;
        }
    }
}
