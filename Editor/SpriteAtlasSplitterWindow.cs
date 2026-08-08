using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace UIR.EditorTools
{
    /// <summary>
    /// SpriteAtlas 拆分工具
    /// 选择一个源文件夹，根据文件夹内的图片，在指定输出位置自动创建一个或多个 SpriteAtlas。
    /// 通过“单个图集最大尺寸”与“单个 Atlas 最大页数”约束容量：
    ///   使用 MaxRects 矩形装箱算法预先估算，一个 Atlas 放不下就拆成多个；
    ///   同时把图片按分组移动到各自的子文件夹，Atlas 直接引用对应子文件夹；
    ///   Atlas 与子文件夹同名，序号递增。
    /// 移动图片使用 AssetDatabase.MoveAsset，保留 GUID/fileID，原有引用（Prefab 等）保持不变。
    /// 采用 IMGUI 实现，仅依赖 Unity 内置 API。
    /// </summary>
    public class SpriteAtlasSplitterWindow : EditorWindow
    {
        // ==================== EditorPrefs 键 ====================

        private const string PrefKeySource = "SpriteAtlasSplitter.SourceFolder";
        private const string PrefKeyOutput = "SpriteAtlasSplitter.OutputFolder";
        private const string PrefKeyMaxSize = "SpriteAtlasSplitter.MaxSize";
        private const string PrefKeyMaxPage = "SpriteAtlasSplitter.MaxPageCount";
        private const string PrefKeyPadding = "SpriteAtlasSplitter.Padding";
        private const string PrefKeyPrefix = "SpriteAtlasSplitter.NamePrefix";
        private const string PrefKeyStartIndex = "SpriteAtlasSplitter.StartIndex";
        private const string PrefKeyRecursive = "SpriteAtlasSplitter.Recursive";
        private const string PrefKeyPrecise = "SpriteAtlasSplitter.Precise";
        private const string PrefKeyBackup = "SpriteAtlasSplitter.Backup";

        /// <summary>精确模式使用的临时 Atlas 名（试打包用，用完即删）</summary>
        private const string TempAtlasName = "__SpriteAtlasSplitterTemp";

        /// <summary>备份与撤销清单的根目录名（位于工程根目录，Assets 同级）</summary>
        private const string BackupRootName = "SpriteAtlasSplitterBackups";

        /// <summary>最近一次操作清单文件名（用于撤销）</summary>
        private const string ManifestFileName = "last_operation.json";

        // ==================== 可选项 ====================

        /// <summary>最大尺寸候选（2 的幂）</summary>
        private static readonly int[] MaxSizeOptions = { 256, 512, 1024, 2048, 4096, 8192 };

        /// <summary>SpriteAtlas 支持的 padding 候选</summary>
        private static readonly int[] PaddingOptions = { 2, 4, 8 };

        // ==================== 运行时字段 ====================

        [Tooltip("待拆分图片所在的源文件夹")]
        private DefaultAsset _sourceFolder;

        [Tooltip("生成的 SpriteAtlas 与图片子文件夹所在的输出文件夹")]
        private DefaultAsset _outputFolder;

        /// <summary>单个图集（页）的最大尺寸</summary>
        private int _maxSize = 1024;

        /// <summary>单个 Atlas 允许的最大页数（图集数量）</summary>
        private int _maxPageCount = 1;

        /// <summary>精灵之间的间距，同步写入 Atlas 打包设置</summary>
        private int _padding = 4;

        /// <summary>生成的 Atlas / 子文件夹名前缀</summary>
        private string _namePrefix = "Atlas";

        /// <summary>起始序号</summary>
        private int _startIndex = 1;

        /// <summary>是否递归包含子文件夹内的图片</summary>
        private bool _recursive = true;

        /// <summary>是否使用精确模式（调用 Unity 真实打包器判定分组，结果精确但更慢）</summary>
        private bool _preciseMode;

        /// <summary>执行前是否把原图复制到备份目录作为灾备副本</summary>
        private bool _backup = true;

        /// <summary>GetPreviewTextures 反射缓存（Unity 未公开该 API，需反射调用）</summary>
        private static MethodInfo _getPreviewTexturesMethod;

        private Vector2 _scroll;

        /// <summary>预览结果缓存文本，避免每帧重算</summary>
        private string _previewText = string.Empty;

        // ==================== 窗口入口 ====================

        /// <summary>打开窗口（菜单注册统一在 UIRMenuRegister 中）</summary>
        public static void Open()
        {
            var window = GetWindow<SpriteAtlasSplitterWindow>("SpriteAtlas 拆分");
            window.minSize = new Vector2(460f, 460f);
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
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawFolders();
            EditorGUILayout.Space();

            DrawSettings();
            EditorGUILayout.Space();

            DrawActions();
            EditorGUILayout.Space();

            DrawPreview();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>绘制源/输出文件夹选择</summary>
        private void DrawFolders()
        {
            EditorGUILayout.LabelField("文件夹", EditorStyles.boldLabel);

            _sourceFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                new GUIContent("源文件夹", "待拆分图片所在的文件夹"),
                _sourceFolder, typeof(DefaultAsset), false);

            using (new EditorGUILayout.HorizontalScope())
            {
                _outputFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                    new GUIContent("输出文件夹", "生成的 SpriteAtlas 与图片子文件夹所在的文件夹；留空则使用源文件夹"),
                    _outputFolder, typeof(DefaultAsset), false);

                // 浏览/新建输出文件夹：可选择工程内已有目录，或在对话框中新建
                if (GUILayout.Button("浏览/新建", GUILayout.Width(72f)))
                {
                    BrowseOutputFolder();
                }
            }

            // 提供“使用当前 Project 选中文件夹”的快捷方式
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("源=当前选中"))
                {
                    var folder = GetSelectedFolderAsset();
                    if (folder != null)
                    {
                        _sourceFolder = folder;
                    }
                }

                if (GUILayout.Button("输出=当前选中"))
                {
                    var folder = GetSelectedFolderAsset();
                    if (folder != null)
                    {
                        _outputFolder = folder;
                    }
                }
            }
        }

        /// <summary>绘制拆分参数</summary>
        private void DrawSettings()
        {
            EditorGUILayout.LabelField("参数", EditorStyles.boldLabel);

            _maxSize = IntPopup(new GUIContent("最大尺寸", "单个图集（页）的最大宽/高"), _maxSize, MaxSizeOptions);
            _maxPageCount = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("最大页数(图集数量)", "单个 Atlas 允许的最大页数；例如 1 表示一个 Atlas 只能有一个图集"),
                _maxPageCount));
            _padding = IntPopup(new GUIContent("间距 Padding", "精灵之间的间距，同步写入 Atlas 打包设置"), _padding, PaddingOptions);
            _namePrefix = EditorGUILayout.TextField(
                new GUIContent("图集名字", "生成的 SpriteAtlas 与子文件夹名字；多个图集时按“名字_序号”递增"),
                _namePrefix);
            _startIndex = EditorGUILayout.IntField(new GUIContent("起始序号", "生成名称的起始序号，序号递增"), _startIndex);
            _recursive = EditorGUILayout.Toggle(new GUIContent("包含子文件夹", "是否递归收集子文件夹内的图片"), _recursive);
            _preciseMode = EditorGUILayout.Toggle(
                new GUIContent("精确模式", "开启后调用 Unity 真实打包器判定分组，结果与引擎完全一致，但更慢（会反复试打包）"),
                _preciseMode);
            if (_preciseMode)
            {
                EditorGUILayout.HelpBox("精确模式会用真实打包器反复试打包来确定拆分点，图片较多时耗时明显。", MessageType.Info);
            }

            _backup = EditorGUILayout.Toggle(
                new GUIContent("执行前备份", "执行前把原图及其 .meta 复制到备份目录，作为灾备副本"),
                _backup);
        }

        /// <summary>绘制操作按钮</summary>
        private void DrawActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("预览分组", GUILayout.Height(28f)))
                {
                    RefreshPreview();
                }

                if (GUILayout.Button("执行拆分", GUILayout.Height(28f)))
                {
                    Execute();
                }
            }

            // 撤销上次拆分：仅当存在清单时可用
            using (new EditorGUI.DisabledScope(!File.Exists(GetManifestPath())))
            {
                if (GUILayout.Button("撤销上次拆分", GUILayout.Height(24f)))
                {
                    UndoLast();
                }
            }
        }

        /// <summary>绘制预览文本</summary>
        private void DrawPreview()
        {
            EditorGUILayout.LabelField("预览", EditorStyles.boldLabel);

            if (string.IsNullOrEmpty(_previewText))
            {
                EditorGUILayout.HelpBox("点击“预览分组”查看将生成多少个 Atlas 以及每个 Atlas 的精灵数量。", MessageType.Info);
                return;
            }

            EditorGUILayout.TextArea(_previewText, GUILayout.MinHeight(120f));
        }

        // ==================== 预览 ====================

        /// <summary>收集图片并计算分组，生成预览文本</summary>
        private void RefreshPreview()
        {
            if (!ValidateFolders(out string sourcePath, out string outputPath))
            {
                return;
            }

            List<TextureInfo> textures = GatherTextures(sourcePath);
            if (textures.Count == 0)
            {
                _previewText = "源文件夹内未找到可用图片(Texture2D)。";
                return;
            }

            List<AtlasGroup> groups = ComputeGroups(textures, outputPath);

            var sb = new StringBuilder();
            sb.AppendLine($"模式: {(_preciseMode ? "精确(真实打包器)" : "快速(MaxRects 估算)")}");
            sb.AppendLine($"图片总数: {textures.Count}");
            sb.AppendLine($"最大尺寸: {_maxSize}，最大页数: {_maxPageCount}，间距: {_padding}");
            sb.AppendLine($"将生成 {groups.Count} 个 SpriteAtlas：");
            sb.AppendLine();

            for (int i = 0; i < groups.Count; i++)
            {
                AtlasGroup g = groups[i];
                string oversizeTag = g.Oversize ? "  [警告:单图超出最大尺寸，将单独成组]" : string.Empty;
                sb.AppendLine($"  #{i + 1}  精灵 {g.Items.Count} 个，页数 {Mathf.Max(1, g.PageCount)}{oversizeTag}");
            }

            _previewText = sb.ToString();
        }

        // ==================== 执行 ====================

        /// <summary>执行拆分：移动图片到子文件夹并创建对应 SpriteAtlas</summary>
        private void Execute()
        {
            if (!ValidateFolders(out string sourcePath, out string outputPath))
            {
                return;
            }

            List<TextureInfo> textures = GatherTextures(sourcePath);
            if (textures.Count == 0)
            {
                EditorUtility.DisplayDialog("SpriteAtlas 拆分", "源文件夹内未找到可用图片(Texture2D)。", "确定");
                return;
            }

            List<AtlasGroup> groups = ComputeGroups(textures, outputPath);

            bool confirm = EditorUtility.DisplayDialog(
                "SpriteAtlas 拆分",
                $"将把 {textures.Count} 张图片拆分为 {groups.Count} 个 SpriteAtlas，" +
                $"并把图片移动到对应子文件夹（引用保持不变）。\n\n输出位置: {outputPath}\n\n是否继续？",
                "执行", "取消");
            if (!confirm)
            {
                return;
            }

            // 准备撤销清单，记录本次全部操作
            var manifest = new SplitManifest
            {
                timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss")
            };

            // 执行前备份原图（可选）
            if (_backup)
            {
                manifest.backupDir = BackupTextures(textures, manifest.timestamp);
            }

            // 第一步：为每个分组分配唯一名称并创建子文件夹
            // 注意：文件夹创建必须在批处理(StartAssetEditing)之外完成，否则新目录尚未登记进
            // AssetDatabase，紧接着的 MoveAsset 会报“Parent directory is not in asset database”。
            var plans = new List<AtlasPlan>();
            int index = _startIndex;
            foreach (AtlasGroup group in groups)
            {
                // 跳过已存在的名称，保证子文件夹与 Atlas 名称均可用
                index = FindAvailableIndex(outputPath, index, out string name);
                string subFolderPath = outputPath + "/" + name;
                EnsureFolder(outputPath, name);
                // 记录新建的子文件夹，供撤销时清理
                manifest.createdFolders.Add(subFolderPath);

                plans.Add(new AtlasPlan { Name = name, SubFolderPath = subFolderPath, Items = group.Items });
                index++;
            }

            // 第二步：批量移动图片到各自子文件夹（子文件夹已在 AssetDatabase 中）
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (AtlasPlan plan in plans)
                {
                    // 批处理未提交时 GenerateUniqueAssetPath 看不到刚移入的文件，
                    // 故自行维护本子文件夹内已用文件名，避免重名冲突
                    var usedNames = new HashSet<string>();
                    foreach (TextureInfo tex in plan.Items)
                    {
                        string targetPath = MakeUniqueTargetPath(plan.SubFolderPath, Path.GetFileName(tex.Path), usedNames);
                        // MoveAsset 保留 GUID，原有引用不受影响
                        string error = AssetDatabase.MoveAsset(tex.Path, targetPath);
                        if (!string.IsNullOrEmpty(error))
                        {
                            Debug.LogError($"[SpriteAtlas 拆分] 移动失败: {tex.Path} -> {targetPath}\n{error}");
                        }
                        else
                        {
                            // 记录移动，撤销时反向移回原位
                            manifest.moves.Add(new MoveRecord { from = tex.Path, to = targetPath });
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            // 第三步：为每个子文件夹创建 SpriteAtlas 并引用该文件夹
            foreach (AtlasPlan plan in plans)
            {
                CreateAtlas(outputPath, plan.Name, plan.SubFolderPath);
                manifest.createdAtlases.Add(outputPath + "/" + plan.Name + ".spriteatlas");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 保存撤销清单，供“撤销上次拆分”使用
            SaveManifest(manifest);

            Debug.Log($"[SpriteAtlas 拆分] 完成：生成 {plans.Count} 个 SpriteAtlas，输出于 {outputPath}" +
                      (string.IsNullOrEmpty(manifest.backupDir) ? string.Empty : $"；备份目录: {manifest.backupDir}"));

            // 选中输出文件夹，便于查看结果
            var outObj = AssetDatabase.LoadAssetAtPath<Object>(outputPath);
            if (outObj != null)
            {
                Selection.activeObject = outObj;
                EditorGUIUtility.PingObject(outObj);
            }

            RefreshPreview();
        }

        /// <summary>创建单个 SpriteAtlas，并引用指定图片子文件夹</summary>
        private void CreateAtlas(string outputPath, string name, string subFolderPath)
        {
            string atlasPath = outputPath + "/" + name + ".spriteatlas";
            SpriteAtlas atlas = CreateConfiguredAtlas(atlasPath);

            // 直接引用子文件夹：文件夹内所有精灵都会被打进该 Atlas，后续增删图片自动生效
            var folderObj = AssetDatabase.LoadAssetAtPath<Object>(subFolderPath);
            if (folderObj != null)
            {
                atlas.Add(new[] { folderObj });
            }

            EditorUtility.SetDirty(atlas);
        }

        /// <summary>在指定路径创建并写入统一设置的 SpriteAtlas（正式图集与精确模式临时图集共用）</summary>
        private SpriteAtlas CreateConfiguredAtlas(string atlasPath)
        {
            var atlas = new SpriteAtlas();

            // 打包设置：关闭旋转，间距与用户配置一致
            var packing = new SpriteAtlasPackingSettings
            {
                blockOffset = 1,
                enableRotation = false,
                enableTightPacking = false,
                padding = _padding
            };
            atlas.SetPackingSettings(packing);

            // 纹理设置：默认不生成 Mipmap、不可读
            var textureSettings = new SpriteAtlasTextureSettings
            {
                readable = false,
                generateMipMaps = false,
                sRGB = true,
                filterMode = FilterMode.Bilinear
            };
            atlas.SetTextureSettings(textureSettings);

            // 平台设置：限制最大尺寸，保证不超过用户设定
            var platformSettings = new TextureImporterPlatformSettings
            {
                maxTextureSize = _maxSize,
                format = TextureImporterFormat.Automatic,
                textureCompression = TextureImporterCompression.Compressed
            };
            atlas.SetPlatformSettings(platformSettings);

            atlas.SetIncludeInBuild(true);

            AssetDatabase.CreateAsset(atlas, atlasPath);
            return atlas;
        }

        // ==================== 装箱分组 ====================

        /// <summary>根据当前模式选择分组算法：精确模式用真实打包器，否则用 MaxRects 估算</summary>
        private List<AtlasGroup> ComputeGroups(List<TextureInfo> textures, string outputPath)
        {
            return _preciseMode ? BuildGroupsPrecise(textures, outputPath) : BuildGroups(textures);
        }

        /// <summary>
        /// 使用 MaxRects 装箱算法把图片分组：
        /// 每个 Atlas 最多 _maxPageCount 页，每页 _maxSize×_maxSize；放不下则开新 Atlas。
        /// 图片按较长边降序排列（First-Fit-Decreasing），提高装箱率。
        /// </summary>
        private List<AtlasGroup> BuildGroups(List<TextureInfo> textures)
        {
            // 副本排序，避免修改入参顺序
            var sorted = new List<TextureInfo>(textures);
            sorted.Sort((a, b) =>
            {
                int aMax = Mathf.Max(a.Width, a.Height);
                int bMax = Mathf.Max(b.Width, b.Height);
                if (aMax != bMax)
                {
                    return bMax.CompareTo(aMax);
                }

                return (b.Width * b.Height).CompareTo(a.Width * a.Height);
            });

            var groups = new List<AtlasGroup>();
            var current = new AtlasGroup();
            groups.Add(current);

            foreach (TextureInfo tex in sorted)
            {
                // 含间距后的占位尺寸
                int w = tex.Width + _padding;
                int h = tex.Height + _padding;

                // 单图（含间距）超过一页最大尺寸，无法被任何页容纳，单独成组
                bool oversize = w > _maxSize || h > _maxSize;
                if (oversize)
                {
                    if (current.Items.Count > 0)
                    {
                        current = new AtlasGroup();
                        groups.Add(current);
                    }

                    current.Oversize = true;
                    current.Items.Add(tex);

                    // 结束当前超大组，后续图片进入新组
                    current = new AtlasGroup();
                    groups.Add(current);
                    continue;
                }

                if (TryPlaceInGroup(current, w, h))
                {
                    current.Items.Add(tex);
                    continue;
                }

                // 当前 Atlas 放不下，开新 Atlas
                current = new AtlasGroup();
                groups.Add(current);
                if (!TryPlaceInGroup(current, w, h))
                {
                    // 理论上不会发生（非超大图必然能放进新页），兜底处理
                    current.Oversize = true;
                    current.Items.Add(tex);
                    current = new AtlasGroup();
                    groups.Add(current);
                }
                else
                {
                    current.Items.Add(tex);
                }
            }

            groups.RemoveAll(g => g.Items.Count == 0);
            return groups;
        }

        /// <summary>尝试把尺寸为 w×h 的矩形放进分组：先试已有页，页数未满则开新页</summary>
        private bool TryPlaceInGroup(AtlasGroup group, int w, int h)
        {
            foreach (MaxRectsBin page in group.Pages)
            {
                if (page.Insert(w, h))
                {
                    return true;
                }
            }

            if (group.Pages.Count < _maxPageCount)
            {
                var bin = new MaxRectsBin(_maxSize, _maxSize);
                if (bin.Insert(w, h))
                {
                    group.Pages.Add(bin);
                    return true;
                }
            }

            return false;
        }

        // ==================== 精确装箱分组（真实打包器） ====================

        /// <summary>
        /// 精确模式分组：借助 Unity 真实打包器判定拆分点。
        /// 原理：把图片按较长边降序排列后，页数随图片数量单调不减，
        /// 因此对“前缀数量”做二分，找出打包后不超过 _maxPageCount 页的最大数量，切成一组；
        /// 反复处理剩余图片，直到分完。结果与引擎实际打包一致。
        /// 试打包引用原图（不移动），确定分组后再走正式流程。
        /// </summary>
        private List<AtlasGroup> BuildGroupsPrecise(List<TextureInfo> textures, string outputPath)
        {
            var sorted = new List<TextureInfo>(textures);
            sorted.Sort((a, b) =>
            {
                int aMax = Mathf.Max(a.Width, a.Height);
                int bMax = Mathf.Max(b.Width, b.Height);
                if (aMax != bMax)
                {
                    return bMax.CompareTo(aMax);
                }

                return (b.Width * b.Height).CompareTo(a.Width * a.Height);
            });

            var groups = new List<AtlasGroup>();

            // 创建试打包用的临时 Atlas，用完即删
            string tempPath = AssetDatabase.GenerateUniqueAssetPath(outputPath + "/" + TempAtlasName + ".spriteatlas");
            SpriteAtlas tempAtlas = CreateConfiguredAtlas(tempPath);

            try
            {
                int total = sorted.Count;
                var remaining = new List<TextureInfo>(sorted);

                while (remaining.Count > 0)
                {
                    int processed = total - remaining.Count;
                    EditorUtility.DisplayProgressBar(
                        "SpriteAtlas 拆分（精确模式）",
                        $"正在试打包判定分组… {processed}/{total}",
                        total > 0 ? (float)processed / total : 1f);

                    int cut = FindMaxFit(tempAtlas, remaining, out int pagesAtCut);
                    if (cut <= 0)
                    {
                        cut = 1;
                    }

                    var group = new AtlasGroup
                    {
                        // 记录真实打包得到的页数
                        PageCountOverride = Mathf.Max(1, pagesAtCut),
                        // 单图打包后仍超页视为超大
                        Oversize = cut == 1 && pagesAtCut > _maxPageCount
                    };
                    group.Items.AddRange(remaining.GetRange(0, cut));
                    groups.Add(group);

                    remaining.RemoveRange(0, cut);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.DeleteAsset(tempPath);
            }

            return groups;
        }

        /// <summary>
        /// 对 remaining 的前缀做二分，返回打包后不超过 _maxPageCount 页的最大数量。
        /// pagesAtCut 输出该数量对应的实际页数。
        /// </summary>
        private int FindMaxFit(SpriteAtlas tempAtlas, List<TextureInfo> remaining, out int pagesAtCut)
        {
            int n = remaining.Count;

            // 先试全部：常见情况（全部能进一个 Atlas）只需一次打包
            int pagesAll = PackAndCountPages(tempAtlas, remaining, n);
            if (pagesAll <= _maxPageCount)
            {
                pagesAtCut = pagesAll;
                return n;
            }

            // 再试单张：若单张就超页，说明是超大图，单独成组
            int pages1 = PackAndCountPages(tempAtlas, remaining, 1);
            if (pages1 > _maxPageCount)
            {
                pagesAtCut = pages1;
                return 1;
            }

            // 在 [2, n-1] 间二分，找不超页的最大前缀数量
            int ans = 1;
            int ansPages = pages1;
            int lo = 2;
            int hi = n - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int pages = PackAndCountPages(tempAtlas, remaining, mid);
                if (pages <= _maxPageCount)
                {
                    ans = mid;
                    ansPages = pages;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            pagesAtCut = ansPages;
            return ans;
        }

        /// <summary>把 remaining 的前 count 张图放进临时 Atlas 实打包，返回生成的页数</summary>
        private int PackAndCountPages(SpriteAtlas tempAtlas, List<TextureInfo> remaining, int count)
        {
            // 清空上次的 packables
            Object[] packed = tempAtlas.GetPackables();
            if (packed != null && packed.Length > 0)
            {
                tempAtlas.Remove(packed);
            }

            // 加入本次要测试的图片（引用原图，不移动）
            var objs = new Object[count];
            for (int i = 0; i < count; i++)
            {
                objs[i] = AssetDatabase.LoadAssetAtPath<Object>(remaining[i].Path);
            }

            tempAtlas.Add(objs);
            EditorUtility.SetDirty(tempAtlas);

            // 调用 Unity 真实打包器
            SpriteAtlasUtility.PackAtlases(new[] { tempAtlas }, EditorUserBuildSettings.activeBuildTarget, false);

            Texture2D[] previews = GetPreviewTextures(tempAtlas);
            return previews != null ? previews.Length : 0;
        }

        /// <summary>反射调用 Unity 内部 GetPreviewTextures，读取图集实际生成的页贴图</summary>
        private static Texture2D[] GetPreviewTextures(SpriteAtlas atlas)
        {
            if (_getPreviewTexturesMethod == null)
            {
                _getPreviewTexturesMethod = typeof(SpriteAtlasExtensions).GetMethod(
                    "GetPreviewTextures",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (_getPreviewTexturesMethod == null)
            {
                return null;
            }

            return _getPreviewTexturesMethod.Invoke(null, new object[] { atlas }) as Texture2D[];
        }

        // ==================== 图片收集 ====================

        /// <summary>收集源文件夹内可用的 Texture2D 图片信息</summary>
        private List<TextureInfo> GatherTextures(string sourcePath)
        {
            var result = new List<TextureInfo>();
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { sourcePath });

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // 非递归时，仅保留源文件夹直接子文件
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

                result.Add(new TextureInfo
                {
                    Path = path,
                    // 使用导入后的尺寸，即打包器实际使用的尺寸
                    Width = texture.width,
                    Height = texture.height
                });
            }

            return result;
        }

        // ==================== 校验与辅助 ====================

        /// <summary>校验源/输出文件夹是否有效；输出文件夹为空时回退为源文件夹</summary>
        private bool ValidateFolders(out string sourcePath, out string outputPath)
        {
            sourcePath = _sourceFolder != null ? AssetDatabase.GetAssetPath(_sourceFolder) : string.Empty;
            outputPath = _outputFolder != null ? AssetDatabase.GetAssetPath(_outputFolder) : string.Empty;

            if (string.IsNullOrEmpty(sourcePath) || !AssetDatabase.IsValidFolder(sourcePath))
            {
                EditorUtility.DisplayDialog("SpriteAtlas 拆分", "请指定有效的源文件夹。", "确定");
                return false;
            }

            // 输出文件夹留空则使用源文件夹
            if (string.IsNullOrEmpty(outputPath) || !AssetDatabase.IsValidFolder(outputPath))
            {
                outputPath = sourcePath;
            }

            if (string.IsNullOrEmpty(_namePrefix))
            {
                EditorUtility.DisplayDialog("SpriteAtlas 拆分", "请填写名称前缀。", "确定");
                return false;
            }

            return true;
        }

        /// <summary>从 startIndex 起查找一个子文件夹与 .spriteatlas 均不存在的可用序号</summary>
        private int FindAvailableIndex(string outputPath, int startIndex, out string name)
        {
            int index = startIndex;
            while (true)
            {
                name = _namePrefix + "_" + index;
                bool folderExists = AssetDatabase.IsValidFolder(outputPath + "/" + name);
                bool atlasExists = File.Exists(outputPath + "/" + name + ".spriteatlas");
                if (!folderExists && !atlasExists)
                {
                    return index;
                }

                index++;
            }
        }

        /// <summary>
        /// 在目标子文件夹内生成不重名的资源路径。
        /// 同时考虑本批次已用过的名字(usedNames)与磁盘已存在的文件，避免批处理未提交导致的冲突。
        /// </summary>
        private static string MakeUniqueTargetPath(string subFolderPath, string fileName, HashSet<string> usedNames)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);

            string candidate = fileName;
            int n = 1;
            while (usedNames.Contains(candidate.ToLowerInvariant()) ||
                   File.Exists(ToAbsolute(subFolderPath + "/" + candidate)))
            {
                candidate = $"{nameNoExt}_{n}{ext}";
                n++;
            }

            usedNames.Add(candidate.ToLowerInvariant());
            return subFolderPath + "/" + candidate;
        }

        /// <summary>确保 parent 下存在名为 folderName 的子目录</summary>
        private static void EnsureFolder(string parent, string folderName)
        {
            string full = parent + "/" + folderName;
            if (!AssetDatabase.IsValidFolder(full))
            {
                AssetDatabase.CreateFolder(parent, folderName);
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

        // ==================== 输出文件夹浏览/新建 ====================

        /// <summary>弹出系统对话框选择/新建输出文件夹，并转换为工程内 Assets 相对路径</summary>
        private void BrowseOutputFolder()
        {
            string abs = EditorUtility.OpenFolderPanel("选择/新建输出文件夹", Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(abs))
            {
                return;
            }

            abs = abs.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');

            // 必须位于工程 Assets 目录内
            if (abs != dataPath && !abs.StartsWith(dataPath + "/"))
            {
                EditorUtility.DisplayDialog("SpriteAtlas 拆分", "输出文件夹必须位于工程 Assets 目录内。", "确定");
                return;
            }

            string relative = abs == dataPath ? "Assets" : "Assets" + abs.Substring(dataPath.Length);

            // 目录不存在则逐级创建
            if (!AssetDatabase.IsValidFolder(relative))
            {
                EnsureFolderPath(relative);
                AssetDatabase.Refresh();
            }

            _outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(relative);
        }

        /// <summary>确保一条以 Assets 开头的目录路径逐级存在</summary>
        private static void EnsureFolderPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            string[] parts = assetPath.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets")
            {
                return;
            }

            string parent = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                EnsureFolder(parent, parts[i]);
                parent += "/" + parts[i];
            }
        }

        // ==================== 备份与撤销 ====================

        /// <summary>备份根目录（工程根目录下，与 Assets 同级，不会被 Unity 导入）</summary>
        private static string GetBackupRootDir()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, BackupRootName);
        }

        /// <summary>撤销清单文件的绝对路径</summary>
        private static string GetManifestPath()
        {
            return Path.Combine(GetBackupRootDir(), ManifestFileName);
        }

        /// <summary>把 Assets 相对路径转为磁盘绝对路径</summary>
        private static string ToAbsolute(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, assetPath);
        }

        /// <summary>把原图及其 .meta 复制到备份目录，保留 Assets 相对结构，返回备份目录绝对路径</summary>
        private static string BackupTextures(List<TextureInfo> textures, string timestamp)
        {
            string backupDir = Path.Combine(GetBackupRootDir(), timestamp);
            try
            {
                foreach (TextureInfo tex in textures)
                {
                    string srcAbs = ToAbsolute(tex.Path);
                    string destAbs = Path.Combine(backupDir, tex.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destAbs));

                    if (File.Exists(srcAbs))
                    {
                        File.Copy(srcAbs, destAbs, true);
                    }

                    // 同时备份 .meta，保留导入设置与 GUID
                    string metaSrc = srcAbs + ".meta";
                    if (File.Exists(metaSrc))
                    {
                        File.Copy(metaSrc, destAbs + ".meta", true);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SpriteAtlas 拆分] 备份失败：{e.Message}");
            }

            return backupDir;
        }

        /// <summary>保存撤销清单到磁盘</summary>
        private static void SaveManifest(SplitManifest manifest)
        {
            try
            {
                Directory.CreateDirectory(GetBackupRootDir());
                File.WriteAllText(GetManifestPath(), JsonUtility.ToJson(manifest, true));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SpriteAtlas 拆分] 保存撤销清单失败：{e.Message}");
            }
        }

        /// <summary>读取撤销清单，无则返回 null</summary>
        private static SplitManifest LoadManifest()
        {
            string path = GetManifestPath();
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<SplitManifest>(File.ReadAllText(path));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SpriteAtlas 拆分] 读取撤销清单失败：{e.Message}");
                return null;
            }
        }

        /// <summary>撤销上次拆分：删除生成的图集、把图片移回原位、清理新建的空文件夹</summary>
        private void UndoLast()
        {
            SplitManifest manifest = LoadManifest();
            if (manifest == null)
            {
                EditorUtility.DisplayDialog("SpriteAtlas 拆分", "没有可撤销的操作。", "确定");
                return;
            }

            bool confirm = EditorUtility.DisplayDialog(
                "撤销上次拆分",
                $"将删除 {manifest.createdAtlases.Count} 个生成的图集，" +
                $"把 {manifest.moves.Count} 张图片移回原位，并清理新建的空文件夹。\n\n是否继续？",
                "撤销", "取消");
            if (!confirm)
            {
                return;
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                // 1. 删除生成的 SpriteAtlas
                foreach (string atlasPath in manifest.createdAtlases)
                {
                    if (!string.IsNullOrEmpty(atlasPath) && File.Exists(ToAbsolute(atlasPath)))
                    {
                        AssetDatabase.DeleteAsset(atlasPath);
                    }
                }

                // 2. 把图片移回原位（MoveAsset 保留 GUID，引用仍然有效）
                foreach (MoveRecord move in manifest.moves)
                {
                    if (string.IsNullOrEmpty(move.from) || string.IsNullOrEmpty(move.to))
                    {
                        continue;
                    }

                    if (!File.Exists(ToAbsolute(move.to)))
                    {
                        continue;
                    }

                    // 确保原目录存在
                    string originalDir = Path.GetDirectoryName(move.from)?.Replace('\\', '/');
                    EnsureFolderPath(originalDir);

                    string error = AssetDatabase.MoveAsset(move.to, move.from);
                    if (!string.IsNullOrEmpty(error))
                    {
                        Debug.LogError($"[SpriteAtlas 拆分] 撤销移动失败: {move.to} -> {move.from}\n{error}");
                    }
                }

                // 3. 清理新建的子文件夹（仅当已空，避免误删用户新增内容）
                foreach (string folder in manifest.createdFolders)
                {
                    if (AssetDatabase.IsValidFolder(folder) && IsFolderEmpty(folder))
                    {
                        AssetDatabase.DeleteAsset(folder);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            // 删除清单，避免重复撤销（备份文件保留，作为灾备）
            try
            {
                File.Delete(GetManifestPath());
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SpriteAtlas 拆分] 删除撤销清单失败：{e.Message}");
            }

            _previewText = string.Empty;
            Debug.Log("[SpriteAtlas 拆分] 已撤销上次拆分。");
        }

        /// <summary>判断 Assets 文件夹是否为空（不含任何文件与子目录）</summary>
        private static bool IsFolderEmpty(string assetFolder)
        {
            string abs = ToAbsolute(assetFolder);
            if (!Directory.Exists(abs))
            {
                return false;
            }

            return Directory.GetFiles(abs).Length == 0 && Directory.GetDirectories(abs).Length == 0;
        }

        // ==================== EditorPrefs 读写 ====================

        private void LoadPrefs()
        {
            string sourcePath = EditorPrefs.GetString(PrefKeySource, string.Empty);
            if (!string.IsNullOrEmpty(sourcePath))
            {
                _sourceFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(sourcePath);
            }

            string outputPath = EditorPrefs.GetString(PrefKeyOutput, string.Empty);
            if (!string.IsNullOrEmpty(outputPath))
            {
                _outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(outputPath);
            }

            _maxSize = EditorPrefs.GetInt(PrefKeyMaxSize, 1024);
            _maxPageCount = Mathf.Max(1, EditorPrefs.GetInt(PrefKeyMaxPage, 1));
            _padding = EditorPrefs.GetInt(PrefKeyPadding, 4);
            _namePrefix = EditorPrefs.GetString(PrefKeyPrefix, "Atlas");
            _startIndex = EditorPrefs.GetInt(PrefKeyStartIndex, 1);
            _recursive = EditorPrefs.GetBool(PrefKeyRecursive, true);
            _preciseMode = EditorPrefs.GetBool(PrefKeyPrecise, false);
            _backup = EditorPrefs.GetBool(PrefKeyBackup, true);
        }

        private void SavePrefs()
        {
            EditorPrefs.SetString(PrefKeySource, _sourceFolder != null ? AssetDatabase.GetAssetPath(_sourceFolder) : string.Empty);
            EditorPrefs.SetString(PrefKeyOutput, _outputFolder != null ? AssetDatabase.GetAssetPath(_outputFolder) : string.Empty);
            EditorPrefs.SetInt(PrefKeyMaxSize, _maxSize);
            EditorPrefs.SetInt(PrefKeyMaxPage, _maxPageCount);
            EditorPrefs.SetInt(PrefKeyPadding, _padding);
            EditorPrefs.SetString(PrefKeyPrefix, _namePrefix ?? "Atlas");
            EditorPrefs.SetInt(PrefKeyStartIndex, _startIndex);
            EditorPrefs.SetBool(PrefKeyRecursive, _recursive);
            EditorPrefs.SetBool(PrefKeyPrecise, _preciseMode);
            EditorPrefs.SetBool(PrefKeyBackup, _backup);
        }

        // ==================== 数据结构 ====================

        /// <summary>单张图片信息</summary>
        private class TextureInfo
        {
            public string Path;
            public int Width;
            public int Height;
        }

        /// <summary>一个 Atlas 分组（含若干页与其精灵）</summary>
        private class AtlasGroup
        {
            public readonly List<MaxRectsBin> Pages = new List<MaxRectsBin>();
            public readonly List<TextureInfo> Items = new List<TextureInfo>();

            /// <summary>是否包含超出最大尺寸的单图</summary>
            public bool Oversize;

            /// <summary>精确模式下由真实打包器测得的页数；&lt;0 表示未设置，回退到 MaxRects 页数</summary>
            public int PageCountOverride = -1;

            public int PageCount => PageCountOverride >= 0 ? PageCountOverride : Pages.Count;
        }

        /// <summary>执行计划：分组对应的名称、子文件夹路径与要移入的图片</summary>
        private class AtlasPlan
        {
            public string Name;
            public string SubFolderPath;
            public List<TextureInfo> Items;
        }

        /// <summary>单条移动记录（原路径 -> 新路径），用于撤销</summary>
        [System.Serializable]
        private class MoveRecord
        {
            public string from;
            public string to;
        }

        /// <summary>一次拆分操作的完整清单，持久化以支持撤销</summary>
        [System.Serializable]
        private class SplitManifest
        {
            public string timestamp;
            public string backupDir;
            public List<string> createdAtlases = new List<string>();
            public List<string> createdFolders = new List<string>();
            public List<MoveRecord> moves = new List<MoveRecord>();
        }

        /// <summary>
        /// MaxRects 装箱器（Best Short Side Fit，不旋转）。
        /// 用于在一页固定尺寸内模拟摆放矩形，判断是否放得下。
        /// </summary>
        private class MaxRectsBin
        {
            private readonly int _binWidth;
            private readonly int _binHeight;

            /// <summary>剩余可用空闲矩形列表</summary>
            private readonly List<RectInt> _freeRects = new List<RectInt>();

            public MaxRectsBin(int width, int height)
            {
                _binWidth = width;
                _binHeight = height;
                _freeRects.Add(new RectInt(0, 0, width, height));
            }

            /// <summary>尝试插入一个 w×h 的矩形，成功返回 true 并占用空间</summary>
            public bool Insert(int w, int h)
            {
                if (w > _binWidth || h > _binHeight)
                {
                    return false;
                }

                if (!FindBestPosition(w, h, out RectInt placed))
                {
                    return false;
                }

                PlaceRect(placed);
                return true;
            }

            /// <summary>Best Short Side Fit：寻找短边浪费最小的空闲位置</summary>
            private bool FindBestPosition(int w, int h, out RectInt placed)
            {
                placed = new RectInt(0, 0, 0, 0);
                int bestShortSide = int.MaxValue;
                int bestLongSide = int.MaxValue;
                bool found = false;

                foreach (RectInt free in _freeRects)
                {
                    if (free.width < w || free.height < h)
                    {
                        continue;
                    }

                    int leftoverH = free.width - w;
                    int leftoverV = free.height - h;
                    int shortSide = Mathf.Min(leftoverH, leftoverV);
                    int longSide = Mathf.Max(leftoverH, leftoverV);

                    if (shortSide < bestShortSide || (shortSide == bestShortSide && longSide < bestLongSide))
                    {
                        bestShortSide = shortSide;
                        bestLongSide = longSide;
                        placed = new RectInt(free.x, free.y, w, h);
                        found = true;
                    }
                }

                return found;
            }

            /// <summary>放置矩形：切分与之相交的空闲矩形并裁剪冗余</summary>
            private void PlaceRect(RectInt used)
            {
                for (int i = _freeRects.Count - 1; i >= 0; i--)
                {
                    if (SplitFreeNode(_freeRects[i], used, out List<RectInt> fragments))
                    {
                        _freeRects.RemoveAt(i);
                        _freeRects.AddRange(fragments);
                    }
                }

                PruneFreeList();
            }

            /// <summary>把与 used 相交的空闲矩形切成若干不相交碎片；无相交返回 false</summary>
            private bool SplitFreeNode(RectInt free, RectInt used, out List<RectInt> fragments)
            {
                fragments = new List<RectInt>();

                // 无相交则保留原空闲矩形
                if (used.x >= free.xMax || used.xMax <= free.x || used.y >= free.yMax || used.yMax <= free.y)
                {
                    return false;
                }

                // 水平方向相交：切出上/下两条
                if (used.x < free.xMax && used.xMax > free.x)
                {
                    if (used.y > free.y && used.y < free.yMax)
                    {
                        fragments.Add(new RectInt(free.x, free.y, free.width, used.y - free.y));
                    }

                    if (used.yMax < free.yMax)
                    {
                        fragments.Add(new RectInt(free.x, used.yMax, free.width, free.yMax - used.yMax));
                    }
                }

                // 垂直方向相交：切出左/右两条
                if (used.y < free.yMax && used.yMax > free.y)
                {
                    if (used.x > free.x && used.x < free.xMax)
                    {
                        fragments.Add(new RectInt(free.x, free.y, used.x - free.x, free.height));
                    }

                    if (used.xMax < free.xMax)
                    {
                        fragments.Add(new RectInt(used.xMax, free.y, free.xMax - used.xMax, free.height));
                    }
                }

                return true;
            }

            /// <summary>移除被其它空闲矩形完全包含的冗余矩形</summary>
            private void PruneFreeList()
            {
                for (int i = 0; i < _freeRects.Count; i++)
                {
                    for (int j = i + 1; j < _freeRects.Count; j++)
                    {
                        if (Contains(_freeRects[j], _freeRects[i]))
                        {
                            _freeRects.RemoveAt(i);
                            i--;
                            break;
                        }

                        if (Contains(_freeRects[i], _freeRects[j]))
                        {
                            _freeRects.RemoveAt(j);
                            j--;
                        }
                    }
                }
            }

            /// <summary>outer 是否完全包含 inner</summary>
            private static bool Contains(RectInt outer, RectInt inner)
            {
                return inner.x >= outer.x && inner.y >= outer.y &&
                       inner.xMax <= outer.xMax && inner.yMax <= outer.yMax;
            }
        }
    }
}
