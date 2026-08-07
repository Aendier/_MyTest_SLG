using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ComfyUIUpscaler.Editor
{
    internal enum UpgradeAssetState
    {
        Unprocessed,
        Upgraded,
        Modified,
        RolledBack,
        Failed
    }

    internal enum UpgradeAssetFilter
    {
        All,
        Unprocessed,
        Upgraded,
        Modified,
        Failed,
        RolledBack
    }

    [Serializable]
    internal sealed class UpgradeAssetIndex
    {
        public string formatVersion = "1";
        public string updatedUtc;
        public List<UpgradeAssetRecord> assets = new List<UpgradeAssetRecord>();
    }

    [Serializable]
    internal sealed class UpgradeAssetRecord
    {
        public string guid;
        public string assetPath;
        public string lastAttemptJobId;
        public string lastAttemptUtc;
        public string lastAttemptStatus;
        public string lastSuccessfulJobId;
        public string completedUtc;
        public string workflowSha256;
        public int inputWidth;
        public int inputHeight;
        public int outputWidth;
        public int outputHeight;
        public float actualScale;
        public string outputSha256;
    }

    internal static class UpgradeAssetStateUtility
    {
        public static void Apply(TextureAssetInfo asset, UpgradeAssetRecord record)
        {
            asset.upgradeState = UpgradeAssetState.Unprocessed;
            asset.lastAttemptFailed = false;
            asset.lastAttemptStatus = string.Empty;
            asset.lastAttemptJobId = string.Empty;
            asset.lastAttemptUtc = string.Empty;
            asset.lastUpgradeJobId = string.Empty;
            asset.lastUpgradeUtc = string.Empty;
            asset.workflowSha256 = string.Empty;
            asset.lastInputWidth = 0;
            asset.lastInputHeight = 0;
            asset.lastOutputWidth = 0;
            asset.lastOutputHeight = 0;
            asset.lastActualScale = 0f;

            if (record == null)
                return;

            asset.lastAttemptStatus = record.lastAttemptStatus ?? string.Empty;
            asset.lastAttemptJobId = record.lastAttemptJobId ?? string.Empty;
            asset.lastAttemptUtc = record.lastAttemptUtc ?? string.Empty;
            asset.lastAttemptFailed = record.lastAttemptStatus == JobStatus.Failed ||
                                      record.lastAttemptStatus == JobStatus.Canceled;
            asset.lastUpgradeJobId = record.lastSuccessfulJobId ?? string.Empty;
            asset.lastUpgradeUtc = record.completedUtc ?? string.Empty;
            asset.workflowSha256 = record.workflowSha256 ?? string.Empty;
            asset.lastInputWidth = record.inputWidth;
            asset.lastInputHeight = record.inputHeight;
            asset.lastOutputWidth = record.outputWidth;
            asset.lastOutputHeight = record.outputHeight;
            asset.lastActualScale = record.actualScale;

            if (record.lastAttemptStatus == JobStatus.RolledBack)
            {
                asset.upgradeState = UpgradeAssetState.RolledBack;
                return;
            }

            bool hasSuccessfulOutput = !string.IsNullOrEmpty(record.outputSha256);
            if (!hasSuccessfulOutput)
            {
                asset.upgradeState = asset.lastAttemptFailed
                    ? UpgradeAssetState.Failed
                    : UpgradeAssetState.Unprocessed;
                return;
            }

            asset.upgradeState = string.Equals(
                asset.contentSha256,
                record.outputSha256,
                StringComparison.OrdinalIgnoreCase)
                ? UpgradeAssetState.Upgraded
                : UpgradeAssetState.Modified;

            if (asset.upgradeState == UpgradeAssetState.Upgraded)
                asset.selected = false;
        }

        public static bool MatchesFilter(TextureAssetInfo asset, UpgradeAssetFilter filter)
        {
            switch (filter)
            {
                case UpgradeAssetFilter.Unprocessed:
                    return asset.upgradeState == UpgradeAssetState.Unprocessed;
                case UpgradeAssetFilter.Upgraded:
                    return asset.upgradeState == UpgradeAssetState.Upgraded;
                case UpgradeAssetFilter.Modified:
                    return asset.upgradeState == UpgradeAssetState.Modified;
                case UpgradeAssetFilter.Failed:
                    return asset.lastAttemptFailed || asset.upgradeState == UpgradeAssetState.Failed;
                case UpgradeAssetFilter.RolledBack:
                    return asset.upgradeState == UpgradeAssetState.RolledBack;
                default:
                    return true;
            }
        }

        public static string GetLabel(TextureAssetInfo asset)
        {
            string label;
            switch (asset.upgradeState)
            {
                case UpgradeAssetState.Upgraded:
                    label = "已升级";
                    break;
                case UpgradeAssetState.Modified:
                    label = "升级后已修改";
                    break;
                case UpgradeAssetState.RolledBack:
                    label = "已回滚";
                    break;
                case UpgradeAssetState.Failed:
                    label = asset.lastAttemptStatus == JobStatus.Canceled ? "上次已取消" : "上次失败";
                    break;
                default:
                    label = "未升级";
                    break;
            }
            if (asset.lastAttemptFailed && asset.upgradeState != UpgradeAssetState.Failed)
                label += asset.lastAttemptStatus == JobStatus.Canceled ? " / 上次取消" : " / 上次失败";
            return label;
        }

        public static string GetLocalDate(string utc)
        {
            if (!DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime value))
                return string.Empty;
            return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
    }

    internal static class UpgradeHashUtility
    {
        public static string ComputeSha256(byte[] bytes)
        {
            using (SHA256 sha256 = SHA256.Create())
                return ToHex(sha256.ComputeHash(bytes));
        }

        public static string ComputeFileSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 sha256 = SHA256.Create())
                return ToHex(sha256.ComputeHash(stream));
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
                builder.Append(value.ToString("x2"));
            return builder.ToString();
        }
    }

    internal static class UpgradeAssetIndexStore
    {
        public static string IndexPath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName,
            "ProjectSettings",
            "ComfyUIUpscaler",
            "upgrade-map.json");

        public static void ApplyToAssets(IList<TextureAssetInfo> scannedAssets)
        {
            UpgradeAssetIndex index = LoadOrRebuild();
            var byGuid = index.assets
                .Where(record => !string.IsNullOrEmpty(record.guid))
                .GroupBy(record => record.guid, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            foreach (TextureAssetInfo asset in scannedAssets)
            {
                byGuid.TryGetValue(asset.guid ?? string.Empty, out UpgradeAssetRecord record);
                UpgradeAssetStateUtility.Apply(asset, record);
            }
        }

        public static void RecordFinalizedJob(UpscaleJobManifest manifest)
        {
            UpgradeAssetIndex index = LoadOrRebuild();
            var byGuid = index.assets
                .Where(record => !string.IsNullOrEmpty(record.guid))
                .GroupBy(record => record.guid, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            ApplyManifest(byGuid, manifest);
            index.assets = byGuid.Values.OrderBy(record => record.assetPath, StringComparer.Ordinal).ToList();
            Save(index);
        }

        public static UpgradeAssetIndex RebuildFromManifests()
        {
            var byGuid = new Dictionary<string, UpgradeAssetRecord>(StringComparer.Ordinal);
            foreach (JobRecord job in UpscaleJobStore.List()
                         .OrderBy(record => record.manifest.createdUtc, StringComparer.Ordinal))
            {
                ApplyManifest(byGuid, job.manifest);
            }

            var index = new UpgradeAssetIndex
            {
                assets = byGuid.Values.OrderBy(record => record.assetPath, StringComparer.Ordinal).ToList()
            };
            Save(index);
            return index;
        }

        private static UpgradeAssetIndex LoadOrRebuild()
        {
            if (!File.Exists(IndexPath))
                return RebuildFromManifests();
            try
            {
                UpgradeAssetIndex index = JsonUtility.FromJson<UpgradeAssetIndex>(
                    File.ReadAllText(IndexPath, Encoding.UTF8));
                if (index == null || index.formatVersion != "1")
                    throw new InvalidDataException("不支持的映射表格式。");
                if (index.assets == null)
                    index.assets = new List<UpgradeAssetRecord>();
                return index;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("升级映射表无法读取，将从本机任务记录重建。\n" + exception.Message);
                return RebuildFromManifests();
            }
        }

        private static void ApplyManifest(
            IDictionary<string, UpgradeAssetRecord> byGuid,
            UpscaleJobManifest manifest)
        {
            if (manifest == null || manifest.assets == null)
                return;
            if (manifest.status != JobStatus.Completed &&
                manifest.status != JobStatus.Failed &&
                manifest.status != JobStatus.Canceled &&
                manifest.status != JobStatus.RolledBack)
                return;

            var placements = (manifest.pages ?? new List<AtlasPageManifest>())
                .SelectMany(page => page.placements ?? new List<AtlasPlacement>())
                .GroupBy(placement => placement.assetPath, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            foreach (TextureAssetInfo asset in manifest.assets)
            {
                if (asset == null || string.IsNullOrEmpty(asset.guid))
                    continue;
                if (!byGuid.TryGetValue(asset.guid, out UpgradeAssetRecord record))
                {
                    record = new UpgradeAssetRecord { guid = asset.guid };
                    byGuid.Add(asset.guid, record);
                }

                string currentPath = AssetDatabase.GUIDToAssetPath(asset.guid);
                record.assetPath = string.IsNullOrEmpty(currentPath) ? asset.assetPath : currentPath;
                record.lastAttemptJobId = manifest.jobId;
                record.lastAttemptUtc = string.IsNullOrEmpty(manifest.completedUtc)
                    ? manifest.createdUtc
                    : manifest.completedUtc;
                record.lastAttemptStatus = manifest.status;

                if (manifest.status == JobStatus.Completed &&
                    placements.TryGetValue(asset.assetPath, out AtlasPlacement placement))
                {
                    record.lastSuccessfulJobId = manifest.jobId;
                    record.completedUtc = manifest.completedUtc;
                    record.workflowSha256 = manifest.workflowSha256;
                    record.inputWidth = asset.width;
                    record.inputHeight = asset.height;
                    record.outputWidth = placement.outputWidth;
                    record.outputHeight = placement.outputHeight;
                    record.actualScale = placement.scale;
                    record.outputSha256 = placement.outputSha256;
                }
                else if (manifest.status == JobStatus.RolledBack &&
                         record.lastSuccessfulJobId == manifest.jobId)
                {
                    ClearSuccessfulOutput(record);
                }
            }
        }

        private static void ClearSuccessfulOutput(UpgradeAssetRecord record)
        {
            record.lastSuccessfulJobId = string.Empty;
            record.completedUtc = string.Empty;
            record.workflowSha256 = string.Empty;
            record.inputWidth = 0;
            record.inputHeight = 0;
            record.outputWidth = 0;
            record.outputHeight = 0;
            record.actualScale = 0f;
            record.outputSha256 = string.Empty;
        }

        private static void Save(UpgradeAssetIndex index)
        {
            index.updatedUtc = DateTime.UtcNow.ToString("O");
            string directory = Path.GetDirectoryName(IndexPath);
            Directory.CreateDirectory(directory);
            string temporary = IndexPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(index, true), new UTF8Encoding(false));
            if (File.Exists(IndexPath))
                File.Replace(temporary, IndexPath, null);
            else
                File.Move(temporary, IndexPath);
        }
    }
}