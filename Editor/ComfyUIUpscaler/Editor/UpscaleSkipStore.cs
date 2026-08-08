using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ComfyUIUpscaler.Editor
{
    // “跳过/不升级”标记的持久化数据：按文件夹（递归）与单个资源 GUID 两种维度记录
    [Serializable]
    internal sealed class UpscaleSkipData
    {
        public string formatVersion = "1";
        public List<string> folders = new List<string>();
        public List<string> assetGuids = new List<string>();
    }

    // 工具自管的“跳过”标记存储，落盘到 ProjectSettings；文件夹按前缀递归匹配（含以后新增的资源）
    internal static class UpscaleSkipStore
    {
        public static string SkipPath => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName,
            "ProjectSettings",
            "ComfyUIUpscaler",
            "skip-list.json");

        private static UpscaleSkipData Load()
        {
            if (!File.Exists(SkipPath))
                return new UpscaleSkipData();
            try
            {
                var data = JsonUtility.FromJson<UpscaleSkipData>(File.ReadAllText(SkipPath, Encoding.UTF8));
                if (data == null)
                    return new UpscaleSkipData();
                if (data.folders == null)
                    data.folders = new List<string>();
                if (data.assetGuids == null)
                    data.assetGuids = new List<string>();
                return data;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("跳过标记无法读取，按空处理。\n" + exception.Message);
                return new UpscaleSkipData();
            }
        }

        private static void Save(UpscaleSkipData data)
        {
            string directory = Path.GetDirectoryName(SkipPath);
            Directory.CreateDirectory(directory);
            string temporary = SkipPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(data, true), new UTF8Encoding(false));
            if (File.Exists(SkipPath))
                File.Replace(temporary, SkipPath, null);
            else
                File.Move(temporary, SkipPath);
        }

        // 归一化文件夹路径：反斜杠转正斜杠、去尾部斜杠
        private static string Normalize(string path)
        {
            return (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
        }

        public static List<string> GetFolders()
        {
            return Load().folders
                .Select(Normalize)
                .Where(folder => folder.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 用规范化后的文件夹清单整体覆盖（配合窗口里的文件夹列表编辑）
        public static void SetFolders(IEnumerable<string> folders)
        {
            UpscaleSkipData data = Load();
            data.folders = folders
                .Select(Normalize)
                .Where(folder => folder.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Save(data);
        }

        public static void SetAssetSkipped(string guid, bool skipped)
        {
            if (string.IsNullOrEmpty(guid))
                return;
            UpscaleSkipData data = Load();
            bool has = data.assetGuids.Contains(guid);
            if (skipped && !has)
                data.assetGuids.Add(guid);
            else if (!skipped && has)
                data.assetGuids.Remove(guid);
            else
                return;
            Save(data);
        }

        // 依据存储把 skipped 标记应用到扫描到的资源：命中单资源 GUID 或任一祖先文件夹即视为跳过
        public static void ApplyToAssets(IEnumerable<TextureAssetInfo> assets)
        {
            UpscaleSkipData data = Load();
            var guidSet = new HashSet<string>(data.assetGuids, StringComparer.Ordinal);
            List<string> folderPrefixes = data.folders
                .Select(Normalize)
                .Where(folder => folder.Length > 0)
                .Select(folder => folder + "/")
                .ToList();
            foreach (TextureAssetInfo asset in assets)
            {
                if (asset == null)
                    continue;
                bool byGuid = !string.IsNullOrEmpty(asset.guid) && guidSet.Contains(asset.guid);
                string path = asset.assetPath ?? string.Empty;
                bool byFolder = folderPrefixes.Any(prefix =>
                    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                asset.skipped = byGuid || byFolder;
            }
        }
    }
}
