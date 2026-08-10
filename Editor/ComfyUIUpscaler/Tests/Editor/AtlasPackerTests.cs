using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace ComfyUIUpscaler.Editor.Tests
{
    public sealed class AtlasPackerTests
    {
        [Test]
        public void Pack_IsDeterministicAndPreservesEveryAsset()
        {
            var assets = new List<TextureAssetInfo>
            {
                Asset("Assets/Z.png", 48, 24),
                Asset("Assets/A.png", 32, 32),
                Asset("Assets/M.jpg", 16, 40)
            };

            List<AtlasPageManifest> first = AtlasPacker.Pack(assets, 4, 128, 16384);
            List<AtlasPageManifest> second = AtlasPacker.Pack(assets, 4, 128, 16384);

            Assert.That(first.Count, Is.EqualTo(second.Count));
            Assert.That(first[0].placements.Count, Is.EqualTo(3));
            for (int i = 0; i < first[0].placements.Count; i++)
            {
                Assert.That(first[0].placements[i].assetPath, Is.EqualTo(second[0].placements[i].assetPath));
                Assert.That(first[0].placements[i].contentRect, Is.EqualTo(second[0].placements[i].contentRect));
            }
        }

        [Test]
        public void Pack_CreatesAdditionalPagesWhenPixelLimitIsReached()
        {
            var assets = new List<TextureAssetInfo>
            {
                Asset("Assets/A.png", 48, 48),
                Asset("Assets/B.png", 48, 48),
                Asset("Assets/C.png", 48, 48)
            };

            List<AtlasPageManifest> pages = AtlasPacker.Pack(assets, 0, 64, 4096);

            Assert.That(pages.Count, Is.EqualTo(3));
        }

        [Test]
        public void Pack_RejectsAnImageLargerThanTheConfiguredPage()
        {
            var assets = new List<TextureAssetInfo> { Asset("Assets/Large.png", 128, 64) };
            Assert.Throws<InvalidOperationException>(() => AtlasPacker.Pack(assets, 8, 128, 16384));
        }

        private static TextureAssetInfo Asset(string path, int width, int height)
        {
            return new TextureAssetInfo
            {
                selected = true,
                assetPath = path,
                width = width,
                height = height
            };
        }
    }
}
