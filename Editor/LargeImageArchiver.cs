using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools
{
    /// <summary>
    /// 大尺寸图片归档工具：
    /// 将“源文件夹”（含子目录）内像素尺寸超过阈值的图片，移动到“目标文件夹”下，
    /// 并保持与源文件夹一致的目录层级。移动使用 AssetDatabase.MoveAsset，保证 GUID/引用不丢失。
    /// 本工具自带菜单入口，可独立运行，无需在 UIRMenuRegister 中注册。
    /// </summary>
    public class LargeImageArchiver : EditorWindow
    {
        /// <summary>
        /// 尺寸判定模式：任一边超过阈值 / 两边都超过阈值。
        /// </summary>
        private enum SizeMode
        {
            EitherExceeds, // 宽或高任一超过阈值
            BothExceed,    // 宽和高都超过阈值
        }

        // 源文件夹与目标文件夹（限定为工程内文件夹）
        private DefaultAsset _sourceFolder;
        private DefaultAsset _targetFolder;

        // 像素阈值与判定模式
        private int _pixelThreshold = 1024;
        private SizeMode _sizeMode = SizeMode.EitherExceeds;

        // 预览结果缓存
        private readonly List<PreviewItem> _previewItems = new List<PreviewItem>();
        private Vector2 _scrollPos;

        // 反射缓存：TextureImporter.GetWidthAndHeight(ref int, ref int)，用于获取原始源尺寸
        private static MethodInfo _getWidthAndHeightMethod;

        /// <summary>
        /// 预览项：资源路径与其原始像素尺寸。
        /// </summary>
        private struct PreviewItem
        {
            public string AssetPath;
            public int Width;
            public int Height;
        }

        [MenuItem("UIR/大尺寸图片归档", false, 304)]
        public static void Open()
        {
            LargeImageArchiver window = GetWindow<LargeImageArchiver>("大尺寸图片归档");
            window.minSize = new Vector2(480f, 420f);
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "将【源文件夹】内像素尺寸超过阈值的图片，移动到【目标文件夹】，并保持相同的目录层级。\n" +
                "移动使用 AssetDatabase.MoveAsset，保留 GUID 与引用关系。",
                MessageType.Info);

            EditorGUILayout.Space();

            // 参数区
            _sourceFolder = (DefaultAsset)EditorGUILayout.ObjectField("源文件夹", _sourceFolder, typeof(DefaultAsset), false);
            _targetFolder = (DefaultAsset)EditorGUILayout.ObjectField("目标文件夹", _targetFolder, typeof(DefaultAsset), false);

            _pixelThreshold = EditorGUILayout.IntField("像素阈值", _pixelThreshold);
            if (_pixelThreshold < 1)
            {
                _pixelThreshold = 1;
            }

            _sizeMode = (SizeMode)EditorGUILayout.Popup(
                "判定条件",
                (int)_sizeMode,
                new[] { "宽或高任一超过阈值", "宽和高都超过阈值" });

            EditorGUILayout.Space();

            string sourcePath = GetFolderAssetPath(_sourceFolder);
            string targetPath = GetFolderAssetPath(_targetFolder);
            bool sourceValid = !string.IsNullOrEmpty(sourcePath);
            bool targetValid = !string.IsNullOrEmpty(targetPath);

            // 操作按钮区
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!sourceValid))
                {
                    if (GUILayout.Button("扫描预览", GUILayout.Height(28f)))
                    {
                        ScanPreview(sourcePath);
                    }
                }

                using (new EditorGUI.DisabledScope(!sourceValid || !targetValid || _previewItems.Count == 0))
                {
                    if (GUILayout.Button($"执行移动（{_previewItems.Count}）", GUILayout.Height(28f)))
                    {
                        ExecuteMove(sourcePath, targetPath);
                    }
                }
            }

            if (sourceValid && targetValid && IsSubPath(targetPath, sourcePath))
            {
                EditorGUILayout.HelpBox("目标文件夹不能是源文件夹或其子目录，否则会重复扫描到已移动的图片。", MessageType.Warning);
            }

            EditorGUILayout.Space();

            // 预览列表
            EditorGUILayout.LabelField($"命中图片：{_previewItems.Count} 张", EditorStyles.boldLabel);
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            foreach (PreviewItem item in _previewItems)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{item.Width}x{item.Height}", GUILayout.Width(90f));
                    EditorGUILayout.LabelField(item.AssetPath, EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 扫描源文件夹，收集所有满足阈值条件的图片。
        /// </summary>
        private void ScanPreview(string sourcePath)
        {
            _previewItems.Clear();

            string[] guids = AssetDatabase.FindAssets("t:Texture", new[] { sourcePath });
            HashSet<string> handled = new HashSet<string>();

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                    {
                        continue;
                    }

                    if (!handled.Add(assetPath))
                    {
                        continue;
                    }

                    EditorUtility.DisplayProgressBar("扫描图片尺寸", assetPath, guids.Length > 0 ? (float)i / guids.Length : 1f);

                    if (!TryGetImageSize(assetPath, out int width, out int height))
                    {
                        continue;
                    }

                    if (IsExceedThreshold(width, height))
                    {
                        _previewItems.Add(new PreviewItem { AssetPath = assetPath, Width = width, Height = height });
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"[大尺寸图片归档] 扫描完成：命中 {_previewItems.Count} 张（阈值 {_pixelThreshold}px，模式 {_sizeMode}）。");
        }

        /// <summary>
        /// 执行移动：保持相对层级，将命中图片移动到目标文件夹。
        /// </summary>
        private void ExecuteMove(string sourcePath, string targetPath)
        {
            if (IsSubPath(targetPath, sourcePath))
            {
                EditorUtility.DisplayDialog("提示", "目标文件夹不能是源文件夹或其子目录。", "确定");
                return;
            }

            int movedCount = 0;
            int skippedCount = 0;
            List<string> errors = new List<string>();

            try
            {
                // 注意：此处不使用 StartAssetEditing/StopAssetEditing 批处理。
                // 批处理期间 CreateFolder 不会立即生效，会导致 MoveAsset 因目标目录不存在而失败。
                for (int i = 0; i < _previewItems.Count; i++)
                {
                    string assetPath = _previewItems[i].AssetPath;
                    EditorUtility.DisplayProgressBar("移动图片", assetPath,
                        _previewItems.Count > 0 ? (float)i / _previewItems.Count : 1f);

                    // 计算相对源文件夹的相对路径，拼出目标路径以保持层级一致
                    string relative = assetPath.Substring(sourcePath.Length + 1);
                    string destPath = targetPath + "/" + relative;
                    string destDir = Path.GetDirectoryName(destPath).Replace("\\", "/");

                    EnsureFolder(destDir);

                    // 目标已存在同名文件则跳过，避免覆盖
                    if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(destPath)))
                    {
                        skippedCount++;
                        errors.Add($"[跳过-已存在] {destPath}");
                        continue;
                    }

                    string error = AssetDatabase.MoveAsset(assetPath, destPath);
                    if (string.IsNullOrEmpty(error))
                    {
                        movedCount++;
                    }
                    else
                    {
                        skippedCount++;
                        errors.Add($"[失败] {assetPath} -> {destPath}：{error}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();

            if (errors.Count > 0)
            {
                Debug.LogWarning("[大尺寸图片归档] 存在跳过/失败项：\n" + string.Join("\n", errors));
            }
            Debug.Log($"[大尺寸图片归档] 完成：移动 {movedCount} 张，跳过/失败 {skippedCount} 张。");
            EditorUtility.DisplayDialog("完成",
                $"移动成功 {movedCount} 张。\n跳过/失败 {skippedCount} 张（详见 Console）。",
                "确定");

            // 移动后清空预览，避免路径失效
            _previewItems.Clear();
        }

        /// <summary>
        /// 判定给定像素尺寸是否满足阈值条件。
        /// </summary>
        private bool IsExceedThreshold(int width, int height)
        {
            if (_sizeMode == SizeMode.BothExceed)
            {
                return width > _pixelThreshold && height > _pixelThreshold;
            }
            return width > _pixelThreshold || height > _pixelThreshold;
        }

        /// <summary>
        /// 获取图片原始像素尺寸（不受 maxTextureSize 限制）。
        /// 通过 TextureImporter.GetWidthAndHeight(ref int, ref int) 反射获取。
        /// </summary>
        private static bool TryGetImageSize(string assetPath, out int width, out int height)
        {
            width = 0;
            height = 0;

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return false;
            }

            if (_getWidthAndHeightMethod == null)
            {
                _getWidthAndHeightMethod = typeof(TextureImporter).GetMethod(
                    "GetWidthAndHeight",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_getWidthAndHeightMethod == null)
            {
                // 反射失败时退化为加载后的贴图尺寸（可能受导入设置影响）
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (tex == null)
                {
                    return false;
                }
                width = tex.width;
                height = tex.height;
                return true;
            }

            object[] args = { 0, 0 };
            _getWidthAndHeightMethod.Invoke(importer, args);
            width = (int)args[0];
            height = (int)args[1];
            return true;
        }

        /// <summary>
        /// 确保目标文件夹（含各级父目录）存在，不存在则逐级创建。
        /// </summary>
        private static void EnsureFolder(string folderPath)
        {
            folderPath = folderPath.Replace("\\", "/");
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string parent = Path.GetDirectoryName(folderPath).Replace("\\", "/");
            string folderName = Path.GetFileName(folderPath);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, folderName);
        }

        /// <summary>
        /// 获取 DefaultAsset 对应的文件夹资源路径；非文件夹返回空。
        /// </summary>
        private static string GetFolderAssetPath(DefaultAsset folder)
        {
            if (folder == null)
            {
                return string.Empty;
            }

            string path = AssetDatabase.GetAssetPath(folder);
            return AssetDatabase.IsValidFolder(path) ? path : string.Empty;
        }

        /// <summary>
        /// 判断 candidate 是否等于 root 或为 root 的子目录。
        /// </summary>
        private static bool IsSubPath(string candidate, string root)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root))
            {
                return false;
            }
            return candidate == root || candidate.StartsWith(root + "/");
        }
    }
}
