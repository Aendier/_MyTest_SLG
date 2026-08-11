using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

namespace ComfyUIUpscaler.Editor
{
    internal sealed class JobRecord
    {
        public string directory;
        public UpscaleJobManifest manifest;
    }

    // 恢复计划：把任务资源分为可安全恢复 / 已变化(跳过) / 缺失备份，并统计将写入的备份体积
    internal sealed class RestorePlan
    {
        public readonly List<TextureAssetInfo> safeAssets = new List<TextureAssetInfo>();
        public readonly List<TextureAssetInfo> changedAssets = new List<TextureAssetInfo>();
        public readonly List<string> changedNotes = new List<string>();
        public readonly List<string> missingNotes = new List<string>();
        // 按 GUID 记录每个资源的变化详情（如“已移动”“图片内容已改变”“元数据已改变”），供 UI 逐行展示
        public readonly Dictionary<string, string> detailByGuid = new Dictionary<string, string>(StringComparer.Ordinal);
        public long safeBytes;        // 安全项将写入的备份总字节
        public long restorableBytes;  // 安全项 + 变化项（有备份）合计，供“强制恢复”估算
        public int TotalAssets => safeAssets.Count + changedAssets.Count + missingNotes.Count;
    }

    internal static class UpscaleJobStore
    {
        public static string RootDirectory => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName,
            "Library",
            "ComfyUIUpscaler",
            "Jobs");

        public static JobRecord Create(UpscalerRunSettings settings, IList<TextureAssetInfo> selectedAssets)
        {
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string directory = Path.Combine(RootDirectory, id);
            Directory.CreateDirectory(directory);

            var manifest = new UpscaleJobManifest
            {
                jobId = id,
                createdUtc = DateTime.UtcNow.ToString("O"),
                status = JobStatus.Created,
                sourceFolder = settings.sourceFolder,
                sourceFolders = (settings.sourceFolders ?? new List<string>()).ToList(),
                comfyUrl = settings.comfyUrl,
                workflowPath = settings.workflowPath,
                inputNodeId = settings.inputNodeId,
                inputFieldName = settings.inputFieldName,
                outputNodeId = settings.outputNodeId,
                expectedScale = settings.expectedScale,
                padding = settings.padding,
                maxAtlasEdge = settings.maxAtlasEdge,
                maxAtlasPixels = settings.maxAtlasPixels,
                requestTimeoutSeconds = settings.requestTimeoutSeconds,
                jobTimeoutMinutes = settings.jobTimeoutMinutes,
                jpegQuality = settings.jpegQuality,
                keepDisplaySize = settings.keepDisplaySize,
                assets = selectedAssets.ToList()
            };

            try
            {
                manifest.originalTotalBytes = BackupFiles(directory, manifest.assets);
                Save(directory, manifest);
                return new JobRecord { directory = directory, manifest = manifest };
            }
            catch
            {
                manifest.status = JobStatus.Failed;
                manifest.completedUtc = DateTime.UtcNow.ToString("O");
                manifest.error = "创建备份失败。";
                Save(directory, manifest);
                TryRecordFinalizedJob(manifest);
                throw;
            }
        }

        public static void Save(string directory, UpscaleJobManifest manifest)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "manifest.json");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }

        public static UpscaleJobManifest Load(string directory)
        {
            string path = Path.Combine(directory, "manifest.json");
            return JsonUtility.FromJson<UpscaleJobManifest>(File.ReadAllText(path, Encoding.UTF8));
        }

        public static List<JobRecord> List()
        {
            if (!Directory.Exists(RootDirectory))
                return new List<JobRecord>();

            var records = new List<JobRecord>();
            foreach (string directory in Directory.GetDirectories(RootDirectory).OrderByDescending(path => path, StringComparer.Ordinal))
            {
                try
                {
                    records.Add(new JobRecord { directory = directory, manifest = Load(directory) });
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("跳过损坏的 ComfyUI Upscaler 任务记录: " + directory + "\n" + exception.Message);
                }
            }
            return records;
        }

        // 仅未完成（处理中/已取消/失败）且已生成图集页的任务可以尝试继续
        public static bool CanAttemptResume(UpscaleJobManifest manifest)
        {
            return manifest != null &&
                   (manifest.status == JobStatus.Processing ||
                    manifest.status == JobStatus.Canceled ||
                    manifest.status == JobStatus.Failed) &&
                   manifest.pages != null && manifest.pages.Count > 0;
        }

        public static List<string> GetResumeConflicts(string directory)
        {
            UpscaleJobManifest manifest = Load(directory);
            var conflicts = new List<string>();
            if (!CanAttemptResume(manifest))
            {
                conflicts.Add("仅未完成（处理中/已取消/失败）且已生成图集页的任务可以继续。");
                return conflicts;
            }

            // 校验工作流未变化：SHA-256 需与任务记录一致，否则续跑会与已生成图集不匹配
            if (string.IsNullOrEmpty(manifest.workflowPath) || !File.Exists(manifest.workflowPath))
            {
                conflicts.Add("原工作流 JSON 不存在: " + manifest.workflowPath);
            }
            else if (!string.IsNullOrEmpty(manifest.workflowSha256))
            {
                string currentSha = UpgradeHashUtility.ComputeSha256(
                    Encoding.UTF8.GetBytes(File.ReadAllText(manifest.workflowPath, Encoding.UTF8)));
                if (!string.Equals(currentSha, manifest.workflowSha256, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add("工作流 JSON 已变化，继续可能与已生成图集不一致，请重新开始。");
            }

            // 校验每个源图 GUID 未变、源图与 .meta 仍在，保证续跑后能安全覆盖并保留引用
            foreach (TextureAssetInfo asset in manifest.assets)
            {
                if (asset == null || string.IsNullOrEmpty(asset.assetPath))
                    continue;
                if (!string.Equals(AssetDatabase.AssetPathToGUID(asset.assetPath), asset.guid, StringComparison.Ordinal))
                {
                    conflicts.Add(asset.assetPath + "：GUID 已变化或资源已移动/删除");
                    continue;
                }
                string fullPath = TextureScanner.AssetPathToFullPath(asset.assetPath);
                if (!File.Exists(fullPath) || !File.Exists(fullPath + ".meta"))
                    conflicts.Add(asset.assetPath + "：源图或 .meta 不存在");
            }
            return conflicts;
        }

        public static void TryRecordFinalizedJob(UpscaleJobManifest manifest)
        {
            try
            {
                UpgradeAssetIndexStore.RecordFinalizedJob(manifest);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("升级映射表更新失败，可从任务 manifest 重建。\n" + exception);
            }
        }

        public static void RestoreFiles(string directory, IEnumerable<TextureAssetInfo> assets)
        {
            bool editing = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                editing = true;
                foreach (TextureAssetInfo asset in assets)
                {
                    string currentAssetPath = ResolveCurrentAssetPath(asset);
                    string original = TextureScanner.AssetPathToFullPath(currentAssetPath);
                    string backup = ResolveBackupFile(directory, asset);
                    string backupMeta = backup + ".meta";
                    if (string.IsNullOrEmpty(backup) || !File.Exists(backup) || !File.Exists(backupMeta))
                        throw new FileNotFoundException("备份不完整: " + asset.assetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(original));
                    File.Copy(backup, original, true);
                    File.Copy(backupMeta, original + ".meta", true);
                }
            }
            finally
            {
                if (editing)
                    AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
        }

        // 待哈希项：阶段一在主线程解析好路径与期望哈希，阶段二在后台线程池并行计算比对
        private sealed class RestoreHashJob
        {
            public TextureAssetInfo asset;
            public string currentAssetPath;
            public string fullPath;
            public string metaPath;
            public string expectedImgSha;
            public string expectedMetaSha;
            public string movePrefix;
            public long backupBytes;
            public bool moved;
            public bool imgMatch;
            public bool metaMatch;
        }

        // 异步构建恢复计划：主线程解析路径与非哈希分类 → 后台线程池并行哈希比对(带缓存) → 主线程汇总
        public static async Task<RestorePlan> BuildRestorePlanAsync(
            string directory,
            Action<float, string> progress,
            CancellationToken cancellationToken)
        {
            var plan = new RestorePlan();
            UpscaleJobManifest manifest = Load(directory);
            if (manifest == null)
                return plan;

            var placements = (manifest.pages ?? new List<AtlasPageManifest>())
                .SelectMany(page => page.placements ?? new List<AtlasPlacement>())
                .GroupBy(p => p.assetPath, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

            List<TextureAssetInfo> assets = manifest.assets ?? new List<TextureAssetInfo>();

            // 阶段一（主线程）：解析 GUID→路径、备份/文件是否就绪；非哈希分支就地分类，其余收集为待哈希项
            var hashJobs = new List<RestoreHashJob>();
            var sliceWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < assets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RestoreHashJob job = PrepareRestoreItem(directory, assets[i], placements, plan);
                if (job != null)
                    hashJobs.Add(job);

                bool last = i == assets.Count - 1;
                if (sliceWatch.ElapsedMilliseconds >= 30 || last)
                {
                    // 阶段一占进度前 5%
                    progress?.Invoke(assets.Count == 0 ? 0.05f : 0.05f * (i + 1) / assets.Count,
                        $"解析资源 {i + 1}/{assets.Count}");
                    if (!last)
                    {
                        await Task.Yield();
                        sliceWatch.Restart();
                    }
                }
            }

            // 阶段二（后台线程池并行）：计算图片/元数据哈希并比对；哈希结果按(路径,大小,修改时间)缓存
            if (hashJobs.Count > 0)
            {
                int total = hashJobs.Count;
                int done = 0;
                Task hashTask = Task.Run(() =>
                {
                    var options = new ParallelOptions { CancellationToken = cancellationToken };
                    Parallel.ForEach(hashJobs, options, job =>
                    {
                        job.imgMatch = string.Equals(
                            UpgradeHashUtility.ComputeFileSha256Cached(job.fullPath),
                            job.expectedImgSha, StringComparison.OrdinalIgnoreCase);
                        job.metaMatch = string.Equals(
                            UpgradeHashUtility.ComputeFileSha256Cached(job.metaPath),
                            job.expectedMetaSha, StringComparison.OrdinalIgnoreCase);
                        Interlocked.Increment(ref done);
                    });
                }, cancellationToken);

                // 主线程轮询上报进度（await 续体回到主线程，调用 progress 安全）
                while (!hashTask.IsCompleted)
                {
                    progress?.Invoke(0.05f + 0.95f * done / total, $"并行校验哈希 {done}/{total}");
                    await Task.Delay(80, cancellationToken);
                }
                await hashTask;  // 传播并行阶段的异常/取消
            }

            // 阶段三（主线程）：按哈希结果汇总为安全/变化，并写入逐行变化详情
            foreach (RestoreHashJob job in hashJobs)
                FinalizeHashJob(job, plan);

            progress?.Invoke(1f, "校验完成");
            return plan;
        }

        // 阶段一：主线程解析并对非哈希分支就地分类；需要哈希的返回待办项(null 表示已就地处理)
        private static RestoreHashJob PrepareRestoreItem(
            string directory,
            TextureAssetInfo asset,
            IReadOnlyDictionary<string, AtlasPlacement> placements,
            RestorePlan plan)
        {
            if (asset == null)
                return null;

            void SetDetail(string detail)
            {
                if (!string.IsNullOrEmpty(asset.guid))
                    plan.detailByGuid[asset.guid] = detail;
            }

            string currentAssetPath = AssetDatabase.GUIDToAssetPath(asset.guid);
            if (string.IsNullOrEmpty(currentAssetPath))
            {
                SetDetail("资源已删除或 GUID 失效");
                plan.missingNotes.Add(asset.assetPath + "：GUID 对应资源不存在");
                return null;
            }

            // 移动检测：GUID 仍在，但当前路径与任务记录的路径不同 → 资源被移动/改名
            bool moved = !string.IsNullOrEmpty(asset.assetPath) &&
                         !string.Equals(currentAssetPath, asset.assetPath, StringComparison.Ordinal);
            string movePrefix = moved ? "已移动；" : string.Empty;

            string backup = ResolveBackupFile(directory, asset);
            bool backupOk = !string.IsNullOrEmpty(backup) && File.Exists(backup) && File.Exists(backup + ".meta");
            long backupBytes = 0;
            if (backupOk)
            {
                try { backupBytes = new FileInfo(backup).Length; } catch { backupBytes = 0; }
            }
            if (!backupOk)
            {
                SetDetail(movePrefix + "备份不完整");
                plan.missingNotes.Add(currentAssetPath + "：备份不完整，无法恢复");
                return null;
            }

            if (!placements.TryGetValue(asset.assetPath, out AtlasPlacement placement) ||
                string.IsNullOrEmpty(placement.outputSha256) || string.IsNullOrEmpty(placement.outputMetaSha256))
            {
                plan.changedAssets.Add(asset);
                plan.changedNotes.Add(currentAssetPath + "：任务缺少输出哈希，无法确认一致性");
                plan.restorableBytes += backupBytes;
                SetDetail(movePrefix + "缺少输出哈希，无法确认");
                return null;
            }
            string fullPath = TextureScanner.AssetPathToFullPath(currentAssetPath);
            string metaPath = fullPath + ".meta";
            if (!File.Exists(fullPath) || !File.Exists(metaPath))
            {
                plan.changedAssets.Add(asset);
                plan.changedNotes.Add(currentAssetPath + "：当前图片或 .meta 不存在");
                plan.restorableBytes += backupBytes;
                SetDetail(movePrefix + "当前图片或 .meta 缺失");
                return null;
            }

            return new RestoreHashJob
            {
                asset = asset,
                currentAssetPath = currentAssetPath,
                fullPath = fullPath,
                metaPath = metaPath,
                expectedImgSha = placement.outputSha256,
                expectedMetaSha = placement.outputMetaSha256,
                movePrefix = movePrefix,
                backupBytes = backupBytes,
                moved = moved
            };
        }

        // 阶段三：根据并行得到的图片/元数据哈希匹配结果，把待办项归入安全/变化并写详情
        private static void FinalizeHashJob(RestoreHashJob job, RestorePlan plan)
        {
            void SetDetail(string detail)
            {
                if (!string.IsNullOrEmpty(job.asset.guid))
                    plan.detailByGuid[job.asset.guid] = detail;
            }

            if (job.imgMatch && job.metaMatch)
            {
                plan.safeAssets.Add(job.asset);
                plan.safeBytes += job.backupBytes;
                plan.restorableBytes += job.backupBytes;
                // 内容一致；仅在移动时给出提示，未移动则无详情
                SetDetail(job.moved ? "已移动（内容一致）" : string.Empty);
            }
            else
            {
                // 依据图片/元数据哈希细分变化来源，回答“资源变了还是位置变了”
                string what;
                if (!job.imgMatch && !job.metaMatch)
                    what = "图片与元数据均已改变";
                else if (!job.imgMatch)
                    what = "图片内容已改变";
                else
                    what = "元数据(.meta)已改变";
                plan.changedAssets.Add(job.asset);
                plan.changedNotes.Add(job.currentAssetPath + "：任务完成后已变化");
                plan.restorableBytes += job.backupBytes;
                SetDetail(job.movePrefix + what);
            }
        }

        // 异步恢复给定资源：分片复制备份(含 .meta)覆盖原文件，末尾一次性导入；每个成功项在映射表标记已回滚
        public static async Task<int> RestoreAssetsAsync(
            string directory,
            IList<TextureAssetInfo> assets,
            Action<float, string> progress,
            CancellationToken cancellationToken)
        {
            UpscaleJobManifest manifest = Load(directory);
            int restored = 0;
            var restoredGuids = new List<string>();
            if (assets != null && assets.Count > 0)
            {
                var sliceWatch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < assets.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TextureAssetInfo asset = assets[i];
                    string backup = ResolveBackupFile(directory, asset);
                    if (!string.IsNullOrEmpty(backup) && File.Exists(backup) && File.Exists(backup + ".meta"))
                    {
                        string currentAssetPath = ResolveCurrentAssetPath(asset);
                        string original = TextureScanner.AssetPathToFullPath(currentAssetPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(original));
                        File.Copy(backup, original, true);
                        File.Copy(backup + ".meta", original + ".meta", true);
                        restored++;
                        if (!string.IsNullOrEmpty(asset.guid))
                            restoredGuids.Add(asset.guid);
                    }

                    bool last = i == assets.Count - 1;
                    if (sliceWatch.ElapsedMilliseconds >= 30 || last)
                    {
                        progress?.Invoke(assets.Count == 0 ? 1f : (float)(i + 1) / assets.Count,
                            $"恢复中 {i + 1}/{assets.Count}");
                        if (!last)
                        {
                            await Task.Yield();
                            sliceWatch.Restart();
                        }
                    }
                }
            }

            // 复制完成后一次性触发导入，避免逐张导入卡顿
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            if (manifest != null)
            {
                int totalAssets = manifest.assets?.Count ?? 0;
                manifest.log.Add(DateTime.Now.ToString("HH:mm:ss") +
                                 $" 恢复完成：已恢复 {restored}/{totalAssets} 个资源（含 .meta）。");
                if (totalAssets > 0 && restored >= totalAssets)
                {
                    // 整任务全部恢复：标记回滚，映射表按 manifest 一次性回滚（避免逐个落盘）
                    manifest.status = JobStatus.RolledBack;
                    manifest.completedUtc = DateTime.UtcNow.ToString("O");
                    Save(directory, manifest);
                    WriteReport(directory, manifest);
                    TryRecordFinalizedJob(manifest);
                }
                else
                {
                    // 部分恢复：保留任务状态以便后续继续，仅把实际恢复的资源批量标记为已回滚
                    Save(directory, manifest);
                    WriteReport(directory, manifest);
                    UpgradeAssetIndexStore.MarkAssetsRolledBack(restoredGuids, manifest.jobId);
                }
            }
            return restored;
        }

        public static void WriteReport(string directory, UpscaleJobManifest manifest)
        {
            var builder = new StringBuilder();
            builder.AppendLine("ComfyUI 图片批量高清化报告");
            builder.AppendLine("Job: " + manifest.jobId);
            builder.AppendLine("状态: " + manifest.status);
            builder.AppendLine("创建: " + manifest.createdUtc);
            builder.AppendLine("完成: " + manifest.completedUtc);
            List<string> sourceFolders = manifest.sourceFolders != null && manifest.sourceFolders.Count > 0
                ? manifest.sourceFolders
                : new List<string> { manifest.sourceFolder };
            builder.AppendLine("目录: " + string.Join(", ", sourceFolders.Where(path => !string.IsNullOrEmpty(path))));
            builder.AppendLine($"资源: {manifest.assets.Count}，图集: {manifest.pages.Count}，预期倍率: {manifest.expectedScale:0.##}x");
            builder.AppendLine("升级完成时图片总体积: " + FormatSizeSummary(manifest));
            if (!string.IsNullOrEmpty(manifest.error))
                builder.AppendLine("错误: " + manifest.error);
            builder.AppendLine();
            foreach (TextureAssetInfo asset in manifest.assets)
            {
                AtlasPlacement placement = manifest.pages.SelectMany(page => page.placements)
                    .FirstOrDefault(item => item.assetPath == asset.assetPath);
                string result = placement == null
                    ? "未生成"
                    : $"{asset.width}x{asset.height} -> {placement.outputWidth}x{placement.outputHeight}";
                builder.AppendLine(asset.assetPath + " | " + result +
                                   (string.IsNullOrEmpty(asset.warning) ? string.Empty : " | " + asset.warning));
            }
            builder.AppendLine();
            builder.AppendLine("日志:");
            foreach (string line in manifest.log)
                builder.AppendLine(line);
            File.WriteAllText(Path.Combine(directory, "report.txt"), builder.ToString(), new UTF8Encoding(false));
        }

        internal static string FormatSizeSummary(UpscaleJobManifest manifest)
        {
            if (manifest == null || manifest.originalTotalBytes <= 0 || manifest.outputTotalBytes <= 0)
                return "未记录";

            long delta = manifest.outputTotalBytes - manifest.originalTotalBytes;
            double percent = delta * 100d / manifest.originalTotalBytes;
            string sign = delta >= 0 ? "+" : "-";
            return $"{FormatBytes(manifest.originalTotalBytes)} -> {FormatBytes(manifest.outputTotalBytes)} " +
                   $"({sign}{FormatBytes(Math.Abs(delta))}, {percent:+0.##;-0.##;0}%)";
        }

        internal static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024d && unitIndex < units.Length - 1)
            {
                value /= 1024d;
                unitIndex++;
            }
            return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unitIndex];
        }

        private static long BackupFiles(string directory, IEnumerable<TextureAssetInfo> assets)
        {
            long totalBytes = 0;
            foreach (TextureAssetInfo asset in assets)
            {
                string source = TextureScanner.AssetPathToFullPath(asset.assetPath);
                string meta = source + ".meta";
                if (!File.Exists(source) || !File.Exists(meta))
                    throw new FileNotFoundException("原图或 .meta 不存在: " + asset.assetPath);
                string backup = GetBackupPath(directory, asset);
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.Copy(source, backup, false);
                File.Copy(meta, backup + ".meta", false);
                totalBytes = checked(totalBytes + new FileInfo(backup).Length);
            }
            return totalBytes;
        }

        private static string ResolveCurrentAssetPath(TextureAssetInfo asset)
        {
            string currentPath = AssetDatabase.GUIDToAssetPath(asset.guid);
            return string.IsNullOrEmpty(currentPath) ? asset.assetPath : currentPath;
        }

        // 备份文件名改用资源 GUID 短名，避免镜像深层 Assets 路径触发 Windows 260 长路径限制
        private static string GetBackupPath(string directory, TextureAssetInfo asset)
        {
            if (asset == null || string.IsNullOrEmpty(asset.guid))
                throw new ArgumentException("资源缺少 GUID，无法确定备份路径: " + asset?.assetPath);
            string root = Path.GetFullPath(Path.Combine(directory, "backup"));
            string extension = string.IsNullOrEmpty(asset.extension)
                ? Path.GetExtension(asset.assetPath)
                : asset.extension;
            string result = Path.GetFullPath(Path.Combine(root, asset.guid + extension));
            if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("备份路径越界: " + asset.assetPath);
            return result;
        }

        // 兼容旧任务：旧任务的备份按资源路径镜像存放，读取时可回退到该旧路径
        private static string GetLegacyBackupPath(string directory, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                return string.Empty;
            string root = Path.GetFullPath(Path.Combine(directory, "backup"));
            string result = Path.GetFullPath(Path.Combine(root, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return result;
        }

        // 解析实际存在的备份文件：优先新版 GUID 备份，其次回退旧版镜像备份；均不存在时返回新版路径（调用方需容错）
        internal static string ResolveBackupFile(string directory, TextureAssetInfo asset)
        {
            if (asset == null)
                return string.Empty;
            string guidPath = string.IsNullOrEmpty(asset.guid) ? string.Empty : GetBackupPath(directory, asset);
            if (!string.IsNullOrEmpty(guidPath) && File.Exists(guidPath))
                return guidPath;
            string legacy = GetLegacyBackupPath(directory, asset.assetPath);
            if (!string.IsNullOrEmpty(legacy) && File.Exists(legacy))
                return legacy;
            return guidPath;
        }
    }

    internal static class UpscaleJobRunner
    {
        public static async Task<UpscaleJobManifest> RunAsync(
            IList<TextureAssetInfo> assets,
            UpscalerRunSettings settings,
            Action<float, string> progress,
            Action<string> externalLog,
            CancellationToken cancellationToken)
        {
            ValidateSettings(settings);
            List<TextureAssetInfo> selected = assets.Where(asset => asset.selected).ToList();
            if (selected.Count == 0)
                throw new InvalidOperationException("没有选中待处理图片。");

            // 预先装箱，参数非法时在创建任务前失败
            List<AtlasPageManifest> packedPages = AtlasPacker.Pack(
                selected,
                settings.padding,
                settings.maxAtlasEdge,
                settings.maxAtlasPixels);
            string workflowJson = File.ReadAllText(settings.workflowPath, Encoding.UTF8);
            JobRecord job = UpscaleJobStore.Create(settings, selected);
            job.manifest.workflowSha256 = UpgradeHashUtility.ComputeSha256(Encoding.UTF8.GetBytes(workflowJson));
            job.manifest.pages = packedPages;
            UpscaleJobStore.Save(job.directory, job.manifest);
            return await ExecuteAsync(job, workflowJson, settings, progress, externalLog, cancellationToken);
        }

        // 从已有任务继续：跳过已完成的图集页，仅重跑剩余网络处理，再统一拆分与提交
        public static async Task<UpscaleJobManifest> ResumeAsync(
            JobRecord job,
            int fallbackRequestTimeoutSeconds,
            int fallbackJobTimeoutMinutes,
            int fallbackJpegQuality,
            Action<float, string> progress,
            Action<string> externalLog,
            CancellationToken cancellationToken)
        {
            List<string> conflicts = UpscaleJobStore.GetResumeConflicts(job.directory);
            if (conflicts.Count > 0)
                throw new InvalidOperationException("无法继续：\n" + string.Join("\n", conflicts));

            // 重新读取磁盘上最新的 manifest，避免使用陈旧内存副本
            UpscaleJobManifest manifest = UpscaleJobStore.Load(job.directory);
            var freshJob = new JobRecord { directory = job.directory, manifest = manifest };
            var settings = new UpscalerRunSettings
            {
                sourceFolder = manifest.sourceFolder,
                sourceFolders = manifest.sourceFolders ?? new List<string>(),
                comfyUrl = manifest.comfyUrl,
                workflowPath = manifest.workflowPath,
                inputNodeId = manifest.inputNodeId,
                inputFieldName = manifest.inputFieldName,
                outputNodeId = manifest.outputNodeId,
                expectedScale = manifest.expectedScale,
                padding = manifest.padding,
                maxAtlasEdge = manifest.maxAtlasEdge,
                maxAtlasPixels = manifest.maxAtlasPixels,
                requestTimeoutSeconds = manifest.requestTimeoutSeconds > 0
                    ? manifest.requestTimeoutSeconds
                    : fallbackRequestTimeoutSeconds,
                jobTimeoutMinutes = manifest.jobTimeoutMinutes > 0
                    ? manifest.jobTimeoutMinutes
                    : fallbackJobTimeoutMinutes,
                jpegQuality = manifest.jpegQuality > 0
                    ? manifest.jpegQuality
                    : fallbackJpegQuality,
                keepDisplaySize = manifest.keepDisplaySize
            };
            ValidateSettings(settings);
            string workflowJson = File.ReadAllText(settings.workflowPath, Encoding.UTF8);
            externalLog?.Invoke(DateTime.Now.ToString("HH:mm:ss") +
                                " 从任务 " + manifest.jobId + " 继续，已完成的图集页将被跳过。");
            return await ExecuteAsync(freshJob, workflowJson, settings, progress, externalLog, cancellationToken);
        }

        private static async Task<UpscaleJobManifest> ExecuteAsync(
            JobRecord job,
            string workflowJson,
            UpscalerRunSettings settings,
            Action<float, string> progress,
            Action<string> externalLog,
            CancellationToken cancellationToken)
        {
            UpscaleJobManifest manifest = job.manifest;

            void Log(string message)
            {
                string line = DateTime.Now.ToString("HH:mm:ss") + " " + message;
                manifest.log.Add(line);
                externalLog?.Invoke(line);
                UpscaleJobStore.Save(job.directory, manifest);
            }

            try
            {
                manifest.status = JobStatus.Processing;
                manifest.error = string.Empty;
                UpscaleJobStore.Save(job.directory, manifest);

                var assetsByPath = manifest.assets.ToDictionary(asset => asset.assetPath, StringComparer.Ordinal);
                progress?.Invoke(0.05f, "生成 RGB 图集");
                AtlasImagePipeline.BuildInputAtlases(job.directory, manifest.pages, assetsByPath, cancellationToken);
                UpscaleJobStore.Save(job.directory, manifest);
                Log("已生成/校验 " + manifest.pages.Count + " 张输入图集。");

                var client = new ComfyUIClient(settings.comfyUrl, settings.requestTimeoutSeconds);
                string outputDirectory = Path.Combine(job.directory, "atlas-output");
                Directory.CreateDirectory(outputDirectory);
                for (int i = 0; i < manifest.pages.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AtlasPageManifest page = manifest.pages[i];
                    string relativeOutput = $"atlas-output/page-{page.pageIndex:000}.png";
                    // 续跑时跳过已下载完成的图集页
                    if (!string.IsNullOrEmpty(page.outputFile) &&
                        File.Exists(Path.Combine(job.directory, relativeOutput)))
                    {
                        progress?.Invoke(0.1f + 0.55f * i / manifest.pages.Count, $"跳过已完成图集 {i + 1}/{manifest.pages.Count}");
                        Log($"跳过已完成图集 {i + 1}/{manifest.pages.Count}。");
                        continue;
                    }

                    progress?.Invoke(0.1f + 0.55f * i / manifest.pages.Count, $"ComfyUI 处理图集 {i + 1}/{manifest.pages.Count}");
                    string inputPath = Path.Combine(job.directory, page.inputFile);
                    ComfyOutputImage image = await client.RunPageAsync(
                        inputPath,
                        workflowJson,
                        settings.inputNodeId,
                        settings.inputFieldName,
                        settings.outputNodeId,
                        TimeSpan.FromMinutes(settings.jobTimeoutMinutes),
                        Log,
                        cancellationToken);
                    byte[] outputBytes = await client.DownloadAsync(image, cancellationToken);
                    File.WriteAllBytes(Path.Combine(job.directory, relativeOutput), outputBytes);
                    page.outputFile = relativeOutput.Replace('\\', '/');
                    page.promptId = image.promptId;
                    UpscaleJobStore.Save(job.directory, manifest);
                    Log($"已下载图集 {i + 1}/{manifest.pages.Count}: {image.filename}");
                }

                for (int i = 0; i < manifest.pages.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Invoke(0.65f + 0.2f * i / manifest.pages.Count, $"校验并拆分图集 {i + 1}/{manifest.pages.Count}");
                    AtlasImagePipeline.SplitOutputAtlas(
                        job.directory,
                        manifest.pages[i],
                        assetsByPath,
                        settings.expectedScale,
                        settings.jpegQuality);
                }
                ValidateStagedFiles(job.directory, manifest);
                manifest.status = JobStatus.ReadyToCommit;
                UpscaleJobStore.Save(job.directory, manifest);
                Log("所有输出已生成并通过尺寸校验，开始集中替换原图。");

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(0.9f, "替换原图并校验引用");
                try
                {
                    Commit(job.directory, manifest);
                    manifest.outputTotalBytes = CalculateCurrentAssetBytes(manifest.assets);
                }
                catch (Exception commitException)
                {
                    UpscaleJobStore.RestoreFiles(job.directory, manifest.assets);
                    manifest.status = JobStatus.RolledBack;
                    throw new InvalidOperationException("替换或引用校验失败，已自动回滚。", commitException);
                }

                manifest.status = JobStatus.Completed;
                manifest.completedUtc = DateTime.UtcNow.ToString("O");
                progress?.Invoke(1f, "完成");
                Log("任务完成，GUID 和 Sprite local fileID 校验通过。图片总体积: " +
                    UpscaleJobStore.FormatSizeSummary(manifest));
                UpscaleJobStore.WriteReport(job.directory, manifest);
                UpscaleJobStore.TryRecordFinalizedJob(manifest);
                return manifest;
            }
            catch (OperationCanceledException)
            {
                manifest.status = JobStatus.Canceled;
                manifest.completedUtc = DateTime.UtcNow.ToString("O");
                manifest.error = "用户中断，进度已保留，可从历史继续。源资源未修改。";
                UpscaleJobStore.Save(job.directory, manifest);
                UpscaleJobStore.WriteReport(job.directory, manifest);
                UpscaleJobStore.TryRecordFinalizedJob(manifest);
                throw;
            }
            catch (Exception exception)
            {
                if (manifest.status != JobStatus.RolledBack)
                    manifest.status = JobStatus.Failed;
                manifest.completedUtc = DateTime.UtcNow.ToString("O");
                manifest.error = exception.ToString();
                manifest.log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + exception.Message);
                UpscaleJobStore.Save(job.directory, manifest);
                UpscaleJobStore.WriteReport(job.directory, manifest);
                UpscaleJobStore.TryRecordFinalizedJob(manifest);
                throw;
            }
        }

        private static void ValidateSettings(UpscalerRunSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (!File.Exists(settings.workflowPath))
                throw new FileNotFoundException("请选择 ComfyUI API Format JSON。", settings.workflowPath);
            if (string.IsNullOrWhiteSpace(settings.inputNodeId) || string.IsNullOrWhiteSpace(settings.outputNodeId))
                throw new InvalidOperationException("必须配置输入节点 ID 和最终输出节点 ID。");
            if (string.IsNullOrWhiteSpace(settings.inputFieldName))
                throw new InvalidOperationException("必须配置输入图片字段名。");
            if (float.IsNaN(settings.expectedScale) || float.IsInfinity(settings.expectedScale) ||
                settings.expectedScale < 1f)
                throw new InvalidOperationException("预期放大倍率必须大于等于 1。");
        }

        private static long CalculateCurrentAssetBytes(IEnumerable<TextureAssetInfo> assets)
        {
            long totalBytes = 0;
            foreach (TextureAssetInfo asset in assets)
            {
                string path = TextureScanner.AssetPathToFullPath(asset.assetPath);
                totalBytes = checked(totalBytes + new FileInfo(path).Length);
            }
            return totalBytes;
        }

        private static void ValidateStagedFiles(string jobDirectory, UpscaleJobManifest manifest)
        {
            var placements = manifest.pages.SelectMany(page => page.placements).ToList();
            if (placements.Count != manifest.assets.Count)
                throw new InvalidDataException("暂存输出数量与源资源数量不一致。");
            foreach (AtlasPlacement placement in placements)
            {
                string stagedPath = string.IsNullOrEmpty(placement.stagedFile)
                    ? string.Empty
                    : Path.Combine(jobDirectory, placement.stagedFile);
                if (string.IsNullOrEmpty(stagedPath) || !File.Exists(stagedPath) ||
                    placement.outputWidth <= 0 || placement.outputHeight <= 0)
                    throw new InvalidDataException("暂存输出不完整: " + placement.assetPath);
                placement.outputSha256 = UpgradeHashUtility.ComputeFileSha256(stagedPath);
            }
        }

        private static void Commit(string jobDirectory, UpscaleJobManifest manifest)
        {
            var placements = manifest.pages.SelectMany(page => page.placements)
                .ToDictionary(placement => placement.assetPath, StringComparer.Ordinal);

            foreach (TextureAssetInfo asset in manifest.assets)
            {
                if (AssetDatabase.AssetPathToGUID(asset.assetPath) != asset.guid)
                    throw new InvalidOperationException("提交前 GUID 已变化: " + asset.assetPath);
            }

            bool editing = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                editing = true;
                foreach (TextureAssetInfo asset in manifest.assets)
                {
                    string staged = Path.Combine(jobDirectory, placements[asset.assetPath].stagedFile);
                    File.Copy(staged, TextureScanner.AssetPathToFullPath(asset.assetPath), true);
                }
            }
            finally
            {
                if (editing)
                    AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            // 批量应用 Sprite 元数据：把逐张 SaveAndReimport 收敛为 StopAssetEditing 时的一次批量导入
            bool metaEditing = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                metaEditing = true;
                foreach (TextureAssetInfo asset in manifest.assets)
                    ApplySpriteMetadataSettings(asset, placements[asset.assetPath], manifest.keepDisplaySize);
            }
            finally
            {
                if (metaEditing)
                    AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            // 导入完成后统一校验引用与 Sprite 元数据，并记录输出哈希
            foreach (TextureAssetInfo asset in manifest.assets)
            {
                VerifyReferences(asset);
                VerifySpriteMetadata(asset);
                AtlasPlacement placement = placements[asset.assetPath];
                string outputPath = TextureScanner.AssetPathToFullPath(asset.assetPath);
                placement.outputSha256 = UpgradeHashUtility.ComputeFileSha256(outputPath);
                placement.outputMetaSha256 = UpgradeHashUtility.ComputeFileSha256(outputPath + ".meta");
            }
        }

        // 仅设置并保存 Sprite 元数据（不做校验），供批量 SaveAndReimport 使用；校验在批量导入后统一进行
        private static void ApplySpriteMetadataSettings(TextureAssetInfo info, AtlasPlacement placement, bool keepDisplaySize)
        {
            var importer = AssetImporter.GetAtPath(info.assetPath) as TextureImporter;
            if (importer == null || importer.textureType != TextureImporterType.Sprite)
                return;

            // Fractional workflows round each output dimension independently; use the validated actual scales.
            float scaleX = (float)placement.outputWidth / info.width;
            float scaleY = (float)placement.outputHeight / info.height;

            // 保持显示尺寸：纹理放大多少，pixelsPerUnit 就放大多少，使“像素÷ppu”不变（显示尺寸/九宫格外观保持）
            if (keepDisplaySize)
            {
                float ppuScale = (scaleX + scaleY) * 0.5f;
                if (ppuScale > 0f)
                    importer.spritePixelsPerUnit *= ppuScale;
            }

            if (importer.spriteImportMode == SpriteImportMode.Single)
            {
                importer.spriteBorder = ScaleBorder(info.singleBorder, scaleX, scaleY);
                importer.SaveAndReimport();
                return;
            }
            if (importer.spriteImportMode != SpriteImportMode.Multiple)
                return;

            var factories = new SpriteDataProviderFactories();
            factories.Init();
            ISpriteEditorDataProvider provider = factories.GetSpriteEditorDataProviderFromObject(importer);
            if (provider == null)
                throw new InvalidOperationException("无法取得 Sprite Data Provider: " + info.assetPath);
            provider.InitSpriteEditorDataProvider();
            SpriteRect[] rects = provider.GetSpriteRects();
            var snapshots = info.sprites.ToDictionary(sprite => sprite.spriteId, StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (SpriteRect rect in rects)
            {
                string id = rect.spriteID.ToString();
                if (!snapshots.TryGetValue(id, out SpriteMetadata snapshot))
                    continue;
                rect.rect = new Rect(
                    snapshot.rect.x * scaleX,
                    snapshot.rect.y * scaleY,
                    snapshot.rect.width * scaleX,
                    snapshot.rect.height * scaleY);
                rect.border = ScaleBorder(snapshot.border, scaleX, scaleY);
                found.Add(id);
            }
            if (found.Count != snapshots.Count)
                throw new InvalidOperationException("Multiple Sprite ID 集合在提交前发生变化: " + info.assetPath);
            provider.SetSpriteRects(rects);
            provider.Apply();
            importer.SaveAndReimport();
        }

        // 批量导入完成后校验 Multiple Sprite 的 ID/名称/Pivot/Alignment 是否保持
        private static void VerifySpriteMetadata(TextureAssetInfo info)
        {
            var importer = AssetImporter.GetAtPath(info.assetPath) as TextureImporter;
            if (importer == null || importer.textureType != TextureImporterType.Sprite ||
                importer.spriteImportMode != SpriteImportMode.Multiple)
                return;

            var factories = new SpriteDataProviderFactories();
            factories.Init();
            ISpriteEditorDataProvider provider = factories.GetSpriteEditorDataProviderFromObject(importer);
            if (provider == null)
                throw new InvalidOperationException("无法取得 Sprite Data Provider: " + info.assetPath);
            provider.InitSpriteEditorDataProvider();
            var verified = provider.GetSpriteRects().ToDictionary(rect => rect.spriteID.ToString(), StringComparer.Ordinal);
            foreach (SpriteMetadata snapshot in info.sprites)
            {
                if (!verified.TryGetValue(snapshot.spriteId, out SpriteRect rect) || rect.name != snapshot.name ||
                    rect.pivot != snapshot.pivot || (int)rect.alignment != snapshot.alignment)
                    throw new InvalidOperationException("Multiple Sprite 的 ID/名称/Pivot/Alignment 未保持: " + info.assetPath);
            }
        }

        private static Vector4 ScaleBorder(Vector4 border, float scaleX, float scaleY)
        {
            return new Vector4(border.x * scaleX, border.y * scaleY, border.z * scaleX, border.w * scaleY);
        }

        private static void VerifyReferences(TextureAssetInfo info)
        {
            string guid = AssetDatabase.AssetPathToGUID(info.assetPath);
            if (!string.Equals(guid, info.guid, StringComparison.Ordinal))
                throw new InvalidOperationException("资源 GUID 改变: " + info.assetPath);

            var current = new Dictionary<long, AssetReferenceSnapshot>();
            foreach (Sprite sprite in AssetDatabase.LoadAllAssetsAtPath(info.assetPath).OfType<Sprite>())
            {
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string spriteGuid, out long fileId))
                    throw new InvalidOperationException("无法校验 Sprite 引用: " + info.assetPath);
                current[fileId] = new AssetReferenceSnapshot { name = sprite.name, guid = spriteGuid, localFileId = fileId };
            }
            foreach (AssetReferenceSnapshot before in info.references)
            {
                if (!current.TryGetValue(before.localFileId, out AssetReferenceSnapshot after) ||
                    after.name != before.name || after.guid != before.guid)
                    throw new InvalidOperationException(
                        $"Sprite 引用改变: {info.assetPath}/{before.name} (local fileID {before.localFileId})");
            }
        }
    }
}
