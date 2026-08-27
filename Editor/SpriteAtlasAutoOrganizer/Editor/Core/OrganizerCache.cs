using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SpriteAtlasAutoOrganizer.Editor
{
    [Serializable]
    internal sealed class OrganizerCacheRoot
    {
        public List<HostCacheEntry> hosts = new List<HostCacheEntry>();
        public List<AtlasCacheEntry> atlases = new List<AtlasCacheEntry>();
    }

    [Serializable]
    internal sealed class HostCacheEntry
    {
        public string guid;
        public string path;
        public bool isScene;
        public string dependencyHash;
        public string spriteHash;
        public List<string> spriteTokens = new List<string>();
    }

    [Serializable]
    internal sealed class AtlasCacheEntry
    {
        public string name;
        public string path;
        public string contentHash;
    }

    /// <summary>
    /// 增量缓存，落在 Library 下，不进 SVN。
    /// </summary>
    internal static class OrganizerCache
    {
        private const string FileName = "ReferenceCache.json";

        public static string GetCachePath(SpriteAtlasAutoOrganizerConfig config)
        {
            string folder = config != null && !string.IsNullOrEmpty(config.cacheDirectory)
                ? config.cacheDirectory
                : "Library/SpriteAtlasAutoOrganizer";
            return Path.Combine(folder, FileName).Replace('\\', '/');
        }

        public static OrganizerCacheRoot Load(SpriteAtlasAutoOrganizerConfig config)
        {
            string path = GetCachePath(config);
            if (!File.Exists(path))
                return new OrganizerCacheRoot();

            try
            {
                string json = File.ReadAllText(path);
                var root = JsonUtility.FromJson<OrganizerCacheRoot>(json);
                return root ?? new OrganizerCacheRoot();
            }
            catch (Exception)
            {
                return new OrganizerCacheRoot();
            }
        }

        public static void Save(SpriteAtlasAutoOrganizerConfig config, OrganizerCacheRoot root)
        {
            if (root == null)
                return;

            string path = GetCachePath(config);
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(path, JsonUtility.ToJson(root, true));
        }

        public static Dictionary<string, HostCacheEntry> IndexHosts(OrganizerCacheRoot root)
        {
            var map = new Dictionary<string, HostCacheEntry>();
            if (root == null || root.hosts == null)
                return map;

            for (int i = 0; i < root.hosts.Count; i++)
            {
                HostCacheEntry entry = root.hosts[i];
                if (entry != null && !string.IsNullOrEmpty(entry.guid))
                    map[entry.guid] = entry;
            }

            return map;
        }
    }
}
