using System;
using System.Collections.Generic;
using SpriteAtlasAutoOrganizer.Editor;
using UnityEditor;
using YooAsset.Editor;

namespace SpriteAtlasAutoOrganizer.YooAsset.Editor
{
    /// <summary>
    /// 用 YooAsset CollectPath 最长前缀匹配 Package，作为 Atlas Domain。
    /// </summary>
    internal sealed class YooAssetCollectorDomainResolver : IAtlasDomainResolver
    {
        public string ResolveDomain(string assetPath, string assetGuid)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            AssetBundleCollectorSetting setting = AssetBundleCollectorSettingData.Setting;
            if (setting == null || setting.Packages == null)
                return null;

            EnsureCache(setting);

            string bestDomain = null;
            int bestLength = -1;
            for (int i = 0; i < _collectors.Count; i++)
            {
                CollectorPath cached = _collectors[i];
                if (!IsUnder(assetPath, cached.Path))
                    continue;
                if (cached.Path.Length <= bestLength)
                    continue;

                bestLength = cached.Path.Length;
                bestDomain = cached.Package;
            }

            return bestDomain;
        }

        private struct CollectorPath
        {
            public string Path;
            public string Package;
        }

        private static readonly List<CollectorPath> _collectors = new List<CollectorPath>();
        private static bool _cacheReady;

        private static void EnsureCache(AssetBundleCollectorSetting setting)
        {
            if (_cacheReady)
                return;

            _collectors.Clear();
            for (int p = 0; p < setting.Packages.Count; p++)
            {
                AssetBundleCollectorPackage package = setting.Packages[p];
                if (package == null || package.Groups == null)
                    continue;

                for (int g = 0; g < package.Groups.Count; g++)
                {
                    AssetBundleCollectorGroup group = package.Groups[g];
                    if (group == null || group.Collectors == null)
                        continue;

                    for (int c = 0; c < group.Collectors.Count; c++)
                    {
                        AssetBundleCollector collector = group.Collectors[c];
                        if (collector == null || string.IsNullOrEmpty(collector.CollectPath))
                            continue;

                        _collectors.Add(new CollectorPath
                        {
                            Path = collector.CollectPath.Replace('\\', '/').TrimEnd('/'),
                            Package = package.PackageName
                        });
                    }
                }
            }

            _cacheReady = true;
        }

        private static bool IsUnder(string path, string root)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }
    }

    [InitializeOnLoad]
    internal static class YooAssetDomainResolverBootstrap
    {
        static YooAssetDomainResolverBootstrap()
        {
            AtlasDomainRegistry.Register(new YooAssetCollectorDomainResolver());
        }
    }
}
