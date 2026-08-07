using System.Text;
using NUnit.Framework;

namespace ComfyUIUpscaler.Editor.Tests
{
    public sealed class UpgradeAssetIndexTests
    {
        [Test]
        public void MatchingSuccessfulOutput_IsUpgradedAndDeselected()
        {
            var asset = new TextureAssetInfo
            {
                selected = true,
                contentSha256 = "same"
            };
            var record = CreateSuccessfulRecord("same");

            UpgradeAssetStateUtility.Apply(asset, record);

            Assert.AreEqual(UpgradeAssetState.Upgraded, asset.upgradeState);
            Assert.IsFalse(asset.selected);
            Assert.AreEqual("job-success", asset.lastUpgradeJobId);
        }

        [Test]
        public void ChangedSuccessfulOutput_IsModifiedAndRemainsSelected()
        {
            var asset = new TextureAssetInfo
            {
                selected = true,
                contentSha256 = "changed"
            };

            UpgradeAssetStateUtility.Apply(asset, CreateSuccessfulRecord("expected"));

            Assert.AreEqual(UpgradeAssetState.Modified, asset.upgradeState);
            Assert.IsTrue(asset.selected);
        }

        [Test]
        public void FailedAttemptWithoutSuccess_IsFailed()
        {
            var asset = new TextureAssetInfo { selected = true, contentSha256 = "current" };
            var record = new UpgradeAssetRecord
            {
                lastAttemptJobId = "job-failed",
                lastAttemptStatus = JobStatus.Failed
            };

            UpgradeAssetStateUtility.Apply(asset, record);

            Assert.AreEqual(UpgradeAssetState.Failed, asset.upgradeState);
            Assert.IsTrue(asset.lastAttemptFailed);
            Assert.IsTrue(UpgradeAssetStateUtility.MatchesFilter(asset, UpgradeAssetFilter.Failed));
        }

        [Test]
        public void FailedAttemptAfterSuccess_PreservesCurrentUpgradeStateAndMatchesFailedFilter()
        {
            var asset = new TextureAssetInfo { selected = true, contentSha256 = "same" };
            UpgradeAssetRecord record = CreateSuccessfulRecord("same");
            record.lastAttemptJobId = "job-failed";
            record.lastAttemptStatus = JobStatus.Failed;

            UpgradeAssetStateUtility.Apply(asset, record);

            Assert.AreEqual(UpgradeAssetState.Upgraded, asset.upgradeState);
            Assert.IsFalse(asset.selected);
            Assert.IsTrue(UpgradeAssetStateUtility.MatchesFilter(asset, UpgradeAssetFilter.Upgraded));
            Assert.IsTrue(UpgradeAssetStateUtility.MatchesFilter(asset, UpgradeAssetFilter.Failed));
        }

        [Test]
        public void ComputeSha256_ReturnsStableLowercaseHex()
        {
            string result = UpgradeHashUtility.ComputeSha256(Encoding.ASCII.GetBytes("abc"));

            Assert.AreEqual(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                result);
        }

        private static UpgradeAssetRecord CreateSuccessfulRecord(string outputSha256)
        {
            return new UpgradeAssetRecord
            {
                lastAttemptJobId = "job-success",
                lastAttemptStatus = JobStatus.Completed,
                lastSuccessfulJobId = "job-success",
                completedUtc = "2026-08-05T10:00:00.0000000Z",
                inputWidth = 100,
                inputHeight = 50,
                outputWidth = 250,
                outputHeight = 125,
                actualScale = 2.5f,
                outputSha256 = outputSha256
            };
        }
    }
}