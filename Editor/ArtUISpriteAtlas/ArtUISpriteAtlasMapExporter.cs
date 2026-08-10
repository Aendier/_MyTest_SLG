using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D;

namespace UIR.EditorTools
{
    /// <summary>
    /// Art UI 图集映射导出（放在 _MyTest_SLG，不改主工程 Analyzer）。
    /// 迁移期同时扫描 AB / ABNew / Art，写出统一的 sprite_atlas_map.json。
    /// </summary>
    public static class ArtUISpriteAtlasMapExporter
    {
        /// <summary>与运行时约定一致的 map 输出路径</summary>
        public const string OutputPath = "Assets/GameAssets/AB/Config/AtlasConfig/sprite_atlas_map.json";

        /// <summary>图集与源图扫描根目录（新旧并存）</summary>
        private static readonly string[] SearchPaths =
        {
            "Assets/GameAssets/ABNew/SpriteAtlas",
            "Assets/GameAssets/AB/SpriteAtlas",
            "Assets/GameAssets/Art/UI/SpriteAtlas",
        };

        /// <summary>
        /// 扫描全部搜索路径，按 packables 建立 sprite→atlas 映射并写出 JSON。
        /// </summary>
        /// <returns>是否成功写出</returns>
        public static bool ExportMap()
        {
            var spriteInfos = new Dictionary<string, MapSpriteInfo>();

            string[] atlasGuids = AssetDatabase.FindAssets("t:SpriteAtlas", SearchPaths);
            string[] spriteGuids = AssetDatabase.FindAssets("t:Sprite", SearchPaths);

            // 先登记所有精灵，默认无图集
            foreach (string guid in spriteGuids)
            {
                string spritePath = AssetDatabase.GUIDToAssetPath(guid);
                string spriteName = Path.GetFileNameWithoutExtension(spritePath);
                var importer = AssetImporter.GetAtPath(spritePath) as TextureImporter;
                if (importer == null)
                {
                    continue;
                }

                spriteInfos[spriteName] = new MapSpriteInfo
                {
                    AtlasName = null,
                    MaxTextureSize = importer.maxTextureSize,
                    Format = importer.textureCompression
                };
            }

            try
            {
                for (int i = 0; i < atlasGuids.Length; i++)
                {
                    string atlasPath = AssetDatabase.GUIDToAssetPath(atlasGuids[i]);
                    SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                    if (atlas == null)
                    {
                        continue;
                    }

                    string atlasName = Path.GetFileNameWithoutExtension(atlasPath);
                    HashSet<string> packableSpriteNames = SpriteAtlasExtensions2.GetPackableSpriteNames(atlas);
                    foreach (string spriteName in packableSpriteNames)
                    {
                        if (spriteInfos.TryGetValue(spriteName, out MapSpriteInfo info))
                        {
                            info.AtlasName = atlasName;
                        }
                    }

                    EditorUtility.DisplayProgressBar(
                        "导出 sprite_atlas_map",
                        $"正在分析图集 {atlasName}...",
                        atlasGuids.Length > 0 ? (float)(i + 1) / atlasGuids.Length : 1f);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var jsonBuilder = new StringBuilder();
            jsonBuilder.AppendLine("{");

            bool isFirst = true;
            foreach (var kvp in spriteInfos)
            {
                if (!isFirst)
                {
                    jsonBuilder.AppendLine(",");
                }

                isFirst = false;
                jsonBuilder.AppendLine($"  \"{kvp.Key}\": {{");
                jsonBuilder.AppendLine(
                    $"    \"AtlasName\": {(kvp.Value.AtlasName == null ? "null" : $"\"{kvp.Value.AtlasName}\"")},");
                jsonBuilder.AppendLine($"    \"MaxTextureSize\": {kvp.Value.MaxTextureSize},");
                jsonBuilder.AppendLine($"    \"Format\": \"{kvp.Value.Format}\"");
                jsonBuilder.Append("  }");
            }

            jsonBuilder.AppendLine("\n}");

            string directory = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(OutputPath, jsonBuilder.ToString(), Encoding.UTF8);
            AssetDatabase.Refresh();
            Debug.Log($"[ArtUISpriteAtlas] map 已导出: {OutputPath}，共 {spriteInfos.Count} 条");
            return true;
        }

        /// <summary>map 条目（仅本工具使用，避免与主工程 SpriteInfo 冲突）</summary>
        private class MapSpriteInfo
        {
            public string AtlasName;
            public int MaxTextureSize;
            public TextureImporterCompression Format;
        }
    }
}
