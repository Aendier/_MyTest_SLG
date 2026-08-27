using System;
using System.Collections.Generic;

namespace SpriteAtlasAutoOrganizer.Editor
{
    internal static class AtlasDiffBuilder
    {
        public static List<AtlasDiffEntry> Build(
            IEnumerable<AtlasCluster> planned,
            IDictionary<string, HashSet<string>> existingAutoAtlases)
        {
            var diffs = new List<AtlasDiffEntry>();
            var plannedNames = new HashSet<string>();
            if (planned != null)
            {
                foreach (AtlasCluster cluster in planned)
                {
                    if (cluster == null || string.IsNullOrEmpty(cluster.StableName))
                        continue;

                    plannedNames.Add(cluster.StableName);
                    HashSet<string> before = null;
                    if (existingAutoAtlases != null)
                        existingAutoAtlases.TryGetValue(cluster.StableName, out before);

                    var after = new HashSet<string>();
                    for (int i = 0; i < cluster.Sprites.Count; i++)
                        after.Add(cluster.Sprites[i].Token);

                    var diff = new AtlasDiffEntry
                    {
                        AtlasName = cluster.StableName,
                        Reason = cluster.Reason,
                        IsNew = before == null
                    };

                    foreach (string token in after)
                    {
                        if (before == null || !before.Contains(token))
                            diff.Added.Add(token);
                    }

                    if (before != null)
                    {
                        foreach (string token in before)
                        {
                            if (!after.Contains(token))
                                diff.Removed.Add(token);
                        }
                    }

                    cluster.Changed = diff.IsNew || diff.Added.Count > 0 || diff.Removed.Count > 0;
                    if (cluster.Changed)
                        diffs.Add(diff);
                }
            }

            if (existingAutoAtlases != null)
            {
                foreach (KeyValuePair<string, HashSet<string>> pair in existingAutoAtlases)
                {
                    if (plannedNames.Contains(pair.Key))
                        continue;

                    var deleted = new AtlasDiffEntry
                    {
                        AtlasName = pair.Key,
                        IsDeleted = true,
                        Reason = "Plan no longer contains this atlas"
                    };
                    if (pair.Value != null)
                        deleted.Removed.AddRange(pair.Value);
                    diffs.Add(deleted);
                }
            }

            diffs.Sort((a, b) => string.CompareOrdinal(a.AtlasName, b.AtlasName));
            return diffs;
        }
    }
}
