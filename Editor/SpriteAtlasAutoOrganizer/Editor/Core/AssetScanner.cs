using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace SpriteAtlasAutoOrganizer.Editor
{
    internal sealed class ScanResult
    {
        public readonly List<string> PrefabPaths = new List<string>();
        public readonly List<string> ScenePaths = new List<string>();
        public readonly List<string> AtlasPaths = new List<string>();
        public readonly List<string> TexturePaths = new List<string>();
    }

    /// <summary>
    /// 扫描 Prefab / Scene / SpriteAtlas / Sprite。不读代码字符串引用。
    /// </summary>
    internal static class AssetScanner
    {
        public static ScanResult Scan(SpriteAtlasAutoOrganizerConfig config)
        {
            var result = new ScanResult();
            string[] roots = GetExistingRoots(config);
            if (roots.Length == 0)
                return result;

            Collect(result.PrefabPaths, "t:Prefab", roots);
            Collect(result.ScenePaths, "t:Scene", roots);
            Collect(result.AtlasPaths, "t:SpriteAtlas", roots);
            Collect(result.TexturePaths, "t:Texture2D", roots);
            return result;
        }

        public static Dictionary<SpriteKey, SpriteRecord> CollectSprites(
            IEnumerable<string> texturePaths,
            SpriteAtlasAutoOrganizerConfig config)
        {
            var sprites = new Dictionary<SpriteKey, SpriteRecord>();
            if (texturePaths == null)
                return sprites;

            int padding = config != null ? config.packPadding : 4;
            float slack = config != null ? config.packingSlack : 1.15f;
            int index = 0;
            foreach (string path in texturePaths)
            {
                index++;
                if ((index & 127) == 0)
                {
                    OrganizerProgress.Report("读取 Sprite 信息（不加载贴图像素） " + index, 0.08f);
                    OrganizerProgress.ThrowIfCanceled();
                }

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.textureType != TextureImporterType.Sprite)
                    continue;

                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                    continue;

                int width;
                int height;
                ReadImporterSize(importer, out width, out height);

                if (importer.spriteImportMode == SpriteImportMode.Multiple)
                {
                    SpriteMetaData[] sheet = importer.spritesheet;
                    if (sheet == null || sheet.Length == 0)
                        continue;

                    for (int i = 0; i < sheet.Length; i++)
                    {
                        int sw = Mathf.Max(1, Mathf.RoundToInt(sheet[i].rect.width));
                        int sh = Mathf.Max(1, Mathf.RoundToInt(sheet[i].rect.height));
                        // Multiple 子图 fileID 不稳定读取时先用序号占位，分析阶段靠 guid 回退匹配。
                        var key = new SpriteKey(guid, 21300000L + i);
                        AddSprite(sprites, key, path, sheet[i].name, sw, sh, padding, slack);
                    }

                    continue;
                }

                AddSprite(
                    sprites,
                    new SpriteKey(guid, 21300000L),
                    path,
                    Path.GetFileNameWithoutExtension(path),
                    width,
                    height,
                    padding,
                    slack);
            }

            return sprites;
        }

        private static void AddSprite(
            Dictionary<SpriteKey, SpriteRecord> sprites,
            SpriteKey key,
            string path,
            string name,
            int width,
            int height,
            int padding,
            float slack)
        {
            if (sprites.ContainsKey(key))
                return;

            sprites[key] = new SpriteRecord
            {
                Key = key,
                AssetPath = path,
                Name = name,
                Width = Mathf.Max(1, width),
                Height = Mathf.Max(1, height),
                EstimatedArea = SpriteSizeEstimator.EstimateSpriteArea(
                    width,
                    height,
                    padding,
                    slack)
            };
        }

        private static MethodInfo _getWidthAndHeight;

        /// <summary>
        /// 只读 importer 源尺寸，禁止 LoadAsset 贴图，否则几千张 UI 会把编辑器撑爆。
        /// </summary>
        private static void ReadImporterSize(TextureImporter importer, out int width, out int height)
        {
            width = 64;
            height = 64;
            if (importer == null)
                return;

            if (_getWidthAndHeight == null)
            {
                _getWidthAndHeight = typeof(TextureImporter).GetMethod(
                    "GetWidthAndHeight",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_getWidthAndHeight == null)
                return;

            object[] args = { 0, 0 };
            _getWidthAndHeight.Invoke(importer, args);
            width = Mathf.Max(1, (int)args[0]);
            height = Mathf.Max(1, (int)args[1]);
        }

        public static HashSet<SpriteKey> CollectManualAtlasSprites(
            IEnumerable<string> atlasPaths,
            SpriteAtlasAutoOrganizerConfig config)
        {
            // 测试阶段不加载正式 Atlas / 不展开 Folder Packable，避免把图集里的图再次全部载入内存。
            return new HashSet<SpriteKey>();
        }

        public static Dictionary<string, HashSet<string>> CollectExistingAutoAtlases(
            SpriteAtlasAutoOrganizerConfig config)
        {
            var map = new Dictionary<string, HashSet<string>>();
            string output = config != null ? config.outputPath : null;
            if (string.IsNullOrEmpty(output) || !AssetDatabase.IsValidFolder(output))
                return map;

            string[] guids = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { output });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                if (atlas == null)
                    continue;

                string name = Path.GetFileNameWithoutExtension(path);
                var tokens = new HashSet<string>();
                CollectPackableSprites(atlas.GetPackables(), tokens, false);
                map[name] = tokens;
            }

            return map;
        }

        private static void CollectPackableSprites(Object[] packables, HashSet<SpriteKey> keys)
        {
            var tokens = new HashSet<string>();
            CollectPackableSprites(packables, tokens, true);
            foreach (string token in tokens)
            {
                SpriteKey key;
                if (SpriteKey.TryParse(token, out key))
                    keys.Add(key);
            }
        }

        private static void CollectPackableSprites(
            Object[] packables,
            HashSet<string> tokens,
            bool expandFolder)
        {
            if (packables == null)
                return;

            for (int i = 0; i < packables.Length; i++)
            {
                Object packable = packables[i];
                if (packable == null)
                    continue;

                string path = AssetDatabase.GetAssetPath(packable);
                if (AssetDatabase.IsValidFolder(path))
                {
                    if (!expandFolder)
                        continue;

                    string[] spriteGuids = AssetDatabase.FindAssets("t:Sprite", new[] { path });
                    for (int s = 0; s < spriteGuids.Length; s++)
                    {
                        string spritePath = AssetDatabase.GUIDToAssetPath(spriteGuids[s]);
                        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(spritePath);
                        AddSpriteTokens(assets, tokens);
                    }

                    continue;
                }

                AddSpriteTokens(new[] { packable }, tokens);
            }
        }

        private static void AddSpriteTokens(Object[] assets, HashSet<string> tokens)
        {
            if (assets == null)
                return;

            for (int i = 0; i < assets.Length; i++)
            {
                var sprite = assets[i] as Sprite;
                if (sprite == null)
                    continue;

                string guid;
                long fileId;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out guid, out fileId))
                    continue;

                tokens.Add(new SpriteKey(guid, fileId).Token);
            }
        }

        private static void Collect(List<string> output, string filter, string[] roots)
        {
            string[] guids = AssetDatabase.FindAssets(filter, roots);
            var seen = new HashSet<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) || !seen.Add(path))
                    continue;
                output.Add(path);
            }
        }

        private static string[] GetExistingRoots(SpriteAtlasAutoOrganizerConfig config)
        {
            var roots = new List<string>();
            if (config == null || config.scanRoots == null)
                return roots.ToArray();

            for (int i = 0; i < config.scanRoots.Length; i++)
            {
                string root = config.scanRoots[i];
                if (!string.IsNullOrEmpty(root) && AssetDatabase.IsValidFolder(root))
                    roots.Add(root);
            }

            return roots.ToArray();
        }

        private static string Normalize(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');
        }

        private static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
                return false;
            return path.Equals(root, System.StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
