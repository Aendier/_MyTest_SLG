using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools
{
    /// <summary>
    /// 图片降尺寸工具
    /// 选择一个文件夹，扫描出所有“最长边超过指定阈值”的图片，勾选其中需要处理的，
    /// 一键把它们的纹理导入 Max Size 设为目标尺寸（等比缩到该尺寸内）。
    /// 仅修改纹理导入设置(maxTextureSize)，不移动/改写源文件，GUID 与引用保持不变；
    /// 记录旧的 Max Size 以支持撤销。全部操作前有确认提示，避免误触。
    /// 采用 IMGUI 实现，仅依赖 Unity 内置 API。
    /// </summary>
    public class TextureDownscaleWindow : EditorWindow
    {
        // ==================== EditorPrefs 键 ====================

        private const string PrefKeySource = "TextureDownscale.SourceFolder";
        private const string PrefKeyThreshold = "TextureDownscale.Threshold";
        private const string PrefKeyTarget = "TextureDownscale.Target";
        private const string PrefKeyRecursive = "TextureDownscale.Recursive";
        private const string PrefKeyMode = "TextureDownscale.Mode";

        /// <summary>撤销清单相关（工程根目录下，与 Assets 同级，不会被 Unity 导入）</summary>
        private const string BackupRootName = "TextureDownscaleBackups";
        private const string ManifestFileName = "last_operation.json";

        /// <summary>目标尺寸候选（maxTextureSize 仅支持 2 的幂）</summary>
        private static readonly int[] TargetSizeOptions = { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };

        /// <summary>缩放方式</summary>
        private enum DownscaleMode
        {
            /// <summary>改纹理导入 Max Size（非破坏，不改源文件）</summary>
            MaxSize = 0,

            /// <summary>物理缩图：改写源文件像素尺寸（需备份，可撤销；仅 PNG/JPG）</summary>
            Physical = 1
        }

        // ==================== 运行时字段 ====================

        [Tooltip("待扫描图片所在的文件夹")]
        private DefaultAsset _sourceFolder;

        /// <summary>扫描阈值：最长边超过该值的图片会被列出</summary>
        private int _threshold = 1024;

        /// <summary>目标尺寸：把图片等比缩到该尺寸内（写入 maxTextureSize）</summary>
        private int _target = 1024;

        /// <summary>是否递归包含子文件夹</summary>
        private bool _recursive = true;

        /// <summary>缩放方式</summary>
        private DownscaleMode _mode = DownscaleMode.Physical;

        /// <summary>扫描结果</summary>
        private readonly List<ScanItem> _results = new List<ScanItem>();

        private Vector2 _scroll;

        // ==================== 窗口入口 ====================

        /// <summary>打开窗口（菜单注册统一在 UIRMenuRegister 中）</summary>
        public static void Open()
        {
            var window = GetWindow<TextureDownscaleWindow>("图片降尺寸");
            window.minSize = new Vector2(520f, 480f);
            window.Show();
        }

        // ==================== 生命周期 ====================

        private void OnEnable()
        {
            LoadPrefs();
        }

        private void OnDisable()
        {
            SavePrefs();
        }

        // ==================== GUI ====================

        private void OnGUI()
        {
            DrawSettings();
            EditorGUILayout.Space();

            DrawActions();
            EditorGUILayout.Space();

            DrawResults();
        }

        /// <summary>绘制文件夹与参数设置</summary>
        private void DrawSettings()
        {
            EditorGUILayout.LabelField("设置", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _sourceFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                    new GUIContent("文件夹", "待扫描图片所在的文件夹"),
                    _sourceFolder, typeof(DefaultAsset), false);

                if (GUILayout.Button("选中", GUILayout.Width(60f)))
                {
                    var folder = GetSelectedFolderAsset();
                    if (folder != null)
                    {
                        _sourceFolder = folder;
                    }
                }
            }

            _threshold = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("扫描阈值", "最长边超过该值的图片会被列出"),
                _threshold));

            _target = IntPopup(new GUIContent("目标尺寸", "把图片等比缩到该尺寸内"), _target, TargetSizeOptions);
            _recursive = EditorGUILayout.Toggle(new GUIContent("包含子文件夹", "是否递归扫描子文件夹"), _recursive);
            _mode = (DownscaleMode)EditorGUILayout.EnumPopup(
                new GUIContent("缩放方式", "改 Max Size：非破坏，只改导入设置；物理缩图：改写源文件像素（仅 PNG/JPG）"),
                _mode);

            if (_mode == DownscaleMode.MaxSize)
            {
                EditorGUILayout.HelpBox("按“导入后尺寸”判断是否超阈值；仅修改纹理导入 Max Size，不改动源文件，引用不变；只影响默认平台设置（若某图有单独平台覆盖，需另行处理）。", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("物理缩图会改写源文件像素尺寸（仅支持 PNG/JPG，其它格式跳过），路径与 GUID 不变故引用保持；执行前自动备份原图，可撤销。此操作会降低源图质量且不可无损还原（仅能从备份还原）。", MessageType.Warning);
            }
        }

        /// <summary>绘制操作按钮</summary>
        private void DrawActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("扫描", GUILayout.Height(28f)))
                {
                    Scan();
                }

                using (new EditorGUI.DisabledScope(GetSelectedCount() == 0))
                {
                    if (GUILayout.Button("缩放选中", GUILayout.Height(28f)))
                    {
                        ApplySelected();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                // 撤销上次操作：仅当存在清单时可用
                using (new EditorGUI.DisabledScope(!File.Exists(GetManifestPath())))
                {
                    if (GUILayout.Button("撤销上次缩放", GUILayout.Height(24f)))
                    {
                        UndoLast();
                    }
                }
            }
        }

        /// <summary>绘制扫描结果列表（可逐项勾选）</summary>
        private void DrawResults()
        {
            EditorGUILayout.LabelField("结果", EditorStyles.boldLabel);

            if (_results.Count == 0)
            {
                EditorGUILayout.HelpBox("点击“扫描”列出最长边超过阈值的图片。", MessageType.Info);
                return;
            }

            // 顶部：统计 + 全选/全不选/反选
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"共 {_results.Count} 张，选中 {GetSelectedCount()} 张");

                if (GUILayout.Button("全选", GUILayout.Width(60f)))
                {
                    SetAllSelected(true);
                }

                if (GUILayout.Button("全不选", GUILayout.Width(60f)))
                {
                    SetAllSelected(false);
                }

                if (GUILayout.Button("反选", GUILayout.Width(60f)))
                {
                    foreach (ScanItem item in _results)
                    {
                        item.Selected = !item.Selected;
                    }
                }
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (ScanItem item in _results)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    item.Selected = EditorGUILayout.Toggle(item.Selected, GUILayout.Width(18f));

                    ComputeTargetSize(item.Width, item.Height, _target, out int nw, out int nh);
                    string sizeInfo = nw == item.Width && nh == item.Height
                        ? $"{item.Width}x{item.Height} (无需缩放)"
                        : $"{item.Width}x{item.Height} → {nw}x{nh}";

                    EditorGUILayout.LabelField(new GUIContent(item.Path, item.Path), GUILayout.MinWidth(200f));
                    EditorGUILayout.LabelField(sizeInfo, GUILayout.Width(180f));

                    if (GUILayout.Button("定位", GUILayout.Width(48f)))
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<Object>(item.Path);
                        if (obj != null)
                        {
                            EditorGUIUtility.PingObject(obj);
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // ==================== 扫描 ====================

        /// <summary>扫描文件夹，列出最长边超过阈值的图片</summary>
        private void Scan()
        {
            _results.Clear();

            string sourcePath = _sourceFolder != null ? AssetDatabase.GetAssetPath(_sourceFolder) : string.Empty;
            if (string.IsNullOrEmpty(sourcePath) || !AssetDatabase.IsValidFolder(sourcePath))
            {
                EditorUtility.DisplayDialog("图片降尺寸", "请指定有效的文件夹。", "确定");
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { sourcePath });
            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);

                    EditorUtility.DisplayProgressBar("图片降尺寸", $"扫描中… {i + 1}/{guids.Length}",
                        guids.Length > 0 ? (float)(i + 1) / guids.Length : 1f);

                    // 非递归时仅保留直接子文件
                    if (!_recursive)
                    {
                        string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
                        if (dir != sourcePath)
                        {
                            continue;
                        }
                    }

                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (texture == null)
                    {
                        continue;
                    }

                    // 以导入后的尺寸判断（即实际生效的尺寸）
                    if (Mathf.Max(texture.width, texture.height) > _threshold)
                    {
                        _results.Add(new ScanItem
                        {
                            Path = path,
                            Width = texture.width,
                            Height = texture.height,
                            Selected = true
                        });
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"[图片降尺寸] 扫描完成：{_results.Count} 张图片最长边超过 {_threshold}。");
        }

        // ==================== 应用（缩放） ====================

        /// <summary>把选中的图片 Max Size 设为目标尺寸</summary>
        private void ApplySelected()
        {
            var selected = new List<ScanItem>();
            foreach (ScanItem item in _results)
            {
                if (item.Selected)
                {
                    selected.Add(item);
                }
            }

            if (selected.Count == 0)
            {
                return;
            }

            string modeDesc = _mode == DownscaleMode.MaxSize ? "修改纹理 Max Size（不改源文件）" : "改写源文件像素尺寸（会先备份）";
            bool confirm = EditorUtility.DisplayDialog(
                "图片降尺寸",
                $"将把选中的 {selected.Count} 张图片等比缩到 {_target} 以内。\n方式：{modeDesc}\n引用保持不变。\n\n是否继续？",
                "执行", "取消");
            if (!confirm)
            {
                return;
            }

            if (_mode == DownscaleMode.MaxSize)
            {
                ApplyMaxSize(selected);
            }
            else
            {
                ApplyPhysical(selected);
            }

            // 重新扫描以刷新列表状态
            Scan();
        }

        /// <summary>方式一：修改纹理导入 Max Size（非破坏）</summary>
        private void ApplyMaxSize(List<ScanItem> selected)
        {
            var manifest = new DownscaleManifest
            {
                timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                mode = (int)DownscaleMode.MaxSize
            };

            int changed = 0;
            try
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    ScanItem item = selected[i];
                    EditorUtility.DisplayProgressBar("图片降尺寸", $"处理中… {i + 1}/{selected.Count}",
                        (float)(i + 1) / selected.Count);

                    var importer = AssetImporter.GetAtPath(item.Path) as TextureImporter;
                    if (importer == null)
                    {
                        continue;
                    }

                    // 已经不大于目标则跳过，不记录、不重导入
                    if (importer.maxTextureSize <= _target)
                    {
                        continue;
                    }

                    // 记录旧值以支持撤销
                    manifest.records.Add(new SizeRecord { path = item.Path, oldSize = importer.maxTextureSize });

                    importer.maxTextureSize = _target;
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            if (manifest.records.Count > 0)
            {
                SaveManifest(manifest);
            }

            Debug.Log($"[图片降尺寸] 完成(Max Size)：实际修改 {changed} 张（目标 {_target}）。");
        }

        /// <summary>方式二：物理缩图，改写源文件像素尺寸（仅 PNG/JPG），先备份以支持撤销</summary>
        private void ApplyPhysical(List<ScanItem> selected)
        {
            var manifest = new DownscaleManifest
            {
                timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                mode = (int)DownscaleMode.Physical,
                backupDir = Path.Combine(GetBackupRootDir(), System.DateTime.Now.ToString("yyyyMMdd_HHmmss"))
            };

            int changed = 0;
            int skipped = 0;
            var toReimport = new List<string>();
            try
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    ScanItem item = selected[i];
                    EditorUtility.DisplayProgressBar("图片降尺寸", $"处理中… {i + 1}/{selected.Count}",
                        (float)(i + 1) / selected.Count);

                    string ext = Path.GetExtension(item.Path).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                    {
                        Debug.LogWarning($"[图片降尺寸] 跳过(仅支持 PNG/JPG)：{item.Path}");
                        skipped++;
                        continue;
                    }

                    // 多图精灵表的子图矩形以像素记录，物理缩图会导致其错位，跳过以免破坏切图
                    var importer = AssetImporter.GetAtPath(item.Path) as TextureImporter;
                    if (importer != null && importer.spriteImportMode == SpriteImportMode.Multiple)
                    {
                        Debug.LogWarning($"[图片降尺寸] 跳过(多图精灵表，物理缩图会破坏切图)：{item.Path}");
                        skipped++;
                        continue;
                    }

                    // 先备份原图，再改写
                    if (!BackupFile(item.Path, manifest.backupDir))
                    {
                        Debug.LogError($"[图片降尺寸] 备份失败，跳过：{item.Path}");
                        skipped++;
                        continue;
                    }

                    if (ResizeTextureFile(item.Path, _target, out string error))
                    {
                        manifest.physicalFiles.Add(item.Path);
                        toReimport.Add(item.Path);
                        changed++;
                    }
                    else
                    {
                        Debug.LogWarning($"[图片降尺寸] 跳过：{item.Path}（{error}）");
                        skipped++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // 统一重导入被改写的文件
            foreach (string path in toReimport)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            AssetDatabase.Refresh();

            if (manifest.physicalFiles.Count > 0)
            {
                SaveManifest(manifest);
            }

            Debug.Log($"[图片降尺寸] 完成(物理缩图)：改写 {changed} 张，跳过 {skipped} 张，目标 {_target}，备份于 {manifest.backupDir}。");
        }

        // ==================== 撤销 ====================

        /// <summary>撤销上次缩放：按模式还原（Max Size 还原导入值；物理缩图从备份拷回）</summary>
        private void UndoLast()
        {
            DownscaleManifest manifest = LoadManifest();
            bool hasMaxSize = manifest != null && manifest.records.Count > 0;
            bool hasPhysical = manifest != null && manifest.physicalFiles.Count > 0;
            if (manifest == null || (!hasMaxSize && !hasPhysical))
            {
                EditorUtility.DisplayDialog("图片降尺寸", "没有可撤销的操作。", "确定");
                return;
            }

            if (manifest.mode == (int)DownscaleMode.Physical)
            {
                UndoPhysical(manifest);
            }
            else
            {
                UndoMaxSize(manifest);
            }

            // 删除清单，避免重复撤销
            try
            {
                File.Delete(GetManifestPath());
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[图片降尺寸] 删除撤销清单失败：{e.Message}");
            }
        }

        /// <summary>撤销 Max Size 修改：还原到旧值并重导入</summary>
        private void UndoMaxSize(DownscaleManifest manifest)
        {
            bool confirm = EditorUtility.DisplayDialog(
                "撤销上次缩放",
                $"将还原 {manifest.records.Count} 张图片的 Max Size 到修改前的值。\n\n是否继续？",
                "撤销", "取消");
            if (!confirm)
            {
                return;
            }

            try
            {
                for (int i = 0; i < manifest.records.Count; i++)
                {
                    SizeRecord record = manifest.records[i];
                    EditorUtility.DisplayProgressBar("图片降尺寸", $"撤销中… {i + 1}/{manifest.records.Count}",
                        (float)(i + 1) / manifest.records.Count);

                    var importer = AssetImporter.GetAtPath(record.path) as TextureImporter;
                    if (importer == null)
                    {
                        continue;
                    }

                    importer.maxTextureSize = record.oldSize;
                    importer.SaveAndReimport();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            Debug.Log("[图片降尺寸] 已撤销上次缩放(Max Size)。");
        }

        /// <summary>撤销物理缩图：从备份拷回源文件并重导入</summary>
        private void UndoPhysical(DownscaleManifest manifest)
        {
            bool confirm = EditorUtility.DisplayDialog(
                "撤销上次缩放",
                $"将从备份还原 {manifest.physicalFiles.Count} 张图片的源文件。\n备份目录: {manifest.backupDir}\n\n是否继续？",
                "撤销", "取消");
            if (!confirm)
            {
                return;
            }

            var toReimport = new List<string>();
            try
            {
                for (int i = 0; i < manifest.physicalFiles.Count; i++)
                {
                    string path = manifest.physicalFiles[i];
                    EditorUtility.DisplayProgressBar("图片降尺寸", $"撤销中… {i + 1}/{manifest.physicalFiles.Count}",
                        (float)(i + 1) / manifest.physicalFiles.Count);

                    string backupAbs = Path.Combine(manifest.backupDir, path);
                    if (!File.Exists(backupAbs))
                    {
                        Debug.LogWarning($"[图片降尺寸] 备份缺失，无法还原：{path}");
                        continue;
                    }

                    File.Copy(backupAbs, ToAbsolute(path), true);
                    toReimport.Add(path);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            foreach (string path in toReimport)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            AssetDatabase.Refresh();

            Debug.Log("[图片降尺寸] 已撤销上次缩放(物理缩图，已从备份还原)。");
        }

        // ==================== 辅助 ====================

        /// <summary>计算等比缩到 target 以内后的尺寸；不超过则原样返回</summary>
        private static void ComputeTargetSize(int width, int height, int target, out int newWidth, out int newHeight)
        {
            int maxSide = Mathf.Max(width, height);
            if (maxSide <= target)
            {
                newWidth = width;
                newHeight = height;
                return;
            }

            float scale = (float)target / maxSide;
            newWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            newHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
        }

        /// <summary>物理备份根目录（工程根目录下，与 Assets 同级，不会被 Unity 导入）</summary>
        private static string GetBackupRootDir()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, BackupRootName);
        }

        /// <summary>把 Assets 相对路径转为磁盘绝对路径</summary>
        private static string ToAbsolute(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, assetPath);
        }

        /// <summary>把源文件复制到备份目录（保留 Assets 相对结构），成功返回 true</summary>
        private static bool BackupFile(string assetPath, string backupDir)
        {
            try
            {
                string srcAbs = ToAbsolute(assetPath);
                if (!File.Exists(srcAbs))
                {
                    return false;
                }

                string destAbs = Path.Combine(backupDir, assetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destAbs));
                File.Copy(srcAbs, destAbs, true);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[图片降尺寸] 备份异常：{e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 物理缩图：解码源文件到原始分辨率，CPU 双线性等比重采样到目标尺寸内，再编码写回源文件。
        /// 仅支持 PNG/JPG。不改变路径与 .meta，故引用不变。
        /// </summary>
        private static bool ResizeTextureFile(string assetPath, int target, out string error)
        {
            error = null;
            Texture2D src = null;
            Texture2D dst = null;
            try
            {
                string abs = ToAbsolute(assetPath);
                byte[] bytes = File.ReadAllBytes(abs);

                src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!src.LoadImage(bytes))
                {
                    error = "解码失败(仅支持 PNG/JPG)";
                    return false;
                }

                int w = src.width;
                int h = src.height;
                ComputeTargetSize(w, h, target, out int nw, out int nh);
                if (nw == w && nh == h)
                {
                    // 无需缩放
                    error = "原始尺寸已不超过目标";
                    return false;
                }

                Color32[] srcPixels = src.GetPixels32();
                Color32[] dstPixels = ResampleBilinear(srcPixels, w, h, nw, nh);

                dst = new Texture2D(nw, nh, TextureFormat.RGBA32, false);
                dst.SetPixels32(dstPixels);
                dst.Apply();

                string ext = Path.GetExtension(assetPath).ToLowerInvariant();
                byte[] outBytes = (ext == ".jpg" || ext == ".jpeg") ? dst.EncodeToJPG(95) : dst.EncodeToPNG();
                File.WriteAllBytes(abs, outBytes);
                return true;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                if (src != null)
                {
                    Object.DestroyImmediate(src);
                }

                if (dst != null)
                {
                    Object.DestroyImmediate(dst);
                }
            }
        }

        /// <summary>CPU 双线性重采样（在原始像素字节上直接计算，避免色彩空间转换导致偏色）</summary>
        private static Color32[] ResampleBilinear(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            float rx = (float)sw / dw;
            float ry = (float)sh / dh;

            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * ry - 0.5f;
                int y0 = Mathf.FloorToInt(fy);
                float wy = fy - y0;
                int y0c = Mathf.Clamp(y0, 0, sh - 1);
                int y1c = Mathf.Clamp(y0 + 1, 0, sh - 1);

                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * rx - 0.5f;
                    int x0 = Mathf.FloorToInt(fx);
                    float wx = fx - x0;
                    int x0c = Mathf.Clamp(x0, 0, sw - 1);
                    int x1c = Mathf.Clamp(x0 + 1, 0, sw - 1);

                    Color32 c00 = src[y0c * sw + x0c];
                    Color32 c10 = src[y0c * sw + x1c];
                    Color32 c01 = src[y1c * sw + x0c];
                    Color32 c11 = src[y1c * sw + x1c];

                    dst[y * dw + x] = LerpColor32(LerpColor32(c00, c10, wx), LerpColor32(c01, c11, wx), wy);
                }
            }

            return dst;
        }

        /// <summary>按 t 在两个 Color32 间线性插值（逐通道）</summary>
        private static Color32 LerpColor32(Color32 a, Color32 b, float t)
        {
            return new Color32(
                (byte)Mathf.RoundToInt(a.r + (b.r - a.r) * t),
                (byte)Mathf.RoundToInt(a.g + (b.g - a.g) * t),
                (byte)Mathf.RoundToInt(a.b + (b.b - a.b) * t),
                (byte)Mathf.RoundToInt(a.a + (b.a - a.a) * t));
        }

        /// <summary>当前选中数量</summary>
        private int GetSelectedCount()
        {
            int count = 0;
            foreach (ScanItem item in _results)
            {
                if (item.Selected)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>设置所有项的选中状态</summary>
        private void SetAllSelected(bool value)
        {
            foreach (ScanItem item in _results)
            {
                item.Selected = value;
            }
        }

        /// <summary>获取当前 Project 视图选中的文件夹资产（若选中的是文件夹）</summary>
        private static DefaultAsset GetSelectedFolderAsset()
        {
            var active = Selection.activeObject as DefaultAsset;
            if (active != null && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(active)))
            {
                return active;
            }

            return null;
        }

        /// <summary>IntPopup 辅助：把候选值数组渲染为下拉框</summary>
        private static int IntPopup(GUIContent label, int value, int[] options)
        {
            var display = new GUIContent[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                display[i] = new GUIContent(options[i].ToString());
            }

            return EditorGUILayout.IntPopup(label, value, display, options);
        }

        // ==================== 撤销清单读写 ====================

        private static string GetManifestPath()
        {
            return Path.Combine(GetBackupRootDir(), ManifestFileName);
        }

        private static void SaveManifest(DownscaleManifest manifest)
        {
            try
            {
                string dir = Path.GetDirectoryName(GetManifestPath());
                Directory.CreateDirectory(dir);
                File.WriteAllText(GetManifestPath(), JsonUtility.ToJson(manifest, true));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[图片降尺寸] 保存撤销清单失败：{e.Message}");
            }
        }

        private static DownscaleManifest LoadManifest()
        {
            string path = GetManifestPath();
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<DownscaleManifest>(File.ReadAllText(path));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[图片降尺寸] 读取撤销清单失败：{e.Message}");
                return null;
            }
        }

        // ==================== EditorPrefs 读写 ====================

        private void LoadPrefs()
        {
            string sourcePath = EditorPrefs.GetString(PrefKeySource, string.Empty);
            if (!string.IsNullOrEmpty(sourcePath))
            {
                _sourceFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(sourcePath);
            }

            _threshold = Mathf.Max(1, EditorPrefs.GetInt(PrefKeyThreshold, 1024));
            _target = EditorPrefs.GetInt(PrefKeyTarget, 1024);
            _recursive = EditorPrefs.GetBool(PrefKeyRecursive, true);
            _mode = (DownscaleMode)EditorPrefs.GetInt(PrefKeyMode, (int)DownscaleMode.Physical);
        }

        private void SavePrefs()
        {
            EditorPrefs.SetString(PrefKeySource, _sourceFolder != null ? AssetDatabase.GetAssetPath(_sourceFolder) : string.Empty);
            EditorPrefs.SetInt(PrefKeyThreshold, _threshold);
            EditorPrefs.SetInt(PrefKeyTarget, _target);
            EditorPrefs.SetBool(PrefKeyRecursive, _recursive);
            EditorPrefs.SetInt(PrefKeyMode, (int)_mode);
        }

        // ==================== 数据结构 ====================

        /// <summary>单条扫描结果</summary>
        private class ScanItem
        {
            public string Path;
            public int Width;
            public int Height;
            public bool Selected;
        }

        /// <summary>单条尺寸修改记录（用于撤销）</summary>
        [System.Serializable]
        private class SizeRecord
        {
            public string path;
            public int oldSize;
        }

        /// <summary>一次缩放操作的清单，持久化以支持撤销</summary>
        [System.Serializable]
        private class DownscaleManifest
        {
            public string timestamp;

            /// <summary>缩放方式，见 DownscaleMode</summary>
            public int mode;

            /// <summary>物理缩图模式的备份目录</summary>
            public string backupDir;

            /// <summary>Max Size 模式：每张图的旧值</summary>
            public List<SizeRecord> records = new List<SizeRecord>();

            /// <summary>物理缩图模式：被改写的源文件资源路径</summary>
            public List<string> physicalFiles = new List<string>();
        }
    }
}
