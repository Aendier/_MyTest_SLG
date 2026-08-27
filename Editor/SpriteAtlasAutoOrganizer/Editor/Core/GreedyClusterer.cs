using System;
using System.Collections.Generic;

namespace SpriteAtlasAutoOrganizer.Editor
{
    internal sealed class ClusterWork
    {
        public int Id;
        public string Domain;
        public string Reason;
        public bool Frozen;
        public readonly HashSet<SpriteKey> Sprites = new HashSet<SpriteKey>();
        public long EstimatedArea;
    }

    internal sealed class ClusterConstraints
    {
        public int MaxSpriteCount = 500;
        public long MaxEstimatedArea = 2048L * 2048L;
        public bool AllowCrossPackage;
        public HashSet<SpriteKey> NeverShareKeys = new HashSet<SpriteKey>();
    }

    /// <summary>
    /// 贪心聚类：反复合并当前共现分最高且满足硬约束的 Cluster。
    /// 不追求全局最优，保证结果稳定、可维护。
    /// </summary>
    internal static class GreedyClusterer
    {
        public static List<ClusterWork> Cluster(
            IEnumerable<SpriteRecord> sprites,
            Dictionary<string, float> pairScores,
            IEnumerable<LockedSpriteGroup> lockedGroups,
            Func<string, SpriteKey?> resolveSprite,
            ClusterConstraints constraints)
        {
            var works = new List<ClusterWork>();
            var keyToCluster = new Dictionary<SpriteKey, int>();
            if (sprites == null)
                return works;

            OrganizerProgress.ThrowIfCanceled();

            int nextId = 0;
            foreach (SpriteRecord sprite in sprites)
            {
                if (sprite == null || sprite.InManualAtlas)
                    continue;

                var work = new ClusterWork
                {
                    Id = nextId++,
                    Domain = sprite.Domain ?? "Default",
                    Frozen = constraints != null &&
                             constraints.NeverShareKeys != null &&
                             constraints.NeverShareKeys.Contains(sprite.Key),
                    EstimatedArea = sprite.EstimatedArea,
                    Reason = sprite.NeverShare ? "NeverShare" : "Initial"
                };
                work.Sprites.Add(sprite.Key);
                keyToCluster[sprite.Key] = work.Id;
                works.Add(work);
            }

            MergeLockedGroups(works, keyToCluster, lockedGroups, resolveSprite);

            if (pairScores == null || pairScores.Count == 0)
                return Compact(works);

            var active = new Dictionary<int, ClusterWork>();
            foreach (ClusterWork work in works)
            {
                if (work.Sprites.Count > 0)
                    active[work.Id] = work;
            }

            // 按边权从高到低扫一遍合并（Kruskal），避免每次合并都扫全量边把编辑器卡死。
            var edges = new List<KeyValuePair<string, float>>(pairScores.Count);
            foreach (KeyValuePair<string, float> pair in pairScores)
                edges.Add(pair);
            edges.Sort(CompareEdges);

            for (int i = 0; i < edges.Count; i++)
            {
                if ((i & 1023) == 0)
                    OrganizerProgress.ThrowIfCanceled();

                SpriteKey leftKey;
                SpriteKey rightKey;
                if (!CooccurrenceBuilder.TrySplitPairKey(edges[i].Key, out leftKey, out rightKey))
                    continue;

                int leftId;
                int rightId;
                if (!keyToCluster.TryGetValue(leftKey, out leftId) ||
                    !keyToCluster.TryGetValue(rightKey, out rightId) ||
                    leftId == rightId)
                    continue;

                ClusterWork left;
                ClusterWork right;
                if (!active.TryGetValue(leftId, out left) ||
                    !active.TryGetValue(rightId, out right))
                    continue;

                if (!CanMerge(left, right, constraints))
                    continue;

                UnionClusters(left, right, active, keyToCluster, edges[i].Value);
            }

            return Compact(active.Values);
        }

        private static void MergeLockedGroups(
            List<ClusterWork> works,
            Dictionary<SpriteKey, int> keyToCluster,
            IEnumerable<LockedSpriteGroup> lockedGroups,
            Func<string, SpriteKey?> resolveSprite)
        {
            if (lockedGroups == null)
                return;

            foreach (LockedSpriteGroup group in lockedGroups)
            {
                if (group == null || group.sprites == null || group.sprites.Length == 0)
                    continue;

                ClusterWork locked = null;
                foreach (string token in group.sprites)
                {
                    SpriteKey? resolved = resolveSprite != null ? resolveSprite(token) : null;
                    if (resolved == null)
                        continue;

                    SpriteKey key = resolved.Value;
                    int clusterId;
                    if (!keyToCluster.TryGetValue(key, out clusterId))
                        continue;

                    ClusterWork current = Find(works, clusterId);
                    if (current == null)
                        continue;

                    if (locked == null)
                    {
                        locked = current;
                        locked.Reason = "Locked:" + (group.groupName ?? string.Empty);
                        continue;
                    }

                    if (locked.Id == current.Id)
                        continue;

                    foreach (SpriteKey sprite in current.Sprites)
                    {
                        locked.Sprites.Add(sprite);
                        keyToCluster[sprite] = locked.Id;
                    }

                    locked.EstimatedArea += current.EstimatedArea;
                    current.Sprites.Clear();
                }
            }
        }

        private static ClusterWork Find(List<ClusterWork> works, int id)
        {
            for (int i = 0; i < works.Count; i++)
            {
                if (works[i].Id == id)
                    return works[i];
            }

            return null;
        }

        private static bool CanMerge(
            ClusterWork left,
            ClusterWork right,
            ClusterConstraints constraints)
        {
            if (left == null || right == null || left.Id == right.Id)
                return false;
            if (left.Frozen || right.Frozen)
                return false;

            if (constraints == null)
                return true;

            if (!constraints.AllowCrossPackage &&
                !string.Equals(left.Domain, right.Domain, StringComparison.Ordinal))
                return false;

            int totalCount = left.Sprites.Count + right.Sprites.Count;
            if (constraints.MaxSpriteCount > 0 && totalCount > constraints.MaxSpriteCount)
                return false;

            long totalArea = left.EstimatedArea + right.EstimatedArea;
            if (constraints.MaxEstimatedArea > 0 && totalArea > constraints.MaxEstimatedArea)
                return false;

            return true;
        }

        private static void UnionClusters(
            ClusterWork keep,
            ClusterWork drop,
            Dictionary<int, ClusterWork> active,
            Dictionary<SpriteKey, int> keyToCluster,
            float mergeScore)
        {
            foreach (SpriteKey sprite in drop.Sprites)
            {
                keep.Sprites.Add(sprite);
                keyToCluster[sprite] = keep.Id;
            }

            keep.EstimatedArea += drop.EstimatedArea;
            keep.Reason = "Cooccur:" + mergeScore.ToString("0");
            drop.Sprites.Clear();
            active.Remove(drop.Id);
        }

        private static int CompareEdges(
            KeyValuePair<string, float> left,
            KeyValuePair<string, float> right)
        {
            int score = right.Value.CompareTo(left.Value);
            if (score != 0)
                return score;
            return string.CompareOrdinal(left.Key, right.Key);
        }

        private static List<ClusterWork> Compact(IEnumerable<ClusterWork> works)
        {
            var result = new List<ClusterWork>();
            foreach (ClusterWork work in works)
            {
                if (work != null && work.Sprites.Count > 0)
                    result.Add(work);
            }

            result.Sort(CompareWorks);
            return result;
        }

        private static int CompareWorks(ClusterWork left, ClusterWork right)
        {
            int domain = string.CompareOrdinal(left.Domain, right.Domain);
            if (domain != 0)
                return domain;

            var leftTokens = new List<string>();
            foreach (SpriteKey key in left.Sprites)
                leftTokens.Add(key.Token);
            leftTokens.Sort(StringComparer.Ordinal);

            var rightTokens = new List<string>();
            foreach (SpriteKey key in right.Sprites)
                rightTokens.Add(key.Token);
            rightTokens.Sort(StringComparer.Ordinal);

            int count = Math.Min(leftTokens.Count, rightTokens.Count);
            for (int i = 0; i < count; i++)
            {
                int cmp = string.CompareOrdinal(leftTokens[i], rightTokens[i]);
                if (cmp != 0)
                    return cmp;
            }

            return leftTokens.Count.CompareTo(rightTokens.Count);
        }
    }
}
