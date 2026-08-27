using System.Collections.Generic;
using UnityEditor;

namespace SpriteAtlasAutoOrganizer.Editor
{
    internal static class AtlasPlanner
    {
        public static List<AtlasCluster> Plan(
            AnalysisResult analysis,
            SpriteAtlasAutoOrganizerConfig config)
        {
            var clusters = new List<AtlasCluster>();
            if (analysis == null)
                return clusters;

            var eligible = new List<SpriteRecord>();
            foreach (KeyValuePair<SpriteKey, SpriteRecord> pair in analysis.Sprites)
            {
                if (!pair.Value.InManualAtlas)
                    eligible.Add(pair.Value);
            }

            var hosts = new List<HostRecord>();
            CollectHosts(analysis.PrefabToSprites, false, hosts);
            CollectHosts(analysis.SceneToSprites, true, hosts);

            Dictionary<string, float> scores = CooccurrenceBuilder.Build(
                hosts,
                config != null ? config.prefabReferenceWeight : 10f,
                config != null ? config.sceneReferenceWeight : 3f,
                config != null ? config.maxSpritePerPrefab : 200,
                SpriteSizeEstimator.ToKeySet(eligible));

            var neverShare = new HashSet<SpriteKey>();
            foreach (SpriteRecord sprite in eligible)
            {
                if (sprite.NeverShare)
                    neverShare.Add(sprite.Key);
            }

            int maxSize = config != null ? config.maxAtlasSize : 2048;
            var constraints = new ClusterConstraints
            {
                MaxSpriteCount = config != null ? config.maxSpriteCount : 500,
                MaxEstimatedArea = (long)maxSize * maxSize,
                AllowCrossPackage = config != null && config.allowCrossPackage,
                NeverShareKeys = neverShare
            };

            List<ClusterWork> works = GreedyClusterer.Cluster(
                eligible,
                scores,
                config != null ? config.lockedGroups : null,
                token => ResolveSpriteToken(token, analysis.Sprites),
                constraints);

            for (int i = 0; i < works.Count; i++)
            {
                ClusterWork work = works[i];
                var cluster = new AtlasCluster
                {
                    Domain = work.Domain,
                    Reason = work.Reason,
                    EstimatedArea = work.EstimatedArea
                };
                cluster.Sprites.AddRange(work.Sprites);
                cluster.Sprites.Sort(CompareKeys);
                cluster.StableName = AtlasNamer.BuildStableName(work.Domain, cluster.Sprites);
                int width;
                int height;
                SpriteSizeEstimator.EstimateAtlasSize(work.EstimatedArea, maxSize, out width, out height);
                cluster.EstimatedWidth = width;
                cluster.EstimatedHeight = height;
                clusters.Add(cluster);
            }

            return clusters;
        }

        public static SpriteKey? ResolveSpriteToken(
            string token,
            IDictionary<SpriteKey, SpriteRecord> sprites)
        {
            if (string.IsNullOrEmpty(token) || sprites == null)
                return null;

            SpriteKey parsed;
            if (SpriteKey.TryParse(token, out parsed) && sprites.ContainsKey(parsed))
                return parsed;

            string guid = AssetDatabase.AssetPathToGUID(token);
            if (string.IsNullOrEmpty(guid))
                guid = token;

            foreach (KeyValuePair<SpriteKey, SpriteRecord> pair in sprites)
            {
                if (pair.Key.Guid == guid)
                    return pair.Key;
            }

            return null;
        }

        private static void CollectHosts(
            Dictionary<string, HashSet<SpriteKey>> map,
            bool isScene,
            List<HostRecord> hosts)
        {
            if (map == null)
                return;

            foreach (KeyValuePair<string, HashSet<SpriteKey>> pair in map)
            {
                var host = new HostRecord
                {
                    Guid = pair.Key,
                    IsScene = isScene
                };
                if (pair.Value != null)
                    host.Sprites.AddRange(pair.Value);
                hosts.Add(host);
            }
        }

        private static int CompareKeys(SpriteKey left, SpriteKey right)
        {
            int guid = string.CompareOrdinal(left.Guid, right.Guid);
            return guid != 0 ? guid : left.FileId.CompareTo(right.FileId);
        }
    }
}
