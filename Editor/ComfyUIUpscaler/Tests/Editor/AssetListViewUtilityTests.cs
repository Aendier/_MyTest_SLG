using System;
using NUnit.Framework;

namespace ComfyUIUpscaler.Editor.Tests
{
    public sealed class AssetListViewUtilityTests
    {
        [Test]
        public void MatchesSearch_FiltersAssetPathCaseInsensitively()
        {
            var asset = new TextureAssetInfo
            {
                assetPath = "Assets/UI/Chat/SP_Chat_Red.png"
            };

            Assert.That(AssetListViewUtility.MatchesSearch(asset, "chat_red"), Is.True);
            Assert.That(AssetListViewUtility.MatchesSearch(asset, "battle"), Is.False);
        }

        [Test]
        public void GetPageCount_IncludesPartialLastPage()
        {
            Assert.That(AssetListViewUtility.GetPageCount(101, 50), Is.EqualTo(3));
            Assert.That(AssetListViewUtility.GetPageCount(0, 50), Is.EqualTo(1));
        }

        [Test]
        public void ClampPageIndex_ClampsAfterFilterShrinks()
        {
            Assert.That(AssetListViewUtility.ClampPageIndex(8, 12, 50), Is.EqualTo(0));
            Assert.That(AssetListViewUtility.ClampPageIndex(-1, 120, 50), Is.EqualTo(0));
        }

        [Test]
        public void CollapseNestedFolders_RemovesDuplicatesAndCoveredChildren()
        {
            var result = TextureScanner.CollapseNestedFolders(new[]
            {
                "Assets/UI/Chat",
                "Assets/World",
                "Assets/UI",
                "Assets/UI",
                "Assets/World/Maps"
            });

            CollectionAssert.AreEqual(new[] { "Assets/UI", "Assets/World" }, result);
        }

        [Test]
        public void FormatSizeSummary_ReportsTotalsDeltaAndPercentage()
        {
            var manifest = new UpscaleJobManifest
            {
                originalTotalBytes = 1024,
                outputTotalBytes = 2560
            };

            Assert.That(
                UpscaleJobStore.FormatSizeSummary(manifest),
                Is.EqualTo("1 KB -> 2.5 KB (+1.5 KB, +150%)"));
        }

        [Test]
        public void Pack_WhenPixelLimitIsExceeded_ReportsTheSpecificLimit()
        {
            var asset = new TextureAssetInfo
            {
                selected = true,
                assetPath = "Assets/Test.png",
                width = 750,
                height = 2755
            };

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                AtlasPacker.Pack(new[] { asset }, 32, 4096, 2000000));

            StringAssert.Contains("814x2819（2294666 像素）", exception.Message);
            StringAssert.Contains("像素数 2294666 > 最大像素数 2000000", exception.Message);
            StringAssert.Contains("当前限制：最大边长 4096，最大像素数 2000000", exception.Message);
        }
    }
}
