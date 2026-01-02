using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Nimrita.InstaReload.Editor
{
    [Serializable]
    internal sealed class PatchRecord
    {
        public string patchId;
        public string assemblyName;
        public string sourceFilePath;
        public string sourceHash;
        public string patchAssemblyPath;
        public string runtimeModuleMvid;
        public PatchTokenPairRecord[] tokenPairs;
        public long timestampUtcTicks;
    }

    [Serializable]
    internal sealed class PatchTokenPairRecord
    {
        public int patchToken;
        public int runtimeToken;
    }

    [Serializable]
    internal sealed class PatchRecordList
    {
        public List<PatchRecord> records = new List<PatchRecord>();
    }

    internal static class PatchHistoryStore
    {
        private const string CacheFolderName = "Library";
        private const string CacheRootName = "InstaReload";
        private const string PatchFolderName = "Patches";
        private const string PatchIndexFileName = "patches.json";
        private static readonly object Sync = new object();

        internal static void RecordPatch(PatchApplyResult result, string sourceFilePath, byte[] assemblyBytes)
        {
            if (result == null || !result.AppliedAny)
            {
                return;
            }

            if (string.IsNullOrEmpty(sourceFilePath) || assemblyBytes == null || assemblyBytes.Length == 0)
            {
                return;
            }

            var sourceHash = ComputeFileHash(sourceFilePath);
            if (string.IsNullOrEmpty(sourceHash))
            {
                return;
            }

            try
            {
                lock (Sync)
                {
                    var cacheRoot = GetCacheRoot();
                    var patchDir = Path.Combine(cacheRoot, PatchFolderName);
                    Directory.CreateDirectory(cacheRoot);
                    Directory.CreateDirectory(patchDir);

                    var patchId = Guid.NewGuid().ToString("N");
                    var patchAssemblyPath = Path.Combine(patchDir, $"{patchId}.dll");
                    File.WriteAllBytes(patchAssemblyPath, assemblyBytes);

                    var records = LoadRecordsInternal();
                    var removed = new List<PatchRecord>();
                    records.RemoveAll(record =>
                    {
                        var match = string.Equals(record.sourceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(record.assemblyName, result.AssemblyName, StringComparison.Ordinal);
                        if (match)
                        {
                            removed.Add(record);
                        }
                        return match;
                    });
                    DeletePatchFiles(removed);

                    var tokenPairs = BuildTokenPairs(result.TokenPairs);

                    records.Add(new PatchRecord
                    {
                        patchId = patchId,
                        assemblyName = result.AssemblyName,
                        sourceFilePath = sourceFilePath,
                        sourceHash = sourceHash,
                        patchAssemblyPath = patchAssemblyPath,
                        runtimeModuleMvid = result.RuntimeModuleMvid.ToString("N"),
                        tokenPairs = tokenPairs,
                        timestampUtcTicks = DateTime.UtcNow.Ticks
                    });

                    SaveRecordsInternal(records);
                }
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogWarning($"[PatchHistory] Failed to record patch: {ex.Message}");
            }
        }

        internal static List<PatchRecord> LoadRecords()
        {
            lock (Sync)
            {
                return LoadRecordsInternal();
            }
        }

        internal static void RemoveRecords(IReadOnlyCollection<PatchRecord> staleRecords)
        {
            if (staleRecords == null || staleRecords.Count == 0)
            {
                return;
            }

            lock (Sync)
            {
                var records = LoadRecordsInternal();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var record in staleRecords)
                {
                    if (!string.IsNullOrEmpty(record.patchId))
                    {
                        ids.Add(record.patchId);
                    }
                }
                var removed = new List<PatchRecord>();
                records.RemoveAll(record =>
                {
                    var match = !string.IsNullOrEmpty(record.patchId) && ids.Contains(record.patchId);
                    if (match)
                    {
                        removed.Add(record);
                    }
                    return match;
                });
                DeletePatchFiles(removed);
                SaveRecordsInternal(records);
            }
        }

        internal static bool IsRecordValid(PatchRecord record)
        {
            if (record == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(record.sourceFilePath) || string.IsNullOrEmpty(record.sourceHash))
            {
                return false;
            }

            if (!File.Exists(record.sourceFilePath) ||
                string.IsNullOrEmpty(record.patchAssemblyPath) ||
                !File.Exists(record.patchAssemblyPath))
            {
                return false;
            }

            var currentHash = ComputeFileHash(record.sourceFilePath);
            return string.Equals(currentHash, record.sourceHash, StringComparison.Ordinal);
        }

        internal static bool TryCreateReplayContext(PatchRecord record, out PatchReplayContext context)
        {
            context = null;
            if (record == null || record.tokenPairs == null || record.tokenPairs.Length == 0)
            {
                return false;
            }

            if (!Guid.TryParse(record.runtimeModuleMvid, out var mvid))
            {
                return false;
            }

            var map = new Dictionary<int, int>(record.tokenPairs.Length);
            foreach (var pair in record.tokenPairs)
            {
                if (!map.ContainsKey(pair.patchToken))
                {
                    map[pair.patchToken] = pair.runtimeToken;
                }
            }

            if (map.Count == 0)
            {
                return false;
            }

            context = new PatchReplayContext(mvid, map);
            return true;
        }

        private static string GetCacheRoot()
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot ?? string.Empty, CacheFolderName, CacheRootName);
        }

        private static PatchTokenPairRecord[] BuildTokenPairs(IReadOnlyList<MethodTokenPair> pairs)
        {
            if (pairs == null || pairs.Count == 0)
            {
                return Array.Empty<PatchTokenPairRecord>();
            }

            var result = new PatchTokenPairRecord[pairs.Count];
            for (int i = 0; i < pairs.Count; i++)
            {
                result[i] = new PatchTokenPairRecord
                {
                    patchToken = pairs[i].PatchToken,
                    runtimeToken = pairs[i].RuntimeToken
                };
            }

            return result;
        }

        private static List<PatchRecord> LoadRecordsInternal()
        {
            try
            {
                var indexPath = Path.Combine(GetCacheRoot(), PatchIndexFileName);
                if (!File.Exists(indexPath))
                {
                    return new List<PatchRecord>();
                }

                var json = File.ReadAllText(indexPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<PatchRecord>();
                }

                var wrapper = JsonUtility.FromJson<PatchRecordList>(json);
                return wrapper?.records ?? new List<PatchRecord>();
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogWarning($"[PatchHistory] Failed to load records: {ex.Message}");
                return new List<PatchRecord>();
            }
        }

        private static void SaveRecordsInternal(List<PatchRecord> records)      
        {
            try
            {
                var indexPath = Path.Combine(GetCacheRoot(), PatchIndexFileName);
                var wrapper = new PatchRecordList { records = records ?? new List<PatchRecord>() };
                var json = JsonUtility.ToJson(wrapper, prettyPrint: true);
                File.WriteAllText(indexPath, json);
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogWarning($"[PatchHistory] Failed to save records: {ex.Message}");
            }
        }

        private static void DeletePatchFiles(List<PatchRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                return;
            }

            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record.patchAssemblyPath))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(record.patchAssemblyPath))
                    {
                        File.Delete(record.patchAssemblyPath);
                    }
                }
                catch (Exception ex)
                {
                    InstaReloadLogger.LogWarning($"[PatchHistory] Failed to delete patch file: {ex.Message}");
                }
            }
        }

        private static string ComputeFileHash(string filePath)
        {
            try
            {
                using (var stream = File.OpenRead(filePath))
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(stream);
                    var builder = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++)
                    {
                        builder.Append(hash[i].ToString("x2"));
                    }
                    return builder.ToString();
                }
            }
            catch (Exception ex)
            {
                InstaReloadLogger.LogWarning($"[PatchHistory] Failed to hash {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }
    }
}
