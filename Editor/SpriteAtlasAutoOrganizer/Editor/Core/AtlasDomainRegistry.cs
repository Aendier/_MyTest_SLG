using System;
using System.Collections.Generic;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// Domain 解析链：配置前缀 → 外部解析器（YooAsset）→ Default。
    /// </summary>
    internal static class AtlasDomainRegistry
    {
        private static readonly List<IAtlasDomainResolver> Resolvers =
            new List<IAtlasDomainResolver>();

        public static void Register(IAtlasDomainResolver resolver)
        {
            if (resolver == null)
                return;

            for (int i = 0; i < Resolvers.Count; i++)
            {
                if (Resolvers[i].GetType() == resolver.GetType())
                    return;
            }

            Resolvers.Add(resolver);
        }

        public static string Resolve(
            SpriteAtlasAutoOrganizerConfig config,
            string assetPath,
            string assetGuid)
        {
            if (config != null && config.domainPathRules != null)
            {
                string matched = null;
                int bestLength = -1;
                for (int i = 0; i < config.domainPathRules.Length; i++)
                {
                    DomainPathRule rule = config.domainPathRules[i];
                    if (rule == null ||
                        string.IsNullOrEmpty(rule.pathPrefix) ||
                        string.IsNullOrEmpty(rule.domain) ||
                        string.IsNullOrEmpty(assetPath))
                        continue;

                    if (!assetPath.StartsWith(rule.pathPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (rule.pathPrefix.Length > bestLength)
                    {
                        bestLength = rule.pathPrefix.Length;
                        matched = rule.domain;
                    }
                }

                if (!string.IsNullOrEmpty(matched))
                    return matched;
            }

            for (int i = 0; i < Resolvers.Count; i++)
            {
                string domain = Resolvers[i].ResolveDomain(assetPath, assetGuid);
                if (!string.IsNullOrEmpty(domain))
                    return domain;
            }

            return "Default";
        }
    }
}
