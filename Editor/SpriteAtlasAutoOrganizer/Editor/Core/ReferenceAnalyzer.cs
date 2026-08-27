using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// 建立 Prefab/Scene → Sprite 双向索引。内部一律用 GUID+fileID。
    /// </summary>
    internal static class ReferenceAnalyzer
    {
        public static List<HostRecord> AnalyzeHosts(
            ScanResult scan,
            IDictionary<string, SpriteKey> spriteLookup,
            Dictionary<string, HostCacheEntry> cache,
            bool incremental)
        {
            var hosts = new List<HostRecord>();
            if (scan == null)
                return hosts;

            AnalyzeList(scan.PrefabPaths, false, spriteLookup, cache, incremental, hosts);
            AnalyzeList(scan.ScenePaths, true, spriteLookup, cache, incremental, hosts);
            return hosts;
        }

        private static void AnalyzeList(
            List<string> paths,
            bool isScene,
            IDictionary<string, SpriteKey> spriteLookup,
            Dictionary<string, HostCacheEntry> cache,
            bool incremental,
            List<HostRecord> hosts)
        {
            if (paths == null)
                return;

            for (int i = 0; i < paths.Count; i++)
            {
                if ((i & 31) == 0)
                {
                    string kind = isScene ? "Scene" : "Prefab";
                    OrganizerProgress.Report(
                        "分析 " + kind + " " + (i + 1) + "/" + paths.Count,
                        isScene ? 0.55f : 0.35f);
                    OrganizerProgress.ThrowIfCanceled();
                }

                string path = paths[i];
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                    continue;

                // 不用 GetAssetDependencyHash：它会带出贴图依赖，全量扫描时极易把编辑器打崩。
                string depHash = FileStamp(path);
                HostCacheEntry cached;
                if (incremental &&
                    cache != null &&
                    cache.TryGetValue(guid, out cached) &&
                    cached.dependencyHash == depHash)
                {
                    hosts.Add(FromCache(cached, path, isScene, depHash));
                    continue;
                }

                List<SpriteKey> sprites = SpriteYamlReferenceParser.ParseFile(path, spriteLookup);
                var host = new HostRecord
                {
                    Guid = guid,
                    AssetPath = path,
                    IsScene = isScene,
                    DependencyHash = depHash
                };
                host.Sprites.AddRange(sprites);
                hosts.Add(host);
            }
        }

        private static string FileStamp(string path)
        {
            if (!File.Exists(path))
                return string.Empty;

            var info = new FileInfo(path);
            return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }

        private static HostRecord FromCache(
            HostCacheEntry cached,
            string path,
            bool isScene,
            string depHash)
        {
            var host = new HostRecord
            {
                Guid = cached.guid,
                AssetPath = path,
                IsScene = isScene,
                DependencyHash = depHash
            };

            if (cached.spriteTokens != null)
            {
                for (int i = 0; i < cached.spriteTokens.Count; i++)
                {
                    SpriteKey key;
                    if (SpriteKey.TryParse(cached.spriteTokens[i], out key))
                        host.Sprites.Add(key);
                }
            }

            return host;
        }
    }
}
