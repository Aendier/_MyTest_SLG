using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace SpriteAtlasAutoOrganizer.Editor
{
    internal static class AtlasGenerator
    {
        public static GenerateResult Generate(
            AnalysisResult analysis,
            SpriteAtlasAutoOrganizerConfig config)
        {
            var result = new GenerateResult();
            if (analysis == null)
            {
                result.Success = false;
                result.Error = "Analysis result is null.";
                return result;
            }

            string output = config != null ? config.outputPath : null;
            if (string.IsNullOrEmpty(output))
            {
                result.Success = false;
                result.Error = "outputPath is empty.";
                return result;
            }

            if (!AtlasOrganizer.IsAllowedWritePath(output))
            {
                result.Success = false;
                result.Error = "测试阶段禁止写入正式资源目录。outputPath 必须在 " +
                               AtlasOrganizer.AllowedWriteRoot + " 下。";
                return result;
            }

            EnsureFolder(output);

            var plannedNames = new HashSet<string>();
            var changedAtlases = new List<SpriteAtlas>();
            for (int i = 0; i < analysis.Clusters.Count; i++)
            {
                AtlasCluster cluster = analysis.Clusters[i];
                plannedNames.Add(cluster.StableName);
                string atlasPath = output.TrimEnd('/', '\\') + "/" + cluster.StableName + ".spriteatlas";
                if (!cluster.Changed && File.Exists(atlasPath))
                    continue;

                SpriteAtlas atlas = LoadOrCreate(atlasPath, config);
                ApplyPackables(atlas, cluster, analysis.Sprites);
                EditorUtility.SetDirty(atlas);
                result.WrittenAtlasPaths.Add(atlasPath);
                changedAtlases.Add(atlas);
            }

            string[] existing = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { output });
            for (int i = 0; i < existing.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(existing[i]);
                string name = Path.GetFileNameWithoutExtension(path);
                if (plannedNames.Contains(name))
                    continue;

                AssetDatabase.DeleteAsset(path);
                result.DeletedAtlasPaths.Add(path);
            }

            AssetDatabase.SaveAssets();
            if (changedAtlases.Count > 0)
            {
                SpriteAtlasUtility.PackAtlases(
                    changedAtlases.ToArray(),
                    EditorUserBuildSettings.activeBuildTarget);
                for (int i = 0; i < result.WrittenAtlasPaths.Count; i++)
                    result.PackedAtlasPaths.Add(result.WrittenAtlasPaths[i]);
            }

            return result;
        }

        private static SpriteAtlas LoadOrCreate(string path, SpriteAtlasAutoOrganizerConfig config)
        {
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null)
            {
                atlas = new SpriteAtlas();
                AssetDatabase.CreateAsset(atlas, path);
            }

            int maxSize = config != null ? config.maxAtlasSize : 2048;
            int padding = config != null ? config.packPadding : 4;
            atlas.SetPackingSettings(new SpriteAtlasPackingSettings
            {
                blockOffset = 1,
                enableRotation = false,
                enableTightPacking = false,
                padding = padding
            });
            atlas.SetTextureSettings(new SpriteAtlasTextureSettings
            {
                readable = false,
                generateMipMaps = false,
                sRGB = true,
                filterMode = FilterMode.Bilinear
            });
            atlas.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "DefaultTexturePlatform",
                overridden = false,
                maxTextureSize = maxSize,
                format = TextureImporterFormat.Automatic,
                textureCompression = TextureImporterCompression.Compressed
            });
            return atlas;
        }

        private static void ApplyPackables(
            SpriteAtlas atlas,
            AtlasCluster cluster,
            IDictionary<SpriteKey, SpriteRecord> sprites)
        {
            Object[] current = atlas.GetPackables();
            if (current != null && current.Length > 0)
                atlas.Remove(current);

            var objects = new List<Object>();
            for (int i = 0; i < cluster.Sprites.Count; i++)
            {
                SpriteRecord record;
                if (!sprites.TryGetValue(cluster.Sprites[i], out record))
                    continue;

                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(record.AssetPath);
                if (assets == null)
                    continue;

                for (int j = 0; j < assets.Length; j++)
                {
                    var sprite = assets[j] as Sprite;
                    if (sprite == null)
                        continue;

                    string guid;
                    long fileId;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out guid, out fileId))
                        continue;
                    if (guid != record.Key.Guid || fileId != record.Key.FileId)
                        continue;

                    objects.Add(sprite);
                    break;
                }
            }

            if (objects.Count > 0)
                atlas.Add(objects.ToArray());
        }

        private static void EnsureFolder(string assetFolder)
        {
            string normalized = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(normalized))
                return;

            string[] parts = normalized.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
