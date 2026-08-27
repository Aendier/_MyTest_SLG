using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// 自动图集规划入口：Analyze 只计算，Generate 才写 .spriteatlas。
    /// </summary>
    public static class AtlasOrganizer
    {
        public const string DefaultConfigPath =
            "Assets/_MyTest_SLG/Editor/SpriteAtlasAutoOrganizer/SpriteAtlasAutoOrganizerConfig.asset";

        public const string AllowedWriteRoot = "Assets/_MyTest_SLG";

        public const string DefaultTestOutputPath =
            "Assets/_MyTest_SLG/Editor/SpriteAtlasAutoOrganizer/TestOutput";

        public static bool IsAllowedWritePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string normalized = path.Replace('\\', '/').TrimEnd('/');
            return normalized.Equals(AllowedWriteRoot, System.StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(AllowedWriteRoot + "/", System.StringComparison.OrdinalIgnoreCase);
        }

        public static SpriteAtlasAutoOrganizerConfig LoadOrCreateConfig()
        {
            var config = AssetDatabase.LoadAssetAtPath<SpriteAtlasAutoOrganizerConfig>(DefaultConfigPath);
            if (config != null)
                return config;

            config = UnityEngine.ScriptableObject.CreateInstance<SpriteAtlasAutoOrganizerConfig>();
            string folder = "Assets/_MyTest_SLG/Editor/SpriteAtlasAutoOrganizer";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets/_MyTest_SLG/Editor", "SpriteAtlasAutoOrganizer");
            AssetDatabase.CreateAsset(config, DefaultConfigPath);
            AssetDatabase.SaveAssets();
            return config;
        }

        public static AnalysisResult Analyze(SpriteAtlasAutoOrganizerConfig config)
        {
            if (config == null)
                throw new System.ArgumentNullException("config");

            var analysis = new AnalysisResult
            {
                Incremental = config.incremental
            };

            OrganizerProgress.Reset();
            try
            {
                OrganizerProgress.Report("扫描资源路径", 0.02f);
                OrganizerProgress.ThrowIfCanceled();
                ScanResult scan = AssetScanner.Scan(config);
                Dictionary<SpriteKey, SpriteRecord> sprites =
                    AssetScanner.CollectSprites(scan.TexturePaths, config);
                HashSet<SpriteKey> manualSprites =
                    AssetScanner.CollectManualAtlasSprites(scan.AtlasPaths, config);

                var lookup = new Dictionary<string, SpriteKey>();
                var neverShare = ResolveNeverShare(config, sprites);
                int spriteIndex = 0;
                foreach (KeyValuePair<SpriteKey, SpriteRecord> pair in sprites)
                {
                    spriteIndex++;
                    if ((spriteIndex & 255) == 0)
                    {
                        OrganizerProgress.Report("解析 Domain " + spriteIndex, 0.2f);
                        OrganizerProgress.ThrowIfCanceled();
                    }

                    SpriteYamlReferenceParser.AddLookup(lookup, pair.Key);
                    pair.Value.InManualAtlas = manualSprites.Contains(pair.Key);
                    pair.Value.NeverShare = neverShare.Contains(pair.Key);
                    pair.Value.Domain = AtlasDomainRegistry.Resolve(
                        config,
                        pair.Value.AssetPath,
                        pair.Key.Guid);
                    analysis.Sprites[pair.Key] = pair.Value;
                }

                OrganizerCacheRoot cacheRoot = config.incremental
                    ? OrganizerCache.Load(config)
                    : new OrganizerCacheRoot();
                Dictionary<string, HostCacheEntry> hostCache =
                    OrganizerCache.IndexHosts(cacheRoot);

                List<HostRecord> hosts = ReferenceAnalyzer.AnalyzeHosts(
                    scan,
                    lookup,
                    hostCache,
                    config.incremental);

                int skipped = 0;
                for (int i = 0; i < hosts.Count; i++)
                {
                    HostRecord host = hosts[i];
                    HostCacheEntry cached;
                    if (config.incremental &&
                        hostCache.TryGetValue(host.Guid, out cached) &&
                        cached.dependencyHash == host.DependencyHash)
                    {
                        skipped++;
                    }

                    Dictionary<string, HashSet<SpriteKey>> forward = host.IsScene
                        ? analysis.SceneToSprites
                        : analysis.PrefabToSprites;
                    HashSet<SpriteKey> set;
                    if (!forward.TryGetValue(host.Guid, out set))
                    {
                        set = new HashSet<SpriteKey>();
                        forward[host.Guid] = set;
                    }

                    for (int s = 0; s < host.Sprites.Count; s++)
                    {
                        SpriteKey key = host.Sprites[s];
                        if (!analysis.Sprites.ContainsKey(key))
                            continue;

                        set.Add(key);
                        if (!host.IsScene)
                        {
                            HashSet<string> reverse;
                            if (!analysis.SpriteToPrefabs.TryGetValue(key, out reverse))
                            {
                                reverse = new HashSet<string>();
                                analysis.SpriteToPrefabs[key] = reverse;
                            }

                            reverse.Add(host.Guid);
                        }
                    }
                }

                OrganizerProgress.Report("聚类", 0.75f);
                OrganizerProgress.ThrowIfCanceled();
                analysis.Clusters.AddRange(AtlasPlanner.Plan(analysis, config));

                Dictionary<string, HashSet<string>> existing =
                    AssetScanner.CollectExistingAutoAtlases(config);
                analysis.Diffs.AddRange(AtlasDiffBuilder.Build(analysis.Clusters, existing));

                analysis.Stats.SpriteCount = analysis.Sprites.Count;
                analysis.Stats.PrefabCount = analysis.PrefabToSprites.Count;
                analysis.Stats.SceneCount = analysis.SceneToSprites.Count;
                analysis.Stats.ClusterCount = analysis.Clusters.Count;
                analysis.Stats.ChangedAtlasCount = analysis.Diffs.Count;
                analysis.Stats.SkippedHostCount = skipped;
                return analysis;
            }
            catch (System.OperationCanceledException)
            {
                analysis.Stats.SpriteCount = analysis.Sprites.Count;
                analysis.Stats.PrefabCount = analysis.PrefabToSprites.Count;
                analysis.Stats.SceneCount = analysis.SceneToSprites.Count;
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public static GenerateResult Generate(
            SpriteAtlasAutoOrganizerConfig config,
            AnalysisResult analysis)
        {
            if (analysis == null)
                analysis = Analyze(config);

            GenerateResult result = AtlasGenerator.Generate(analysis, config);
            if (!result.Success)
                return result;

            if (config != null && config.validateOnGenerate)
            {
                result.Issues.AddRange(AtlasValidator.Validate(config, analysis));
                if (HasError(result.Issues))
                {
                    result.Success = false;
                    result.Error = "Validate() == Error";
                }
            }

            SaveCache(config, analysis);
            return result;
        }

        public static List<ValidationIssue> Validate(
            SpriteAtlasAutoOrganizerConfig config,
            AnalysisResult analysis)
        {
            return AtlasValidator.Validate(config, analysis);
        }

        public static bool HasError(IList<ValidationIssue> issues)
        {
            if (issues == null)
                return false;
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i] != null && issues[i].IsError)
                    return true;
            }

            return false;
        }

        private static HashSet<SpriteKey> ResolveNeverShare(
            SpriteAtlasAutoOrganizerConfig config,
            Dictionary<SpriteKey, SpriteRecord> sprites)
        {
            var set = new HashSet<SpriteKey>();
            if (config == null || config.neverShareSprites == null)
                return set;

            for (int i = 0; i < config.neverShareSprites.Length; i++)
            {
                SpriteKey? key = AtlasPlanner.ResolveSpriteToken(config.neverShareSprites[i], sprites);
                if (key != null)
                    set.Add(key.Value);
            }

            return set;
        }

        private static void SaveCache(SpriteAtlasAutoOrganizerConfig config, AnalysisResult analysis)
        {
            var root = new OrganizerCacheRoot();
            AppendHosts(root, analysis.PrefabToSprites, false);
            AppendHosts(root, analysis.SceneToSprites, true);
            for (int i = 0; i < analysis.Clusters.Count; i++)
            {
                AtlasCluster cluster = analysis.Clusters[i];
                root.atlases.Add(new AtlasCacheEntry
                {
                    name = cluster.StableName,
                    path = (config.outputPath ?? string.Empty).TrimEnd('/', '\\') +
                           "/" + cluster.StableName + ".spriteatlas",
                    contentHash = AtlasNamer.ComputeContentHash(cluster.Sprites)
                });
            }

            OrganizerCache.Save(config, root);
        }

        private static void AppendHosts(
            OrganizerCacheRoot root,
            Dictionary<string, HashSet<SpriteKey>> map,
            bool isScene)
        {
            foreach (KeyValuePair<string, HashSet<SpriteKey>> pair in map)
            {
                var entry = new HostCacheEntry
                {
                    guid = pair.Key,
                    path = AssetDatabase.GUIDToAssetPath(pair.Key),
                    isScene = isScene,
                    spriteHash = AtlasNamer.ComputeHostHash(pair.Value),
                    dependencyHash = FileStampForCache(AssetDatabase.GUIDToAssetPath(pair.Key))
                };
                if (pair.Value != null)
                {
                    foreach (SpriteKey key in pair.Value)
                        entry.spriteTokens.Add(key.Token);
                }

                root.hosts.Add(entry);
            }
        }

        private static string FileStampForCache(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return string.Empty;

            var info = new FileInfo(path);
            return info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }
    }
}
