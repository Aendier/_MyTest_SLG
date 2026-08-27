using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// 从 Prefab/Scene YAML 提取 Sprite PPtr，避免加载整个资源。
    /// 第一版只认项目内 type:3 引用，不解析代码字符串。
    /// </summary>
    internal static class SpriteYamlReferenceParser
    {
        private static readonly Regex SpritePtrRegex = new Regex(
            @"\{fileID:\s*(-?\d+)\s*,\s*guid:\s*([0-9a-fA-F]{32})\s*,\s*type:\s*3\}",
            RegexOptions.Compiled);

        public static List<SpriteKey> Parse(string yaml, IDictionary<string, SpriteKey> spriteLookup)
        {
            var result = new List<SpriteKey>();
            if (string.IsNullOrEmpty(yaml) || spriteLookup == null || spriteLookup.Count == 0)
                return result;

            var seen = new HashSet<SpriteKey>();
            MatchCollection matches = SpritePtrRegex.Matches(yaml);
            for (int i = 0; i < matches.Count; i++)
                TryAddMatch(matches[i], spriteLookup, seen, result);

            return result;
        }

        /// <summary>
        /// 逐行读文件，避免把超大 Prefab/Scene 整份读进内存后再 Regex。
        /// </summary>
        public static List<SpriteKey> ParseFile(string path, IDictionary<string, SpriteKey> spriteLookup)
        {
            var result = new List<SpriteKey>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || spriteLookup == null)
                return result;

            var seen = new HashSet<SpriteKey>();
            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf("guid:", System.StringComparison.Ordinal) < 0)
                        continue;

                    Match match = SpritePtrRegex.Match(line);
                    if (match.Success)
                        TryAddMatch(match, spriteLookup, seen, result);
                }
            }

            return result;
        }

        public static void AddLookup(
            IDictionary<string, SpriteKey> lookup,
            SpriteKey key)
        {
            if (lookup == null)
                return;

            lookup[key.Token] = key;
            if (!lookup.ContainsKey(key.Guid))
                lookup[key.Guid] = key;
        }

        private static void TryAddMatch(
            Match match,
            IDictionary<string, SpriteKey> spriteLookup,
            HashSet<SpriteKey> seen,
            List<SpriteKey> result)
        {
            long fileId;
            if (!long.TryParse(match.Groups[1].Value, out fileId) || fileId == 0)
                return;

            string guid = match.Groups[2].Value;
            SpriteKey key;
            if (!spriteLookup.TryGetValue(guid + ":" + fileId, out key) &&
                !spriteLookup.TryGetValue(guid, out key))
                return;

            if (seen.Add(key))
                result.Add(key);
        }
    }
}
