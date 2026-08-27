using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace SpriteAtlasAutoOrganizer.Editor
{
    internal static class AtlasValidator
    {
        public static List<ValidationIssue> Validate(
            SpriteAtlasAutoOrganizerConfig config,
            AnalysisResult analysis)
        {
            var issues = new List<ValidationIssue>();
            string output = config != null ? config.outputPath : null;
            if (string.IsNullOrEmpty(output) || !AssetDatabase.IsValidFolder(output))
                return issues;

            string[] guids = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { output });
            var spriteOwners = new Dictionary<SpriteKey, string>();
            int maxSize = config != null ? config.maxAtlasSize : 2048;
            bool allowCross = config != null && config.allowCrossPackage;
            bool allowShared = config != null && config.allowSharedSprite;

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                if (atlas == null)
                    continue;

                string atlasName = Path.GetFileNameWithoutExtension(path);
                Object[] packables = atlas.GetPackables();
                if (packables == null)
                    continue;

                string domain = null;
                for (int j = 0; j < packables.Length; j++)
                {
                    Object packable = packables[j];
                    if (packable == null)
                        continue;

                    string packablePath = AssetDatabase.GetAssetPath(packable);
                    if (AssetDatabase.IsValidFolder(packablePath))
                    {
                        issues.Add(Error(
                            "[AtlasOrganizer] Atlas " + atlasName +
                            " contains Folder Packable.\nAuto-generated atlas must only contain Sprite.",
                            path));
                        continue;
                    }

                    var sprite = packable as Sprite;
                    if (sprite == null)
                    {
                        issues.Add(Error(
                            "[AtlasOrganizer] Atlas " + atlasName +
                            " contains non-Sprite Packable: " + packable.name,
                            path));
                        continue;
                    }

                    string guid;
                    long fileId;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out guid, out fileId))
                        continue;

                    var key = new SpriteKey(guid, fileId);
                    string existed;
                    if (spriteOwners.TryGetValue(key, out existed) && existed != path)
                    {
                        if (!allowShared)
                        {
                            issues.Add(Error(
                                "Duplicate Sprite Atlas Ownership: " + sprite.name +
                                " in " + existed + " and " + path,
                                path));
                        }
                    }
                    else
                    {
                        spriteOwners[key] = path;
                    }

                    if (!allowCross && analysis != null)
                    {
                        SpriteRecord record;
                        if (analysis.Sprites.TryGetValue(key, out record))
                        {
                            if (domain == null)
                                domain = record.Domain;
                            else if (!string.Equals(domain, record.Domain, System.StringComparison.Ordinal))
                            {
                                issues.Add(Error(
                                    "Atlas Cross Package Conflict: " + atlasName +
                                    " mixes " + domain + " and " + record.Domain,
                                    path));
                            }
                        }
                    }
                }

                Texture2D[] previews = TryGetPreviewTextures(atlas);
                if (previews != null)
                {
                    for (int p = 0; p < previews.Length; p++)
                    {
                        Texture2D preview = previews[p];
                        if (preview == null)
                            continue;
                        if (preview.width > maxSize || preview.height > maxSize)
                        {
                            issues.Add(Error(
                                "Atlas Overflow: " + atlasName + " packed " +
                                preview.width + "x" + preview.height +
                                " exceeds " + maxSize,
                                path));
                        }
                    }
                }
            }

            return issues;
        }

        private static Texture2D[] TryGetPreviewTextures(SpriteAtlas atlas)
        {
            if (atlas == null)
                return null;

            MethodInfo method = typeof(SpriteAtlas).GetMethod(
                "GetPreviewTextures",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                return null;

            return method.Invoke(atlas, null) as Texture2D[];
        }

        private static ValidationIssue Error(string message, string path)
        {
            return new ValidationIssue
            {
                IsError = true,
                Message = message,
                AssetPath = path
            };
        }
    }
}
