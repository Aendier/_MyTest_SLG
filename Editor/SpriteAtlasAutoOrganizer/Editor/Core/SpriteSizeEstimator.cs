using System.Collections.Generic;

namespace SpriteAtlasAutoOrganizer.Editor
{
    internal static class SpriteSizeEstimator
    {
        /// <summary>
        /// 第一版面积预估：单图 (w+padding)*(h+padding)，再乘 packingSlack。
        /// 不把原始宽高直接相加当 Atlas 面积。
        /// </summary>
        public static long EstimateSpriteArea(int width, int height, int padding, float slack)
        {
            int w = width > 0 ? width : 1;
            int h = height > 0 ? height : 1;
            int pad = padding < 0 ? 0 : padding;
            long area = (long)(w + pad) * (h + pad);
            if (slack > 1f)
                area = (long)(area * slack);
            return area;
        }

        public static void EstimateAtlasSize(
            long estimatedArea,
            int maxAtlasSize,
            out int width,
            out int height)
        {
            int max = maxAtlasSize > 0 ? maxAtlasSize : 2048;
            int[] sizes = { 1024, 2048, 4096 };
            int chosen = max;
            for (int i = 0; i < sizes.Length; i++)
            {
                if (sizes[i] > max)
                    break;
                if (estimatedArea <= (long)sizes[i] * sizes[i])
                {
                    chosen = sizes[i];
                    break;
                }
            }

            width = chosen;
            height = chosen;
        }

        public static HashSet<SpriteKey> ToKeySet(IEnumerable<SpriteRecord> sprites)
        {
            var set = new HashSet<SpriteKey>();
            if (sprites == null)
                return set;

            foreach (SpriteRecord sprite in sprites)
            {
                if (sprite != null && !sprite.InManualAtlas)
                    set.Add(sprite.Key);
            }

            return set;
        }
    }
}
