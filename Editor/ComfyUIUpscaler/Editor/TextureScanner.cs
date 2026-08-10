using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

namespace ComfyUIUpscaler.Editor
{
    internal static class TextureScanner
    {
        private static readonly HashSet<string> SupportedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

        public static List<TextureAssetInfo> Scan(string assetFolder, float expectedScale)
        {
            return Scan(new[] { assetFolder }, expectedScale);
        }

        public static List<TextureAssetInfo> Scan(IEnumerable<string> assetFolders, float expectedScale)
        {
            List<string> paths = CollectAssetPaths(assetFolders);
            var results = new List<TextureAssetInfo>(paths.Count);
            foreach (string path in paths)
                results.Add(Inspect(path, expectedScale));
            return results;
        }

        // 时间分片的增量扫描：图片解码与元数据读取仍在主线程执行（Unity API 限制），
        // 但每累计约 30ms 就 await 让出一次，使 Editor 能重绘、响应并支持取消，避免长时间冻结。
        public static async Task<List<TextureAssetInfo>> ScanAsync(
            IEnumerable<string> assetFolders,
            float expectedScale,
            Action<float, string> progress,
            CancellationToken cancellationToken)
        {
            List<string> paths = CollectAssetPaths(assetFolders);
            var results = new List<TextureAssetInfo>(paths.Count);
            var sliceWatch = Stopwatch.StartNew();
            for (int i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(Inspect(paths[i], expectedScale));

                bool last = i == paths.Count - 1;
                if (sliceWatch.ElapsedMilliseconds >= 30 || last)
                {
                    progress?.Invoke(paths.Count == 0 ? 1f : (float)(i + 1) / paths.Count,
                        $"扫描中 {i + 1}/{paths.Count}");
                    if (!last)
                    {
                        // 让出主线程一帧，Editor 得以重绘与处理事件
                        await Task.Yield();
                        sliceWatch.Restart();
                    }
                }
            }
            return results;
        }

        private static List<string> CollectAssetPaths(IEnumerable<string> assetFolders)
        {
            var requestedFolders = (assetFolders ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Replace((char)92, '/').TrimEnd('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (requestedFolders.Count == 0)
                throw new ArgumentException("请至少选择一个 Assets 下的有效目录。");
            foreach (string folder in requestedFolders)
            {
                if (!AssetDatabase.IsValidFolder(folder))
                    throw new ArgumentException("无效的 Assets 目录: " + folder);
            }

            List<string> folders = CollapseNestedFolders(requestedFolders);
            return folders
                .SelectMany(folder => Directory.EnumerateFiles(
                    AssetPathToFullPath(folder),
                    "*.*",
                    SearchOption.AllDirectories))
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .Select(FullPathToAssetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        internal static List<string> CollapseNestedFolders(IEnumerable<string> assetFolders)
        {
            var result = new List<string>();
            foreach (string folder in assetFolders
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Select(path => path.Replace((char)92, '/').TrimEnd('/'))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path.Length)
                         .ThenBy(path => path, StringComparer.Ordinal))
            {
                if (result.Any(parent => folder.StartsWith(
                        parent + "/",
                        StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Add(folder);
            }
            return result.OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static TextureAssetInfo Inspect(string assetPath, float expectedScale)
        {
            string fullPath = AssetPathToFullPath(assetPath);
            byte[] bytes = File.ReadAllBytes(fullPath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, false))
                    throw new InvalidDataException("无法解码图片: " + assetPath);

                Color32[] pixels = texture.GetPixels32();
                bool hasAlpha = Path.GetExtension(assetPath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    && pixels.Any(pixel => pixel.a < 255);
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null)
                    throw new InvalidOperationException("找不到 TextureImporter: " + assetPath);

                var info = new TextureAssetInfo
                {
                    assetPath = assetPath,
                    extension = Path.GetExtension(assetPath).ToLowerInvariant(),
                    width = texture.width,
                    height = texture.height,
                    hasAlpha = hasAlpha,
                    textureType = importer.textureType.ToString(),
                    spriteMode = importer.textureType == TextureImporterType.Sprite
                        ? importer.spriteImportMode.ToString()
                        : "N/A",
                    maxTextureSize = importer.maxTextureSize,
                    singleBorder = importer.spriteBorder,
                    guid = AssetDatabase.AssetPathToGUID(assetPath),
                    contentSha256 = UpgradeHashUtility.ComputeSha256(bytes)
                };

                bool isColor = IsColorTexture(importer.textureType);
                info.selected = isColor;
                var warnings = new List<string>();
                if (!isColor)
                    warnings.Add("非颜色纹理，默认跳过");
                if ((double)info.width * expectedScale > importer.maxTextureSize ||
                    (double)info.height * expectedScale > importer.maxTextureSize)
                    warnings.Add("预计输出超过 Max Size " + importer.maxTextureSize);

                if (importer.textureType == TextureImporterType.Sprite)
                {
                    CaptureSpriteMetadata(importer, info);
                    if (importer.spriteImportMode == SpriteImportMode.Single && HasBorder(info.singleBorder))
                        warnings.Add("Single 九宫格 Border 将按倍率缩放");
                    if (importer.spriteImportMode == SpriteImportMode.Multiple &&
                        info.sprites.Any(sprite => HasBorder(sprite.border)))
                        warnings.Add("Multiple Rect/Border 将按倍率缩放");
                }

                CaptureReferenceSnapshot(info);
                info.warning = string.Join("；", warnings);
                return info;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void CaptureSpriteMetadata(TextureImporter importer, TextureAssetInfo info)
        {
            if (importer.spriteImportMode != SpriteImportMode.Multiple)
                return;

            var factories = new SpriteDataProviderFactories();
            factories.Init();
            ISpriteEditorDataProvider provider = factories.GetSpriteEditorDataProviderFromObject(importer);
            if (provider == null)
                throw new InvalidOperationException("无法取得 Sprite Data Provider: " + info.assetPath);
            provider.InitSpriteEditorDataProvider();

            foreach (SpriteRect sprite in provider.GetSpriteRects())
            {
                info.sprites.Add(new SpriteMetadata
                {
                    name = sprite.name,
                    spriteId = sprite.spriteID.ToString(),
                    rect = sprite.rect,
                    border = sprite.border,
                    pivot = sprite.pivot,
                    alignment = (int)sprite.alignment
                });
            }
        }

        private static void CaptureReferenceSnapshot(TextureAssetInfo info)
        {
            foreach (Sprite sprite in AssetDatabase.LoadAllAssetsAtPath(info.assetPath).OfType<Sprite>())
            {
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long fileId))
                    throw new InvalidOperationException("无法读取 Sprite GUID/local fileID: " + info.assetPath);
                info.references.Add(new AssetReferenceSnapshot
                {
                    name = sprite.name,
                    guid = guid,
                    localFileId = fileId
                });
            }
        }

        private static bool IsColorTexture(TextureImporterType type)
        {
            return type == TextureImporterType.Default ||
                   type == TextureImporterType.Sprite ||
                   type == TextureImporterType.GUI ||
                   type == TextureImporterType.Cursor;
        }

        private static bool HasBorder(Vector4 border)
        {
            return border.x != 0f || border.y != 0f || border.z != 0f || border.w != 0f;
        }

        internal static string AssetPathToFullPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        internal static string FullPathToAssetPath(string fullPath)
        {
            string normalizedDataPath = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalized = Path.GetFullPath(fullPath);
            if (!normalized.StartsWith(normalizedDataPath + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("路径不在 Assets 下: " + fullPath);
            return "Assets" + normalized.Substring(normalizedDataPath.Length).Replace('\\', '/');
        }
    }
}
