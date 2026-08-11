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

                // 探测 ComfyUI 实际放大倍率：由用户在 ComfyUI 侧手动保证为整数 POT（如 2×/4×）且 ≥ 目标倍率。
                // 整数倍率下，裁剪坐标 = 原坐标 × 整数，精确且与图在图集中的位置无关 → 同尺寸输入必得同尺寸裁剪。
                float rawScaleX = (float)output.width / page.width;
                float rawScaleY = (float)output.height / page.height;
                int modelScale = Mathf.RoundToInt(rawScaleX);
                const float scaleIntegerTolerance = 0.02f;
                if (modelScale < 1 ||
                    Mathf.Abs(rawScaleX - modelScale) > scaleIntegerTolerance ||
                    Mathf.Abs(rawScaleY - modelScale) > scaleIntegerTolerance)
                {
                    throw new InvalidDataException(
                        $"图集页 {page.pageIndex} 输出为 {output.width}x{output.height}，" +
                        $"相对输入 {page.width}x{page.height} 不是整数倍（探测到约 {rawScaleX:0.###}x）。" +
                        "请在 ComfyUI 侧使用纯整数倍放大（不要在工作流内再做缩放）。");
                }
                if (modelScale < expectedScale)
                {
                    throw new InvalidDataException(
                        $"图集页 {page.pageIndex} 的放大倍率 {modelScale}x 小于目标倍率 {expectedScale:0.##}x，" +
                        "无法通过降采样得到目标尺寸。请在 ComfyUI 侧提高放大倍率。");
                }

                page.outputScale = modelScale;
                Color32[] outputPixels = output.GetPixels32();
                foreach (AtlasPlacement placement in page.placements)
                {
                    TextureAssetInfo info = assetsByPath[placement.assetPath];

                    // 第一步：按整数倍率精确裁出该图（例如 40×40 → 恒为 40·N × 40·N）
                    int cropX = Mathf.RoundToInt(placement.contentRect.xMin) * modelScale;
                    int cropY = Mathf.RoundToInt(placement.contentRect.yMin) * modelScale;
                    int cropW = Mathf.RoundToInt(placement.contentRect.width) * modelScale;
                    int cropH = Mathf.RoundToInt(placement.contentRect.height) * modelScale;
                    if (cropW <= 0 || cropH <= 0 || cropX < 0 || cropY < 0 ||
                        cropX + cropW > output.width || cropY + cropH > output.height)
                        throw new InvalidDataException("拆图区域越界: " + placement.assetPath);

                    var cropped = new Color32[checked(cropW * cropH)];
                    for (int y = 0; y < cropH; y++)
                        Array.Copy(outputPixels, (cropY + y) * output.width + cropX, cropped, y * cropW, cropW);

                    // 第二步：目标尺寸只由“原始尺寸 × 目标倍率”决定（与位置、与所在页无关）→ 全局一致
                    int targetW = Mathf.Max(1, Mathf.RoundToInt(info.width * expectedScale));
                    int targetH = Mathf.Max(1, Mathf.RoundToInt(info.height * expectedScale));
                    // 从更高分辨率的整数倍裁剪块降采样到目标（超采样 + 面积平均，抗锯齿、无放大发虚）
                    Color32[] pixels = DownsampleRgbArea(cropped, cropW, cropH, targetW, targetH);

                    if (info.hasAlpha && info.extension == ".png")
                    {
                        SourceImage original = LoadSource(info);
                        byte[] alpha = ResizeAlphaBilinear(original.pixels, original.width, original.height, targetW, targetH);
                        for (int i = 0; i < pixels.Length; i++)
                            pixels[i].a = alpha[i];
                    }
                    else
                    {
                        for (int i = 0; i < pixels.Length; i++)
                            pixels[i].a = 255;
                    }

                    // 暂存文件名改用资源 GUID 短名，避免镜像深层 Assets 路径导致 Windows 260 长路径写入失败
                    string stagedExtension = string.IsNullOrEmpty(info.extension)
                        ? Path.GetExtension(info.assetPath)
                        : info.extension;
                    string relative = "staged/" + info.guid + stagedExtension;
                    string stagedPath = Path.Combine(jobDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath));
                    EncodeImage(stagedPath, info.extension, pixels, targetW, targetH, jpegQuality);
                    placement.outputWidth = targetW;
                    placement.outputHeight = targetH;
                    placement.scale = (float)targetW / info.width;
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

        // RGB 面积（box）降采样：对每个目标像素，取其在源图覆盖范围内的所有源像素求平均。
        // 相比双线性只取 2×2，面积平均在 2×/4× 这类较大降采样比下能保留更多细节、抗锯齿更好；alpha 单独处理。
        private static Color32[] DownsampleRgbArea(
            Color32[] source,
            int sourceWidth,
            int sourceHeight,
            int destinationWidth,
            int destinationHeight)
        {
            var result = new Color32[checked(destinationWidth * destinationHeight)];
            for (int y = 0; y < destinationHeight; y++)
            {
                int sy0 = Mathf.FloorToInt((float)y * sourceHeight / destinationHeight);
                int sy1 = Mathf.Min(sourceHeight - 1, Mathf.CeilToInt((float)(y + 1) * sourceHeight / destinationHeight) - 1);
                if (sy1 < sy0)
                    sy1 = sy0;
                for (int x = 0; x < destinationWidth; x++)
                {
                    int sx0 = Mathf.FloorToInt((float)x * sourceWidth / destinationWidth);
                    int sx1 = Mathf.Min(sourceWidth - 1, Mathf.CeilToInt((float)(x + 1) * sourceWidth / destinationWidth) - 1);
                    if (sx1 < sx0)
                        sx1 = sx0;

                    int sumR = 0, sumG = 0, sumB = 0, count = 0;
                    for (int yy = sy0; yy <= sy1; yy++)
                    {
                        int rowOffset = yy * sourceWidth;
                        for (int xx = sx0; xx <= sx1; xx++)
                        {
                            Color32 c = source[rowOffset + xx];
                            sumR += c.r;
                            sumG += c.g;
                            sumB += c.b;
                            count++;
                        }
                    }
                    if (count == 0)
                        count = 1;
                    result[y * destinationWidth + x] = new Color32(
                        (byte)(sumR / count),
                        (byte)(sumG / count),
                        (byte)(sumB / count),
                        255);
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
