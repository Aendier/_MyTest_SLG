using System;
using System.Collections.Generic;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// 按 Host（Prefab/Scene）反向生成共现，避免 Sprite×Sprite 全量 O(N²)。
    /// </summary>
    internal static class CooccurrenceBuilder
    {
        public static Dictionary<string, float> Build(
            IEnumerable<HostRecord> hosts,
            float prefabWeight,
            float sceneWeight,
            int maxSpritePerHost,
            HashSet<SpriteKey> eligibleSprites)
        {
            var scores = new Dictionary<string, float>();
            if (hosts == null)
                return scores;

            foreach (HostRecord host in hosts)
            {
                if (host == null || host.Sprites == null || host.Sprites.Count < 2)
                    continue;

                var unique = new List<SpriteKey>();
                var seen = new HashSet<SpriteKey>();
                foreach (SpriteKey key in host.Sprites)
                {
                    if (eligibleSprites != null && !eligibleSprites.Contains(key))
                        continue;
                    if (seen.Add(key))
                        unique.Add(key);
                }

                if (unique.Count < 2)
                    continue;

                // 超大 Prefab 不生成两两关系，避免单个 Host 炸出海量边。
                if (maxSpritePerHost > 0 && unique.Count > maxSpritePerHost)
                    continue;

                unique.Sort(CompareKeys);
                float weight = host.IsScene ? sceneWeight : prefabWeight;
                for (int i = 0; i < unique.Count; i++)
                {
                    for (int j = i + 1; j < unique.Count; j++)
                    {
                        string pairKey = MakePairKey(unique[i], unique[j]);
                        float current;
                        scores.TryGetValue(pairKey, out current);
                        scores[pairKey] = current + weight;
                    }
                }
            }

            return scores;
        }

        public static string MakePairKey(SpriteKey left, SpriteKey right)
        {
            return CompareKeys(left, right) <= 0
                ? left.Token + "|" + right.Token
                : right.Token + "|" + left.Token;
        }

        public static bool TrySplitPairKey(string pairKey, out SpriteKey left, out SpriteKey right)
        {
            left = default;
            right = default;
            if (string.IsNullOrEmpty(pairKey))
                return false;

            int split = pairKey.IndexOf('|');
            if (split <= 0)
                return false;

            return SpriteKey.TryParse(pairKey.Substring(0, split), out left) &&
                   SpriteKey.TryParse(pairKey.Substring(split + 1), out right);
        }

        private static int CompareKeys(SpriteKey left, SpriteKey right)
        {
            int guidCompare = string.CompareOrdinal(left.Guid, right.Guid);
            if (guidCompare != 0)
                return guidCompare;
            return left.FileId.CompareTo(right.FileId);
        }
    }
}
