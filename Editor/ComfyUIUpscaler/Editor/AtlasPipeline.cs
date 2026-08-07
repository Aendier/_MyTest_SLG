using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ComfyUIUpscaler.Editor
{
    internal static class AtlasPacker
    {
        private sealed class Shelf
        {
            public int y;
            public int height;
            public int nextX;
        }

        private sealed class PageState
        {
            public readonly AtlasPageManifest manifest = new AtlasPageManifest();
            public readonly List<Shelf> shelves = new List<Shelf>();
        }

        public static List<AtlasPageManifest> Pack(
            IEnumerable<TextureAssetInfo> assets,
            int padding,
            int maxEdge,
            long maxPixels)
        {
            if (padding < 0)
                throw new ArgumentOutOfRangeException(nameof(padding));
            if (maxEdge < 64)
                throw new ArgumentOutOfRangeException(nameof(maxEdge));
            if (maxPixels < 4096)
                throw new ArgumentOutOfRangeException(nameof(maxPixels));

            var sorted = assets
                .Where(asset => asset.selected)
                .OrderByDescending(asset => asset.height + padding * 2)
                .ThenByDescending(asset => asset.width + padding * 2)
                .ThenBy(asset => asset.assetPath, StringComparer.Ordinal)
                .ToList();

            var pages = new List<PageState>();
            foreach (TextureAssetInfo asset in sorted)
            {
                int outerWidth = checked(asset.width + padding * 2);
                int outerHeight = checked(asset.height + padding * 2);
                long outerPixels = (long)outerWidth * outerHeight;
                var exceededLimits = new List<string>();
                if (outerWidth > maxEdge)
                    exceededLimits.Add($"宽度 {outerWidth} > 最大边长 {maxEdge}");
                if (outerHeight > maxEdge)
                    exceededLimits.Add($"高度 {outerHeight} > 最大边长 {maxEdge}");
                if (outerPixels > maxPixels)
                    exceededLimits.Add($"像素数 {outerPixels} > 最大像素数 {maxPixels}");
                if (exceededLimits.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"图片 {asset.assetPath} 加 Padding 后为 {outerWidth}x{outerHeight}（{outerPixels} 像素），" +
                        $"超过单页限制：{string.Join("；", exceededLimits)}。当前限制：最大边长 {maxEdge}，最大像素数 {maxPixels}。");
                }

                bool placed = false;
                foreach (PageState page in pages)
                {
                    if (TryPlace(page, asset, outerWidth, outerHeight, padding, maxEdge, maxPixels))
                    {
                        placed = true;
                        break;
                    }
                }

                if (placed)
                    continue;

                var newPage = new PageState();
                newPage.manifest.pageIndex = pages.Count;
                pages.Add(newPage);
                if (!TryPlace(newPage, asset, outerWidth, outerHeight, padding, maxEdge, maxPixels))
                    throw new InvalidOperationException("无法将图片放入空图集页: " + asset.assetPath);
            }

            return pages.Select(page => page.manifest).ToList();
        }

        private static bool TryPlace(
            PageState page,
            TextureAssetInfo asset,
            int outerWidth,
            int outerHeight,
            int padding,
            int maxEdge,
            long maxPixels)
        {
            for (int i = 0; i < page.shelves.Count; i++)
            {
                Shelf shelf = page.shelves[i];
                if (outerHeight > shelf.height || shelf.nextX + outerWidth > maxEdge)
                    continue;

                int candidateWidth = Math.Max(page.manifest.width, shelf.nextX + outerWidth);
                int candidateHeight = page.manifest.height;
                if ((long)candidateWidth * candidateHeight > maxPixels)
                    continue;

                AddPlacement(page.manifest, asset, shelf.nextX, shelf.y, padding);
                shelf.nextX += outerWidth;
                page.manifest.width = candidateWidth;
                return true;
            }

            int y = page.manifest.height;
            int newWidth = Math.Max(page.manifest.width, outerWidth);
            int newHeight = y + outerHeight;
            if (newWidth > maxEdge || newHeight > maxEdge || (long)newWidth * newHeight > maxPixels)
                return false;

            page.shelves.Add(new Shelf { y = y, height = outerHeight, nextX = outerWidth });
            AddPlacement(page.manifest, asset, 0, y, padding);
            page.manifest.width = newWidth;
            page.manifest.height = newHeight;
            return true;
        }

        private static void AddPlacement(
            AtlasPageManifest page,
            TextureAssetInfo asset,
            int outerX,
            int outerY,
            int padding)
        {
            page.placements.Add(new AtlasPlacement
            {
                assetPath = asset.assetPath,
                pageIndex = page.pageIndex,
                padding = padding,
                contentRect = new RectInt(outerX + padding, outerY + padding, asset.width, asset.height)
            });
        }
    }

    internal static class AtlasImagePipeline
    {
        public static void BuildInputAtlases(
            string jobDirectory,
            IList<AtlasPageManifest> pages,
            IReadOnlyDictionary<string, TextureAssetInfo> assetsByPath,
            CancellationToken cancellationToken)
        {
            string inputDirectory = Path.Combine(jobDirectory, "atlas-input");
            Directory.CreateDirectory(inputDirectory);

            foreach (AtlasPageManifest page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relativeInput = $"atlas-input/page-{page.pageIndex:000}.png";
                // 续跑时若输入图集已在磁盘，直接复用，避免重复解码与写盘
                if (File.Exists(Path.Combine(jobDirectory, relativeInput)))
                {
                    page.inputFile = relativeInput.Replace('\\', '/');
                    continue;
                }

                var atlasPixels = new Color32[checked(page.width * page.height)];
                for (int i = 0; i < atlasPixels.Length; i++)
                    atlasPixels[i] = new Color32(0, 0, 0, 255);

                foreach (AtlasPlacement placement in page.placements)
                {
                    TextureAssetInfo info = assetsByPath[placement.assetPath];
                    SourceImage source = LoadSource(info);
                    Color32[] rgb = BuildOpaqueRgb(source.pixels, source.width, source.height);
                    BlitWithEdgePadding(
                        rgb,
                        source.width,
                        source.height,
                        atlasPixels,
                        page.width,
                        page.height,
                        placement.contentRect,
                        placement.padding);
                }

                var atlas = new Texture2D(page.width, page.height, TextureFormat.RGB24, false, false);
                try
                {
                    atlas.SetPixels32(atlasPixels);
                    atlas.Apply(false, false);
                    File.WriteAllBytes(Path.Combine(jobDirectory, relativeInput), atlas.EncodeToPNG());
                    page.inputFile = relativeInput.Replace('\\', '/');
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(atlas);
                }
            }
        }

        public static void SplitOutputAtlas(
            string jobDirectory,
            AtlasPageManifest page,
            IReadOnlyDictionary<string, TextureAssetInfo> assetsByPath,
            float expectedScale,
            int jpegQuality)
        {
            string outputPath = Path.Combine(jobDirectory, page.outputFile);
            byte[] bytes = File.ReadAllBytes(outputPath);
            var output = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                if (!ImageConversion.LoadImage(output, bytes, false))
                    throw new InvalidDataException("ComfyUI 输出不是可解码图片: " + page.outputFile);

                int expectedWidth = Mathf.FloorToInt(page.width * expectedScale);
                int expectedHeight = Mathf.FloorToInt(page.height * expectedScale);
                const int pageDimensionTolerance = 1;
                if (!MatchesExpectedDimensions(
                    page.width,
                    page.height,
                    output.width,
                    output.height,
                    expectedScale,
                    pageDimensionTolerance))
                {
                    throw new InvalidDataException(
                        $"图集页 {page.pageIndex} 输出为 {output.width}x{output.height}，" +
                        $"预期约 {expectedWidth}x{expectedHeight} ({expectedScale:0.##}x)。");
                }

                float scaleX = (float)output.width / page.width;
                float scaleY = (float)output.height / page.height;

                page.outputScale = scaleX;
                Color32[] outputPixels = output.GetPixels32();
                foreach (AtlasPlacement placement in page.placements)
                {
                    TextureAssetInfo info = assetsByPath[placement.assetPath];
                    int x0 = Mathf.RoundToInt(placement.contentRect.xMin * scaleX);
                    int y0 = Mathf.RoundToInt(placement.contentRect.yMin * scaleY);
                    int x1 = Mathf.RoundToInt(placement.contentRect.xMax * scaleX);
                    int y1 = Mathf.RoundToInt(placement.contentRect.yMax * scaleY);
                    int width = x1 - x0;
                    int height = y1 - y0;
                    if (width <= 0 || height <= 0 || x0 < 0 || y0 < 0 || x1 > output.width || y1 > output.height)
                        throw new InvalidDataException("拆图区域越界: " + placement.assetPath);
                    int expectedAssetWidth = Mathf.FloorToInt(info.width * expectedScale);
                    int expectedAssetHeight = Mathf.FloorToInt(info.height * expectedScale);
                    const int assetDimensionTolerance = 2;
                    if (!MatchesExpectedDimensions(
                        info.width,
                        info.height,
                        width,
                        height,
                        expectedScale,
                        assetDimensionTolerance))
                    {
                        throw new InvalidDataException(
                            $"单图输出尺寸与预期倍率不一致: {placement.assetPath}，" +
                            $"输出 {width}x{height}，预期约 {expectedAssetWidth}x{expectedAssetHeight}。");
                    }

                    var pixels = new Color32[checked(width * height)];
                    for (int y = 0; y < height; y++)
                    {
                        int sourceOffset = (y0 + y) * output.width + x0;
                        int destinationOffset = y * width;
                        Array.Copy(outputPixels, sourceOffset, pixels, destinationOffset, width);
                    }

                    if (info.hasAlpha && info.extension == ".png")
                    {
                        SourceImage original = LoadSource(info);
                        byte[] alpha = ResizeAlphaBilinear(original.pixels, original.width, original.height, width, height);
                        for (int i = 0; i < pixels.Length; i++)
                            pixels[i].a = alpha[i];
                    }
                    else
                    {
                        for (int i = 0; i < pixels.Length; i++)
                            pixels[i].a = 255;
                    }

                    string relative = Path.Combine("staged", info.assetPath).Replace('\\', '/');
                    string stagedPath = Path.Combine(jobDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath));
                    EncodeImage(stagedPath, info.extension, pixels, width, height, jpegQuality);
                    placement.outputWidth = width;
                    placement.outputHeight = height;
                    placement.scale = (float)width / info.width;
                    placement.stagedFile = relative;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(output);
            }
        }

        internal static bool MatchesExpectedDimensions(
            int inputWidth,
            int inputHeight,
            int outputWidth,
            int outputHeight,
            float expectedScale,
            int tolerance)
        {
            int expectedWidth = Mathf.FloorToInt(inputWidth * expectedScale);
            int expectedHeight = Mathf.FloorToInt(inputHeight * expectedScale);
            return Math.Abs(outputWidth - expectedWidth) <= tolerance &&
                   Math.Abs(outputHeight - expectedHeight) <= tolerance;
        }


        private static SourceImage LoadSource(TextureAssetInfo info)
        {
            byte[] bytes = File.ReadAllBytes(TextureScanner.AssetPathToFullPath(info.assetPath));
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, false))
                    throw new InvalidDataException("无法解码原图: " + info.assetPath);
                if (texture.width != info.width || texture.height != info.height)
                    throw new InvalidDataException("任务运行期间原图尺寸发生变化: " + info.assetPath);
                return new SourceImage(texture.width, texture.height, texture.GetPixels32());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static Color32[] BuildOpaqueRgb(Color32[] source, int width, int height)
        {
            var result = new Color32[source.Length];
            var distance = new int[source.Length];
            var queue = new Queue<int>();
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i].a > 0)
                {
                    result[i] = new Color32(source[i].r, source[i].g, source[i].b, 255);
                    distance[i] = 0;
                    queue.Enqueue(i);
                }
                else
                {
                    result[i] = new Color32(source[i].r, source[i].g, source[i].b, 255);
                    distance[i] = -1;
                }
            }

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int x = index % width;
                int y = index / width;
                Propagate(index - 1, x > 0, index, result, distance, queue);
                Propagate(index + 1, x + 1 < width, index, result, distance, queue);
                Propagate(index - width, y > 0, index, result, distance, queue);
                Propagate(index + width, y + 1 < height, index, result, distance, queue);
            }
            return result;
        }

        private static void Propagate(
            int target,
            bool valid,
            int source,
            Color32[] colors,
            int[] distances,
            Queue<int> queue)
        {
            if (!valid || distances[target] >= 0)
                return;
            colors[target] = colors[source];
            distances[target] = distances[source] + 1;
            queue.Enqueue(target);
        }

        private static void BlitWithEdgePadding(
            Color32[] source,
            int sourceWidth,
            int sourceHeight,
            Color32[] destination,
            int destinationWidth,
            int destinationHeight,
            RectInt content,
            int padding)
        {
            for (int y = -padding; y < sourceHeight + padding; y++)
            {
                int destinationY = content.y + y;
                if (destinationY < 0 || destinationY >= destinationHeight)
                    continue;
                int sourceY = Mathf.Clamp(y, 0, sourceHeight - 1);
                for (int x = -padding; x < sourceWidth + padding; x++)
                {
                    int destinationX = content.x + x;
                    if (destinationX < 0 || destinationX >= destinationWidth)
                        continue;
                    int sourceX = Mathf.Clamp(x, 0, sourceWidth - 1);
                    destination[destinationY * destinationWidth + destinationX] =
                        source[sourceY * sourceWidth + sourceX];
                }
            }
        }

        private static byte[] ResizeAlphaBilinear(
            Color32[] source,
            int sourceWidth,
            int sourceHeight,
            int destinationWidth,
            int destinationHeight)
        {
            var result = new byte[checked(destinationWidth * destinationHeight)];
            for (int y = 0; y < destinationHeight; y++)
            {
                float sourceY = Mathf.Clamp((y + 0.5f) * sourceHeight / destinationHeight - 0.5f, 0f, sourceHeight - 1f);
                int y0 = Mathf.FloorToInt(sourceY);
                int y1 = Mathf.Min(y0 + 1, sourceHeight - 1);
                float ty = sourceY - y0;
                for (int x = 0; x < destinationWidth; x++)
                {
                    float sourceX = Mathf.Clamp((x + 0.5f) * sourceWidth / destinationWidth - 0.5f, 0f, sourceWidth - 1f);
                    int x0 = Mathf.FloorToInt(sourceX);
                    int x1 = Mathf.Min(x0 + 1, sourceWidth - 1);
                    float tx = sourceX - x0;
                    float bottom = Mathf.Lerp(source[y0 * sourceWidth + x0].a, source[y0 * sourceWidth + x1].a, tx);
                    float top = Mathf.Lerp(source[y1 * sourceWidth + x0].a, source[y1 * sourceWidth + x1].a, tx);
                    result[y * destinationWidth + x] = (byte)Mathf.RoundToInt(Mathf.Lerp(bottom, top, ty));
                }
            }
            return result;
        }

        private static void EncodeImage(
            string path,
            string extension,
            Color32[] pixels,
            int width,
            int height,
            int jpegQuality)
        {
            bool jpeg = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            var texture = new Texture2D(width, height, jpeg ? TextureFormat.RGB24 : TextureFormat.RGBA32, false, false);
            try
            {
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                byte[] encoded = jpeg
                    ? texture.EncodeToJPG(Mathf.Clamp(jpegQuality, 1, 100))
                    : texture.EncodeToPNG();
                File.WriteAllBytes(path, encoded);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private sealed class SourceImage
        {
            public readonly int width;
            public readonly int height;
            public readonly Color32[] pixels;

            public SourceImage(int width, int height, Color32[] pixels)
            {
                this.width = width;
                this.height = height;
                this.pixels = pixels;
            }
        }
    }
}
