using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

namespace ComfyUIUpscaler.Editor
{
    internal static class AssetListViewUtility
    {
        public static bool MatchesSearch(TextureAssetInfo asset, string searchText)
        {
            if (asset == null)
                return false;
            string query = (searchText ?? string.Empty).Trim();
            return query.Length == 0 ||
                   (asset.assetPath ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (asset.lastUpgradeJobId ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   UpgradeAssetStateUtility.GetLabel(asset)
                       .IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int GetPageCount(int itemCount, int pageSize)
        {
            if (itemCount < 0)
                throw new ArgumentOutOfRangeException(nameof(itemCount));
            if (pageSize < 1)
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            return itemCount == 0 ? 1 : (itemCount - 1) / pageSize + 1;
        }

        public static int ClampPageIndex(int pageIndex, int itemCount, int pageSize)
        {
            return Mathf.Clamp(pageIndex, 0, GetPageCount(itemCount, pageSize) - 1);
        }
    }

    // ComfyUI 侧尺寸预估（纯计算）。仅用于提示，不参与实际处理。
    internal static class UpscaleMemoryEstimator
    {
        // Unity Texture2D 单边上限，输出图集超过必然失败
        public const int UnityMaxTextureEdge = 16384;

        // 估算最终输出图集单边（用最终倍率），用于判断是否超过 Unity 限制
        public static int EstimateMaxOutputEdge(int maxPageEdge, float finalScale)
        {
            if (maxPageEdge <= 0 || finalScale <= 0f)
                return 0;
            return Mathf.CeilToInt(maxPageEdge * finalScale);
        }
    }

    // 基于 Odin 绘制的高清化窗口。窗口自身即绘制目标（OdinEditorWindow 默认绘制 this），
    // 全部界面通过特性声明；两个表格用 Odin TableList，进度/日志等实时视图用 OnInspectorGUI 保留原样。
    public sealed class ComfyUIUpscalerWindow : OdinEditorWindow
    {
        private const string PrefPrefix = "ComfyUIUpscaler.";

        // 各配置盒子的标题（用 Odin BoxGroup 分区）
        private const string BoxSource = "路径与扫描";
        private const string BoxComfy = "ComfyUI 连接与工作流";
        private const string BoxAtlas = "图集参数";

        // 顶部页签：任务面板与历史记录分开，避免历史表格挤占主流程视野
        private enum WindowTab { Task, History }
        private static readonly string[] TabLabels = { "任务面板", "历史记录" };
        private WindowTab activeTab = WindowTab.Task;
        private bool ShowTaskTab => activeTab == WindowTab.Task;
        private bool ShowHistoryTab => activeTab == WindowTab.History;

        [PropertyOrder(0)]
        [OnInspectorGUI]
        private void DrawTabBar()
        {
            activeTab = (WindowTab)GUILayout.Toolbar((int)activeTab, TabLabels, GUILayout.Height(24f));
            EditorGUILayout.Space(4f);
        }

        // ==================== 任务面板 · 路径与扫描 ====================

        // 扫描目录（递归扫描图片）。与“跳过的文件夹”一样用文件夹对象引用（DefaultAsset），可拖拽或点选择器选中
        private readonly List<DefaultAsset> sourceFolders = new List<DefaultAsset>();

        // 扫描目录与跳过目录并排两列，均为文件夹对象引用；用 IMGUI 手绘以获得并排布局，
        // 并规避 Odin 列表控件对 Object 引用“点击复制一份引用”的怪异行为
        [BoxGroup(BoxSource)]
        [PropertyOrder(100)]
        [ShowIf(nameof(ShowTaskTab))]
        [OnInspectorGUI]
        private void DrawFolderColumns()
        {
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(Busy))
            {
                DrawFolderColumn("扫描目录", sourceFolders, OnSourceFoldersChanged,
                    "拖入或点选择器选择 Assets 下的文件夹；将递归扫描其中图片");
                GUILayout.Space(6f);
                DrawFolderColumn("跳过的文件夹（不升级）", skipFolders, OnSkipFoldersChanged,
                    "这些文件夹（含子目录、以后新增）默认不升级；也可在下方资源表逐行勾选“跳过”");
            }
        }

        // 单列文件夹引用编辑：对象字段（点对象可在工程定位）+ 移除按钮 + 添加空槽
        private void DrawFolderColumn(string title, List<DefaultAsset> list, Action onChanged, string tooltip)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(new GUIContent(title, tooltip), EditorStyles.miniBoldLabel);
                int removeIndex = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var picked = (DefaultAsset)EditorGUILayout.ObjectField(list[i], typeof(DefaultAsset), false);
                        if (EditorGUI.EndChangeCheck())
                        {
                            list[i] = picked;
                            onChanged();
                        }
                        if (GUILayout.Button("−", GUILayout.Width(24f)))
                            removeIndex = i;
                    }
                }
                if (removeIndex >= 0)
                {
                    list.RemoveAt(removeIndex);
                    onChanged();
                }
                if (GUILayout.Button("+ 添加文件夹", GUILayout.Height(20f)))
                    list.Add(null);
            }
        }

        // 从文件夹对象引用列表中提取有效、去重的 Assets 相对路径
        private static List<string> GetFolderPaths(List<DefaultAsset> folders)
        {
            return folders
                .Where(folder => folder != null)
                .Select(folder => AssetDatabase.GetAssetPath(folder))
                .Where(path => !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 剔除非文件夹的引用，保留空槽（供随后选择）；返回是否有被剔除项
        private static bool RemoveInvalidFolders(List<DefaultAsset> list)
        {
            bool dropped = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                DefaultAsset folder = list[i];
                if (folder == null)
                    continue;
                string path = AssetDatabase.GetAssetPath(folder);
                if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
                {
                    list.RemoveAt(i);
                    dropped = true;
                }
            }
            return dropped;
        }

        // 扫描目录变更：剔除非文件夹引用、清空旧扫描结果并保存偏好
        private void OnSourceFoldersChanged()
        {
            if (RemoveInvalidFolders(sourceFolders))
                ShowNotification(new GUIContent("请选择 Assets 下的文件夹"));
            assets.Clear();
            RebuildAssetRows();
            atlasEstimateDirty = true;
            SavePreferences();
        }

        private void ScanOrCancel()
        {
            if (scanning)
                scanCancellation?.Cancel();
            else
                _ = ScanAsync();
        }

        // ==================== ComfyUI 连接与工作流（值字段，绘制见 DrawTaskConfig） ====================

        private string comfyUrl = "http://127.0.0.1:8188";
        private string workflowPath = string.Empty;
        private string inputNodeId = string.Empty;
        private string inputFieldName = "image";
        private string outputNodeId = string.Empty;
        private int requestTimeoutSeconds = 120;
        private int jobTimeoutMinutes = 30;

        private void TestConnection()
        {
            _ = TestConnectionAsync();
        }

        // ==================== 图集参数（值字段，绘制见 DrawTaskConfig） ====================

        private float expectedScale = 4f;
        private int padding = 32;
        private int maxAtlasEdge = 4096;
        private long maxAtlasPixels = 16777216;
        private int jpegQuality = 95;
        // 是否在放大纹理时同步放大 spritePixelsPerUnit（保持 Sprite 显示尺寸/九宫格外观不变）
        private bool keepDisplaySize = true;

        private static readonly int[] AtlasEdgeValues = { 1024, 2048, 4096, 8192 };
        private static readonly string[] AtlasEdgeLabels = { "1024", "2048", "4096", "8192" };

        // 各配置区用 IMGUI 手绘（保证多列紧凑布局），外层用 Odin BoxGroup 分别包盒
        [BoxGroup(BoxSource)]
        [PropertyOrder(110)]
        [ShowIf(nameof(ShowTaskTab))]
        [OnInspectorGUI]
        private void DrawScanButton()
        {
            using (new EditorGUI.DisabledScope(!ScanButtonEnabled))
            {
                if (GUILayout.Button(ScanButtonLabel, GUILayout.Height(24f)))
                    ScanOrCancel();
            }
        }

        [BoxGroup(BoxComfy)]
        [PropertyOrder(200)]
        [ShowIf(nameof(ShowTaskTab))]
        [OnInspectorGUI]
        private void DrawComfyConfig()
        {
            using (new EditorGUI.DisabledScope(Busy))
            {
                comfyUrl = EditorGUILayout.TextField("地址", comfyUrl);
                using (new EditorGUILayout.HorizontalScope())
                {
                    workflowPath = EditorGUILayout.TextField("API Format JSON", workflowPath);
                    if (GUILayout.Button("浏览", GUILayout.Width(56f)))
                    {
                        string picked = EditorUtility.OpenFilePanel("选择 API Format 工作流", string.Empty, "json");
                        if (!string.IsNullOrEmpty(picked))
                            workflowPath = picked;
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    inputNodeId = EditorGUILayout.TextField("输入图片节点 ID", inputNodeId);
                    inputFieldName = EditorGUILayout.TextField("输入字段名", inputFieldName);
                }
                outputNodeId = EditorGUILayout.TextField("最终输出节点 ID", outputNodeId);
                using (new EditorGUILayout.HorizontalScope())
                {
                    requestTimeoutSeconds = Mathf.Max(1, EditorGUILayout.IntField("单请求超时（秒）", requestTimeoutSeconds));
                    jobTimeoutMinutes = Mathf.Max(1, EditorGUILayout.IntField("单页超时（分钟）", jobTimeoutMinutes));
                }
                using (new EditorGUI.DisabledScope(!CanTestConnection))
                {
                    if (GUILayout.Button(TestConnectionLabel, GUILayout.Height(22f)))
                        TestConnection();
                }
            }
        }

        [BoxGroup(BoxAtlas)]
        [PropertyOrder(300)]
        [ShowIf(nameof(ShowTaskTab))]
        [OnInspectorGUI]
        private void DrawAtlasConfig()
        {
            using (new EditorGUI.DisabledScope(Busy))
            {
                EditorGUI.BeginChangeCheck();
                using (new EditorGUILayout.HorizontalScope())
                {
                    expectedScale = Mathf.Max(1f, EditorGUILayout.FloatField(
                        new GUIContent("目标倍率", "最终期望的放大倍率（例如 1.44）。ComfyUI 需输出 ≥ 该值的整数 POT 倍率（如 2×/4×，工作流内不要再缩放）；工具会自动按整数倍精确裁剪，再降采样到该目标倍率，保证同尺寸输入得到一致的输出尺寸。"),
                        expectedScale));
                    padding = Mathf.Max(0, EditorGUILayout.IntField("Padding", padding));
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    maxAtlasEdge = EditorGUILayout.IntPopup("最大边长", maxAtlasEdge, AtlasEdgeLabels, AtlasEdgeValues);
                    maxAtlasPixels = Math.Max(4096L, EditorGUILayout.LongField("最大像素数", maxAtlasPixels));
                }
                jpegQuality = EditorGUILayout.IntSlider("JPG 质量", jpegQuality, 1, 100);
                keepDisplaySize = EditorGUILayout.ToggleLeft(
                    new GUIContent("保持显示尺寸（同步放大 PPU）",
                        "开启：放大纹理时同步放大 spritePixelsPerUnit，Sprite 的显示尺寸/九宫格外观不变，仅更清晰（推荐，避免受 Layout/原生尺寸控制的图片变大变形）。\n关闭：仅放大纹理，Sprite 逻辑尺寸随之变大（适合整体提升 UI 分辨率的场景）。"),
                    keepDisplaySize);
                if (EditorGUI.EndChangeCheck())
                    MarkEstimateDirty();
            }
        }

        // ==================== 跳过标记（不升级） ====================

        // 被标记为“不升级”的文件夹（递归，含以后新增的资源）。UI 与“扫描目录”并排绘制（见 DrawFolderColumns），
        // 用文件夹对象引用（DefaultAsset）；存储层保存路径字符串，OnEnable/变更时双向转换。
        private readonly List<DefaultAsset> skipFolders = new List<DefaultAsset>();

        // 跳过文件夹变更后：剔除非文件夹引用，把有效文件夹路径去重写回存储并重算跳过标记
        private void OnSkipFoldersChanged()
        {
            if (RemoveInvalidFolders(skipFolders))
                ShowNotification(new GUIContent("请选择 Assets 下的文件夹"));
            UpscaleSkipStore.SetFolders(GetFolderPaths(skipFolders));
            ReapplySkipMarks();
        }

        // 依据存储重算 skipped 标记：被标记的资源取消勾选并重建资源表
        private void ReapplySkipMarks()
        {
            UpscaleSkipStore.ApplyToAssets(assets);
            foreach (TextureAssetInfo asset in assets)
                if (asset.skipped)
                    asset.selected = false;
            RebuildAssetRows();
            atlasEstimateDirty = true;
            SafeRepaint();
        }

        // 逐行“跳过”开关触发：延迟到下一帧重建表格（避免绘制表格时修改集合），并刷新预估
        private void OnSkipToggled()
        {
            assetRowsDirty = true;
            atlasEstimateDirty = true;
            SafeRepaint();
        }

        // ==================== 待处理资源 ====================

        private static readonly string[] UpgradeFilterLabels =
            { "全部状态", "未升级", "已升级", "已变化", "上次失败", "已回滚" };

        private string AssetSummary =>
            $"已选 {assets.Count(asset => asset.selected)}/{assets.Count}，筛选后 {filteredAssets.Count}，已跳过 {assets.Count(asset => asset.skipped)}";

        private UpgradeAssetFilter assetUpgradeFilter = UpgradeAssetFilter.All;

        // 一行横排的资源工具条：统计 + 状态筛选下拉 + 批量选择按钮（用 IMGUI 保证紧凑横向布局）
        [PropertyOrder(420)]
        [ShowIf(nameof(ShowTaskTab))]
        [OnInspectorGUI]
        private void DrawAssetToolbar()
        {
            // 逐行“跳过”开关会请求延迟重建，这里在绘制表格前统一重建，避免在表格绘制过程中修改集合
            if (assetRowsDirty)
            {
                assetRowsDirty = false;
                RebuildAssetRows();
            }

            EditorGUILayout.LabelField("待处理资源", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(Busy))
            {
                EditorGUILayout.LabelField(AssetSummary, GUILayout.MinWidth(220f));
                GUILayout.Label("状态", GUILayout.Width(30f));
                EditorGUI.BeginChangeCheck();
                int updated = EditorGUILayout.Popup((int)assetUpgradeFilter, UpgradeFilterLabels, GUILayout.Width(90f));
                if (EditorGUI.EndChangeCheck())
                {
                    assetUpgradeFilter = (UpgradeAssetFilter)updated;
                    OnFilterChanged();
                }
                GUILayout.Space(8f);
                EditorGUI.BeginChangeCheck();
                hideSkipped = GUILayout.Toggle(hideSkipped, "隐藏已跳过", GUILayout.Width(90f));
                if (EditorGUI.EndChangeCheck())
                    RebuildAssetRows();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("全选", GUILayout.Width(60f)))
                    SelectAll();
                if (GUILayout.Button("仅安全项", GUILayout.Width(76f)))
                    SelectSafeOnly();
                if (GUILayout.Button("取消已升级", GUILayout.Width(90f)))
                    DeselectUpgraded();
                if (GUILayout.Button("全不选", GUILayout.Width(68f)))
                    DeselectAll();
            }
        }

        // 批量选择均跳过被标记为“不升级”的资源
        private void SelectAll() => SetFilteredSelection(asset => !asset.skipped);

        private void SelectSafeOnly() =>
            SetFilteredSelection(asset => !asset.skipped && string.IsNullOrEmpty(asset.warning));

        private void DeselectUpgraded() =>
            SetFilteredSelection(asset => asset.upgradeState != UpgradeAssetState.Upgraded && asset.selected);

        private void DeselectAll() => SetFilteredSelection(_ => false);

        [PropertyOrder(440)]
        [ShowIf(nameof(ShowTaskTab))]
        [ShowInInspector]
        [EnableIf(nameof(NotBusy))]
        [Searchable]
        [TableList(IsReadOnly = true, AlwaysExpanded = true, ShowPaging = true, NumberOfItemsPerPage = 25)]
        [HideLabel]
        private List<AssetRow> assetRows = new List<AssetRow>();

        // ==================== 执行进度与日志 ====================

        [PropertyOrder(500)]
        [ShowIf(nameof(ShowTaskTab))]
        [OnInspectorGUI]
        private void DrawExecution()
        {
            EditorGUILayout.LabelField("执行进度与日志", EditorStyles.boldLabel);

            RefreshAtlasEstimate();
            int selectedCount = assets.Count(asset => asset.selected);
            string estimate = string.IsNullOrEmpty(atlasEstimateError)
                ? $"文件 {selectedCount}，预计图集 {estimatedAtlasPages}"
                : "参数错误: " + atlasEstimateError;
            EditorGUILayout.LabelField(estimate);

            // 显存：实测优先（运行时持续采样），无需再手填峰值倍率
            int outputEdge = UpscaleMemoryEstimator.EstimateMaxOutputEdge(estimatedMaxPageEdge, expectedScale);
            string budgetText = memBudgetKnown
                ? $"可用显存 {UpscaleJobStore.FormatBytes(vramFreeBytes)}/{UpscaleJobStore.FormatBytes(vramTotalBytes)}（{memDeviceName}）"
                : "可用显存未知（点“测试连接”或“刷新显存”）";
            string usedText = observedPeakUsedBytes > 0
                ? $"｜实测占用峰值≈{UpscaleJobStore.FormatBytes(observedPeakUsedBytes)}"
                : string.Empty;
            EditorGUILayout.LabelField(budgetText + usedText);
            foreach (string warning in BuildMemoryWarnings(outputEdge))
                EditorGUILayout.HelpBox(warning, MessageType.Warning);

            Rect progressRect = EditorGUILayout.GetControlRect(false, 20f);
            EditorGUI.ProgressBar(progressRect, progress, status);

            DrawActionButtons();

            logScroll = EditorGUILayout.BeginScrollView(logScroll, EditorStyles.helpBox, GUILayout.Height(120f));
            foreach (string line in liveLog)
                EditorGUILayout.SelectableLabel(line, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndScrollView();
        }

        // 开始 / 中断 / 刷新显存 横排，用背景色区分主次
        private void DrawActionButtons()
        {
            Color previous = GUI.backgroundColor;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!CanStart))
                {
                    GUI.backgroundColor = new Color(0.45f, 0.82f, 0.5f);
                    if (GUILayout.Button("开始高清化", GUILayout.Height(30f)))
                        StartRun();
                    GUI.backgroundColor = previous;
                }
                using (new EditorGUI.DisabledScope(!running))
                {
                    GUI.backgroundColor = new Color(0.95f, 0.55f, 0.4f);
                    if (GUILayout.Button(new GUIContent("中断", "安全中断并保留进度，可从历史任务继续"), GUILayout.Height(30f)))
                    {
                        status = "正在安全中断，进度将保留...";
                        runCancellation?.Cancel();
                    }
                    GUI.backgroundColor = previous;
                }
                using (new EditorGUI.DisabledScope(fetchingMemory))
                {
                    if (GUILayout.Button(RefreshMemLabel, GUILayout.Height(30f)))
                        _ = FetchMemoryBudgetAsync();
                }
            }
        }

        // ==================== 历史记录页签（左任务列表 + 右详情） ====================

        private static readonly string[] HistoryFilterLabels =
            { "全部", "已完成", "失败", "已取消", "已回滚", "进行中" };

        // 历史左右两栏滚动区高度：随窗口高度自适应填满整页（减去上方页签/标题/筛选等固定占用）。
        // 用常量偏移而非布局测量，避免 IMGUI 在 Layout/Repaint 两阶段高度不一致报错。
        // 历史面板上半区（任务列表/任务详情）高度：固定占比，余下空间留给下方整页资源表
        private float HistoryTopHeight => Mathf.Clamp(position.height * 0.34f, 200f, 320f);

        [PropertyOrder(600)]
        [ShowIf(nameof(ShowHistoryTab))]
        [OnInspectorGUI]
        private void DrawHistory()
        {
            EditorGUILayout.LabelField("历史任务与恢复", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("状态", GUILayout.Width(30f));
                historyFilter = (HistoryFilter)EditorGUILayout.Popup((int)historyFilter, HistoryFilterLabels, GUILayout.Width(90f));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("刷新", GUILayout.Width(70f)))
                    RefreshJobs();
                if (GUILayout.Button("打开映射", GUILayout.Width(90f)))
                    OpenIndexMap();
            }
            EditorGUILayout.Space(2f);

            List<JobRecord> visible = FilterJobs();
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawHistoryList(visible);
                DrawHistoryDetail(visible);
            }
        }

        private void OpenIndexMap()
        {
            if (File.Exists(UpgradeAssetIndexStore.IndexPath))
                EditorUtility.RevealInFinder(UpgradeAssetIndexStore.IndexPath);
            else
                ShowNotification(new GUIContent("映射表尚未生成"));
        }

        // 依状态筛选左侧任务列表；“进行中”覆盖新建/处理中/待提交三种未完结状态
        private List<JobRecord> FilterJobs()
        {
            switch (historyFilter)
            {
                case HistoryFilter.Completed:
                    return jobs.Where(job => job.manifest.status == JobStatus.Completed).ToList();
                case HistoryFilter.Failed:
                    return jobs.Where(job => job.manifest.status == JobStatus.Failed).ToList();
                case HistoryFilter.Canceled:
                    return jobs.Where(job => job.manifest.status == JobStatus.Canceled).ToList();
                case HistoryFilter.RolledBack:
                    return jobs.Where(job => job.manifest.status == JobStatus.RolledBack).ToList();
                case HistoryFilter.Processing:
                    return jobs.Where(job =>
                        job.manifest.status == JobStatus.Created ||
                        job.manifest.status == JobStatus.Processing ||
                        job.manifest.status == JobStatus.ReadyToCommit).ToList();
                default:
                    return jobs;
            }
        }

        // 左侧任务列表：每条为可点击条目，选中项高亮
        private void DrawHistoryList(List<JobRecord> visible)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(320f)))
            {
                EditorGUILayout.LabelField($"任务列表（{visible.Count}）", EditorStyles.miniBoldLabel);
                // 左侧比右侧多一行标题，减去其高度以对齐两栏底部
                historyListScroll = EditorGUILayout.BeginScrollView(
                    historyListScroll, EditorStyles.helpBox, GUILayout.Height(HistoryTopHeight - 20f));
                if (visible.Count == 0)
                    EditorGUILayout.LabelField("暂无记录", EditorStyles.miniLabel);
                foreach (JobRecord job in visible)
                {
                    bool isSelected = job.manifest.jobId == selectedJobId;
                    string title = $"{job.manifest.status}｜{job.manifest.jobId}\n" +
                                   $"{UpgradeAssetStateUtility.GetLocalDate(job.manifest.createdUtc)} · " +
                                   $"{job.manifest.assets.Count} 文件 / {job.manifest.pages.Count} 页";
                    Color previous = GUI.backgroundColor;
                    if (isSelected)
                        GUI.backgroundColor = new Color(0.45f, 0.7f, 1f);
                    if (GUILayout.Button(title, HistoryEntryStyle, GUILayout.Height(42f)))
                        SelectJob(job);
                    GUI.backgroundColor = previous;
                }
                EditorGUILayout.EndScrollView();
            }
        }

        // 右侧任务详情（仅详情与操作按钮）；被修改资源表已下移为整页 Odin 表格
        private void DrawHistoryDetail(List<JobRecord> visible)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                historyDetailScroll = EditorGUILayout.BeginScrollView(historyDetailScroll, GUILayout.Height(HistoryTopHeight));
                JobRecord job = jobs.FirstOrDefault(record => record.manifest.jobId == selectedJobId);
                if (job == null)
                {
                    EditorGUILayout.LabelField("请选择左侧任务查看详情", EditorStyles.miniLabel);
                }
                else
                {
                    EnsureDetailRows(job);
                    DrawJobDetailTop(job);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawJobDetailTop(JobRecord job)
        {
            UpscaleJobManifest manifest = job.manifest;
            EditorGUILayout.LabelField("任务详情", EditorStyles.boldLabel);
            DrawKeyValue("任务 ID", manifest.jobId);
            DrawKeyValue("状态", manifest.status);
            DrawKeyValue("创建时间", UpgradeAssetStateUtility.GetLocalDate(manifest.createdUtc));
            if (!string.IsNullOrEmpty(manifest.completedUtc))
                DrawKeyValue("完成时间", UpgradeAssetStateUtility.GetLocalDate(manifest.completedUtc));
            DrawKeyValue("规模", $"{manifest.assets.Count} 文件 / {manifest.pages.Count} 页");
            DrawKeyValue("大小变化", UpscaleJobStore.FormatSizeSummary(manifest));
            DrawKeyValue("预期倍率", $"{manifest.expectedScale:0.##}x");
            DrawKeyValue("工作流", string.IsNullOrEmpty(manifest.workflowPath)
                ? "-"
                : Path.GetFileName(manifest.workflowPath));
            if (!string.IsNullOrEmpty(manifest.workflowSha256))
                DrawKeyValue("工作流 SHA", manifest.workflowSha256.Length > 16
                    ? manifest.workflowSha256.Substring(0, 16) + "…"
                    : manifest.workflowSha256);
            if (!string.IsNullOrEmpty(manifest.error))
                EditorGUILayout.HelpBox(manifest.error, MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(Busy))
            {
                using (new EditorGUI.DisabledScope(!UpscaleJobStore.CanAttemptResume(manifest)))
                {
                    if (GUILayout.Button(new GUIContent("继续", "从中断处继续，已完成的图集页不会重跑"), GUILayout.Height(24f)))
                        Resume(job);
                }
                using (new EditorGUI.DisabledScope(manifest.status != JobStatus.Completed))
                {
                    if (GUILayout.Button(new GUIContent("恢复", "仅已完成且当前文件未变化的任务可恢复"), GUILayout.Height(24f)))
                        Restore(job);
                }
                if (GUILayout.Button("打开目录", GUILayout.Height(24f)))
                    EditorUtility.RevealInFinder(job.directory);
                GUILayout.FlexibleSpace();
            }

            // 恢复进行中：显示进度条与中断按钮（复制阶段可中断，末尾导入不可中断）
            if (restoring)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect barRect = GUILayoutUtility.GetRect(100f, 18f, GUILayout.ExpandWidth(true));
                    EditorGUI.ProgressBar(barRect, progress, status);
                    if (GUILayout.Button("中断", GUILayout.Width(60f), GUILayout.Height(18f)))
                        restoreCancellation?.Cancel();
                }
            }
        }

        // 单资源恢复：用该任务备份覆盖当前资源与 .meta，并把该资源在映射表中标记为已回滚
        private void RestoreSingleAsset(JobRecord job, DetailRow row)
        {
            try
            {
                if (!EditorUtility.DisplayDialog(
                        "恢复该资源",
                        $"将用任务 {job.manifest.jobId} 的备份覆盖以下资源（含 .meta）：\n\n{row.assetPath}\n\n恢复后该资源标记为“已回滚”。",
                        "恢复",
                        "取消"))
                    return;

                var target = new TextureAssetInfo { assetPath = row.assetPath, guid = row.guid };
                UpscaleJobStore.RestoreFiles(job.directory, new[] { target });
                UpgradeAssetIndexStore.MarkAssetRolledBack(row.guid, job.manifest.jobId);
                status = "已恢复资源: " + row.assetPath;
                if (assets.Count > 0)
                    RefreshAssetUpgradeStates();
                PingAsset(row.guid, row.assetPath);
            }
            catch (Exception exception)
            {
                status = "恢复失败: " + exception.Message;
                EditorUtility.DisplayDialog("恢复失败", exception.Message, "确定");
            }
        }

        // 详情资源表上方工具栏：状态筛选 + 刷新状态(异步) + 恢复筛选结果(异步)
        [PropertyOrder(605)]
        [ShowIf(nameof(ShowHistoryDetailTable))]
        [OnInspectorGUI]
        private void DrawDetailToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("状态筛选", GUILayout.Width(56f));
                string[] options = { "全部", "可恢复", "已变化", "无备份" };
                int index = (int)detailStateFilter;
                int picked = EditorGUILayout.Popup(index, options, GUILayout.Width(90f));
                if (picked != index)
                {
                    detailStateFilter = (DetailStateFilter)picked;
                    ApplyDetailFilter();
                }

                using (new EditorGUI.DisabledScope(Busy || detailStatusRefreshing))
                {
                    if (GUILayout.Button(new GUIContent("刷新状态", "异步校验各资源当前是否仍与本任务输出一致"), GUILayout.Width(80f)))
                        RefreshDetailStatuses();

                    using (new EditorGUI.DisabledScope(!detailStatusReady || (detailRows?.Count ?? 0) == 0))
                    {
                        if (GUILayout.Button(
                                new GUIContent($"恢复筛选结果（{detailRows?.Count ?? 0}）", "按当前筛选批量恢复：用本任务备份覆盖当前文件（含 .meta），跳过无备份项"),
                                GUILayout.Width(150f)))
                            RestoreFilteredDetail();
                    }
                }
                GUILayout.FlexibleSpace();
            }

            if (detailStatusRefreshing)
            {
                Rect rect = GUILayoutUtility.GetRect(100f, 16f, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(rect, progress, status);
            }
            else if (!detailStatusReady)
            {
                EditorGUILayout.LabelField("状态未刷新：点击“刷新状态”后可见每个资源当前是否可安全恢复", EditorStyles.miniLabel);
            }
            else if (allDetailRows != null)
            {
                int safe = allDetailRows.Count(row => row.status == DetailStatus.Safe);
                int changed = allDetailRows.Count(row => row.status == DetailStatus.Changed);
                int missing = allDetailRows.Count(row => row.status == DetailStatus.Missing);
                EditorGUILayout.LabelField($"可恢复 {safe} · 已变化 {changed} · 无备份 {missing}", EditorStyles.miniLabel);
            }
        }

        // 异步刷新详情表各行状态：复用 BuildRestorePlanAsync 的哈希分类，避免同步哈希大量文件卡死
        private async void RefreshDetailStatuses()
        {
            JobRecord job = jobs.FirstOrDefault(record => record.manifest.jobId == selectedJobId);
            if (job == null || allDetailRows == null)
                return;
            detailStatusRefreshing = true;
            progress = 0f;
            status = "校验资源状态…";
            var cancellation = new CancellationTokenSource();
            try
            {
                RestorePlan plan = await UpscaleJobStore.BuildRestorePlanAsync(
                    job.directory,
                    (value, message) => { progress = value; status = message; SafeRepaint(); },
                    cancellation.Token);
                var safe = new HashSet<string>(plan.safeAssets.Select(asset => asset.guid), StringComparer.Ordinal);
                var changed = new HashSet<string>(plan.changedAssets.Select(asset => asset.guid), StringComparer.Ordinal);
                foreach (DetailRow row in allDetailRows)
                {
                    if (!string.IsNullOrEmpty(row.guid) && safe.Contains(row.guid))
                        row.status = DetailStatus.Safe;
                    else if (!string.IsNullOrEmpty(row.guid) && changed.Contains(row.guid))
                        row.status = DetailStatus.Changed;
                    else
                        row.status = DetailStatus.Missing;
                    // 填充逐行变化详情（移动/图片/元数据等）
                    row.changeDetail = !string.IsNullOrEmpty(row.guid) &&
                                       plan.detailByGuid.TryGetValue(row.guid, out string detail)
                        ? detail
                        : string.Empty;
                }
                detailStatusReady = true;
                status = "状态已刷新";
                ApplyDetailFilter();
            }
            catch (Exception exception)
            {
                status = "状态刷新失败: " + exception.Message;
                Debug.LogException(exception);
            }
            finally
            {
                detailStatusRefreshing = false;
                cancellation.Dispose();
                SafeRepaint();
            }
        }

        // 按当前筛选批量恢复：只恢复有备份(可恢复/已变化)的行，复用异步恢复流程
        private async void RestoreFilteredDetail()
        {
            JobRecord job = jobs.FirstOrDefault(record => record.manifest.jobId == selectedJobId);
            if (job == null || detailRows == null || Busy || detailStatusRefreshing)
                return;

            var targets = new List<TextureAssetInfo>();
            int changedCount = 0;
            foreach (DetailRow row in detailRows)
            {
                if (row.status == DetailStatus.Missing)
                    continue;
                if (row.status == DetailStatus.Changed)
                    changedCount++;
                targets.Add(new TextureAssetInfo { assetPath = row.assetPath, guid = row.guid });
            }
            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog("无可恢复项", "当前筛选结果中没有可恢复的资源（可能均为“无备份”）。", "确定");
                return;
            }

            int safeCount = targets.Count - changedCount;
            string body = changedCount > 0
                // 含已变化项：明确告知会覆盖当前改动
                ? $"其中 {safeCount} 个“可恢复”（与本任务输出一致）、{changedCount} 个“已变化”，恢复会覆盖这 {changedCount} 个的当前改动。\n"
                // 全部可恢复：明确不会覆盖任何改动，让用户放心
                : "全部为“可恢复”项（当前文件仍与本任务输出一致），恢复不会覆盖任何改动。\n";
            string message =
                $"将按当前筛选恢复 {targets.Count} 个资源（任务 {job.manifest.jobId}）：\n" +
                body +
                $"{EstimateRestoreTimeHint(targets.Count)}\n\n是否继续？";
            if (!EditorUtility.DisplayDialog("恢复筛选结果", message, "恢复", "取消"))
                return;

            restoring = true;
            await ExecuteRestoreAsync(
                job.directory,
                targets,
                job.manifest.jobId,
                "恢复中…",
                changedCount > 0 ? $"（含 {changedCount} 个已变化）" : string.Empty);
        }

        private static void DrawKeyValue(string key, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(key, GUILayout.Width(90f));
                EditorGUILayout.SelectableLabel(value ?? "-", GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        private void SelectJob(JobRecord job)
        {
            selectedJobId = job.manifest.jobId;
            EnsureDetailRows(job);
            GUI.FocusControl(null);
        }

        // 定位资源：优先按 GUID 解析当前路径（兼容移动/改名），成功则 ping 并选中
        private void PingAsset(string guid, string assetPath)
        {
            string path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
                path = assetPath;
            UnityEngine.Object target = string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadMainAssetAtPath(path);
            if (target != null)
            {
                EditorGUIUtility.PingObject(target);
                Selection.activeObject = target;
            }
            else
            {
                ShowNotification(new GUIContent("资源不存在或已移动"));
            }
        }

        // 按需构建并缓存所选任务的资源明细：后尺寸/倍率取 placements，前后字节读备份与暂存文件
        private void EnsureDetailRows(JobRecord job)
        {
            if (job == null)
                return;
            if (detailRowsJobId == job.manifest.jobId && allDetailRows != null)
                return;
            detailRowsJobId = job.manifest.jobId;
            allDetailRows = BuildDetailRows(job);
            // 切换任务后状态需重新计算，筛选回到“全部”
            detailStateFilter = DetailStateFilter.All;
            detailStatusReady = false;
            ApplyDetailFilter();
        }

        // 按当前状态筛选，重建绑定到表格的 detailRows 视图
        private void ApplyDetailFilter()
        {
            if (allDetailRows == null)
            {
                detailRows = null;
                return;
            }
            if (detailStateFilter == DetailStateFilter.All)
            {
                detailRows = new List<DetailRow>(allDetailRows);
                return;
            }
            DetailStatus want;
            switch (detailStateFilter)
            {
                case DetailStateFilter.Safe: want = DetailStatus.Safe; break;
                case DetailStateFilter.Changed: want = DetailStatus.Changed; break;
                default: want = DetailStatus.Missing; break;
            }
            detailRows = allDetailRows.Where(row => row.status == want).ToList();
        }

        private List<DetailRow> BuildDetailRows(JobRecord job)
        {
            var rows = new List<DetailRow>();
            UpscaleJobManifest manifest = job.manifest;
            var placements = (manifest.pages ?? new List<AtlasPageManifest>())
                .SelectMany(page => page.placements ?? new List<AtlasPlacement>())
                .GroupBy(placement => placement.assetPath, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            foreach (TextureAssetInfo asset in manifest.assets ?? new List<TextureAssetInfo>())
            {
                if (asset == null)
                    continue;
                var row = new DetailRow(this, job)
                {
                    assetPath = asset.assetPath,
                    guid = asset.guid,
                    beforeWidth = asset.width,
                    beforeHeight = asset.height,
                    // 备份文件名已改用 GUID 短名，读取时按新/旧路径回退解析
                    beforeBytes = TryFileSize(UpscaleJobStore.ResolveBackupFile(job.directory, asset))
                };
                if (placements.TryGetValue(asset.assetPath ?? string.Empty, out AtlasPlacement placement))
                {
                    row.afterWidth = placement.outputWidth;
                    row.afterHeight = placement.outputHeight;
                    row.scale = placement.scale;
                    if (!string.IsNullOrEmpty(placement.stagedFile))
                        row.afterBytes = TryFileSize(Path.Combine(job.directory, placement.stagedFile));
                }
                rows.Add(row);
            }
            return rows;
        }

        private static long TryFileSize(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return new FileInfo(path).Length;
            }
            catch
            {
                // 读取文件大小失败时按未知(0)处理，不影响其余展示
            }
            return 0;
        }

        // ==================== 运行时状态（不参与绘制） ====================

        private List<TextureAssetInfo> assets = new List<TextureAssetInfo>();
        private readonly List<TextureAssetInfo> filteredAssets = new List<TextureAssetInfo>();
        private List<JobRecord> jobs = new List<JobRecord>();
        private readonly List<string> liveLog = new List<string>();

        // 任务面板：跳过标记相关运行态
        private bool hideSkipped;
        private bool assetRowsDirty;

        // 历史页签：状态筛选、选中任务与资源明细缓存
        private enum HistoryFilter { All, Completed, Failed, Canceled, RolledBack, Processing }
        private HistoryFilter historyFilter = HistoryFilter.All;

        // 详情资源表：相对本任务的可恢复状态（需异步计算）与其筛选
        private enum DetailStatus { Unknown, Safe, Changed, Missing }
        private enum DetailStateFilter { All, Safe, Changed, Missing }
        private DetailStateFilter detailStateFilter = DetailStateFilter.All;
        private bool detailStatusReady;

        private static string DetailStatusLabel(DetailStatus state)
        {
            switch (state)
            {
                case DetailStatus.Safe: return "可恢复";
                case DetailStatus.Changed: return "已变化";
                case DetailStatus.Missing: return "无备份";
                default: return "未刷新";
            }
        }
        private string selectedJobId = string.Empty;
        private Vector2 historyListScroll;
        private Vector2 historyDetailScroll;
        // 被修改资源表：与任务面板资源表一致，使用 Odin 原生 [Searchable]+[TableList] 分页（固定每页 25 条）
        [PropertyOrder(610)]
        [ShowIf(nameof(ShowHistoryDetailTable))]
        [Title("$DetailTableTitle")]
        [Searchable]
        [TableList(IsReadOnly = true, AlwaysExpanded = true, ShowPaging = true, NumberOfItemsPerPage = 25)]
        [HideLabel]
        [ShowInInspector]
        private List<DetailRow> detailRows;
        // 全量明细（未筛选），detailRows 为其按状态筛选后的视图
        private List<DetailRow> allDetailRows;
        private string detailRowsJobId = string.Empty;

        private bool ShowHistoryDetailTable => ShowHistoryTab && allDetailRows != null && allDetailRows.Count > 0;
        private string DetailTableTitle =>
            allDetailRows != null && (detailRows?.Count ?? 0) != allDetailRows.Count
                ? $"被修改的资源（{detailRows?.Count ?? 0}/{allDetailRows.Count}）"
                : $"被修改的资源（{allDetailRows?.Count ?? 0}）";
        private GUIStyle historyEntryStyle;
        private GUIStyle HistoryEntryStyle => historyEntryStyle ??= new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleLeft,
            wordWrap = true,
            padding = new RectOffset(8, 8, 4, 4),
            fontSize = 11
        };
        private bool atlasEstimateDirty = true;
        private int estimatedAtlasPages;
        private int estimatedMaxPageEdge;
        private string atlasEstimateError = string.Empty;
        private bool memBudgetKnown;
        private bool fetchingMemory;
        private long vramFreeBytes;
        private long vramTotalBytes;
        private string memDeviceName = string.Empty;
        // 运行期间显存实测：持续采样，记录占用峰值并在接近上限时提醒（替代原“峰值倍率/安全系数”手填估算）
        private long observedPeakUsedBytes;
        private bool memWarnedHigh;
        private CancellationTokenSource memMonitorCancellation;
        private Vector2 logScroll;
        private bool running;
        private bool scanning;
        private bool testingConnection;
        private float progress;
        private string status = "就绪";
        private CancellationTokenSource runCancellation;
        private CancellationTokenSource connectionCancellation;
        private CancellationTokenSource scanCancellation;
        private bool restoring;
        private CancellationTokenSource restoreCancellation;

        // 详情表状态刷新中（异步哈希校验），期间禁用工具栏按钮
        private bool detailStatusRefreshing;

        // 运行、扫描或恢复期间都视为忙碌，统一禁用配置编辑与开始操作
        private bool Busy => running || scanning || restoring;
        private bool NotBusy => !Busy;
        private bool CanStart => !Busy && assets.Any(asset => asset.selected);
        private bool CanTestConnection => !Busy && !testingConnection;
        private bool ScanButtonEnabled => scanning || !running;
        private string ScanButtonLabel => scanning ? "取消扫描" : "递归扫描";
        private string TestConnectionLabel => testingConnection ? "连接中..." : "测试连接";
        private string RefreshMemLabel => fetchingMemory ? "读取中..." : "刷新显存";

        // 菜单入口统一注册在 UIR.EditorTools.UIRMenuRegister，此处仅暴露公开的打开方法
        public static void Open()
        {
            var window = GetWindow<ComfyUIUpscalerWindow>();
            window.titleContent = new GUIContent("ComfyUI 高清化");
            window.minSize = new Vector2(1080f, 620f);
            window.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            LoadPreferences();
            // 存储的是路径字符串，UI 用文件夹对象引用，这里把有效文件夹加载为 DefaultAsset
            skipFolders.Clear();
            foreach (string path in UpscaleSkipStore.GetFolders())
            {
                var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(path);
                if (folder != null)
                    skipFolders.Add(folder);
            }
            RefreshJobs();
            RebuildAssetRows();
        }

        protected override void OnDisable()
        {
            connectionCancellation?.Cancel();
            if (running)
                runCancellation?.Cancel();
            if (scanning)
                scanCancellation?.Cancel();
            if (restoring)
                restoreCancellation?.Cancel();
            memMonitorCancellation?.Cancel();
            SavePreferences();
            base.OnDisable();
        }

        // ==================== 扫描 / 连接 / 执行 ====================

        private async Task ScanAsync()
        {
            // 手动扫描已由禁用按钮阻止；此处仅防止重复扫描（任务结束后的自动重扫允许在 running 期间进行）
            if (scanning)
                return;

            List<string> configuredFolders = GetFolderPaths(sourceFolders);
            if (configuredFolders.Count == 0)
            {
                EditorUtility.DisplayDialog("扫描失败", "请至少选择一个 Assets 下的目录。", "确定");
                return;
            }

            scanning = true;
            scanCancellation = new CancellationTokenSource();
            progress = 0f;
            status = "扫描中...";
            try
            {
                List<TextureAssetInfo> scanned = await TextureScanner.ScanAsync(
                    configuredFolders,
                    expectedScale,
                    (value, message) =>
                    {
                        progress = value;
                        status = message;
                        SafeRepaint();
                    },
                    scanCancellation.Token);
                assets = scanned;
                RefreshAssetUpgradeStates();
                progress = 0f;
                status = $"扫描完成，共 {assets.Count} 张图片";
                SavePreferences();
            }
            catch (OperationCanceledException)
            {
                status = "扫描已取消";
            }
            catch (Exception exception)
            {
                status = "扫描失败";
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("扫描失败", exception.Message, "确定");
            }
            finally
            {
                scanning = false;
                scanCancellation?.Dispose();
                scanCancellation = null;
                SafeRepaint();
            }
        }

        private async Task TestConnectionAsync()
        {
            testingConnection = true;
            connectionCancellation?.Cancel();
            connectionCancellation = new CancellationTokenSource();
            try
            {
                var client = new ComfyUIClient(comfyUrl, requestTimeoutSeconds);
                ComfyDeviceMemory memory = await client.GetDeviceMemoryAsync(connectionCancellation.Token);
                ApplyMemoryBudget(memory);
                status = memory.hasVram
                    ? $"连接成功｜{memory.deviceName} 显存 {UpscaleJobStore.FormatBytes(memory.vramFreeBytes)}/{UpscaleJobStore.FormatBytes(memory.vramTotalBytes)}"
                    : "连接成功（未获取到显存信息）";
            }
            catch (Exception exception)
            {
                status = "连接失败: " + exception.Message;
                EditorUtility.DisplayDialog("ComfyUI 连接失败", exception.Message, "确定");
            }
            finally
            {
                testingConnection = false;
                SafeRepaint();
            }
        }

        private async void StartRun()
        {
            int selectedCount = assets.Count(asset => asset.selected);
            List<AtlasPageManifest> pages;
            try
            {
                pages = AtlasPacker.Pack(assets, padding, maxAtlasEdge, maxAtlasPixels);
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("图集参数无效", exception.Message, "确定");
                return;
            }
            int pageCount = pages.Count;

            // 开始前尽力刷新显存并做确定性的输出尺寸检查（仅通知，不阻止）
            (_, int maxPageEdge) = GetLargestPage(pages);
            await FetchMemoryBudgetAsync();
            int outputEdge = UpscaleMemoryEstimator.EstimateMaxOutputEdge(maxPageEdge, expectedScale);
            List<string> memoryWarnings = BuildMemoryWarnings(outputEdge);

            int riskyCount = assets.Count(asset => asset.selected && !string.IsNullOrEmpty(asset.warning));
            string confirmation = $"将处理 {selectedCount} 个文件，预计生成 {pageCount} 张图集，并在全部校验成功后覆盖原图。";
            if (riskyCount > 0)
                confirmation += $"\n\n其中 {riskyCount} 个文件有风险提示。";
            if (memoryWarnings.Count > 0)
                confirmation += "\n\n内存风险提示：\n- " + string.Join("\n- ", memoryWarnings);
            if (!EditorUtility.DisplayDialog("确认高清化", confirmation, "开始", "取消"))
                return;

            SavePreferences();
            liveLog.Clear();
            progress = 0f;
            status = "准备任务";
            running = true;
            runCancellation = new CancellationTokenSource();
            StartMemoryMonitor();
            try
            {
                UpscaleJobManifest result = await UpscaleJobRunner.RunAsync(
                    assets,
                    BuildSettings(),
                    (value, message) =>
                    {
                        progress = value;
                        status = message;
                        SafeRepaint();
                    },
                    message =>
                    {
                        liveLog.Add(message);
                        logScroll.y = float.MaxValue;
                        SafeRepaint();
                    },
                    runCancellation.Token);
                status = "完成: " + result.jobId + " | " + UpscaleJobStore.FormatSizeSummary(result);
                progress = 1f;
                await ScanAsync();
            }
            catch (OperationCanceledException)
            {
                status = "已中断，进度已保留，可从历史任务继续。源资源未修改";
            }
            catch (Exception exception)
            {
                status = "失败: " + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("高清化失败", exception.Message, "确定");
            }
            finally
            {
                StopMemoryMonitor();
                running = false;
                runCancellation.Dispose();
                runCancellation = null;
                RefreshJobs();
                if (assets.Count > 0)
                    RefreshAssetUpgradeStates();
                SafeRepaint();
            }
        }

        private async void Resume(JobRecord job)
        {
            List<string> conflicts = UpscaleJobStore.GetResumeConflicts(job.directory);
            if (conflicts.Count > 0)
            {
                string details = string.Join("\n", conflicts.Take(8));
                if (conflicts.Count > 8)
                    details += $"\n……另有 {conflicts.Count - 8} 项";
                EditorUtility.DisplayDialog("无法继续", "任务无法安全继续：\n\n" + details, "确定");
                return;
            }

            int done = job.manifest.pages.Count(page => !string.IsNullOrEmpty(page.outputFile));
            string message = $"将从任务 {job.manifest.jobId} 继续：共 {job.manifest.pages.Count} 张图集，已完成 {done} 张，" +
                             "剩余部分会重新提交 ComfyUI，全部完成并校验后覆盖原图。";
            if (!EditorUtility.DisplayDialog("继续任务", message, "继续", "取消"))
                return;

            liveLog.Clear();
            progress = 0f;
            status = "准备继续任务";
            running = true;
            runCancellation = new CancellationTokenSource();
            StartMemoryMonitor();
            try
            {
                UpscaleJobManifest result = await UpscaleJobRunner.ResumeAsync(
                    job,
                    Mathf.Max(1, requestTimeoutSeconds),
                    Mathf.Max(1, jobTimeoutMinutes),
                    jpegQuality,
                    (value, msg) =>
                    {
                        progress = value;
                        status = msg;
                        SafeRepaint();
                    },
                    msg =>
                    {
                        liveLog.Add(msg);
                        logScroll.y = float.MaxValue;
                        SafeRepaint();
                    },
                    runCancellation.Token);
                status = "完成: " + result.jobId + " | " + UpscaleJobStore.FormatSizeSummary(result);
                progress = 1f;
                await ScanAsync();
            }
            catch (OperationCanceledException)
            {
                status = "已中断，进度已保留，可再次继续";
            }
            catch (Exception exception)
            {
                status = "继续失败: " + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("继续失败", exception.Message, "确定");
            }
            finally
            {
                StopMemoryMonitor();
                running = false;
                runCancellation.Dispose();
                runCancellation = null;
                RefreshJobs();
                if (assets.Count > 0)
                    RefreshAssetUpgradeStates();
                SafeRepaint();
            }
        }

        // 整任务恢复：异步校验 → 详细确认（部分/强制/取消）→ 异步执行，全程可中断并显示进度
        private async void Restore(JobRecord job)
        {
            if (Busy)
                return;
            string directory = job.directory;
            string jobId = job.manifest.jobId;

            // 阶段一：异步校验，构建恢复计划（逐张哈希比对较重，时间分片 + 可中断）
            restoring = true;
            progress = 0f;
            status = "校验可恢复项…";
            restoreCancellation = new CancellationTokenSource();
            RestorePlan plan;
            try
            {
                plan = await UpscaleJobStore.BuildRestorePlanAsync(
                    directory,
                    (value, message) =>
                    {
                        progress = value;
                        status = message;
                        SafeRepaint();
                    },
                    restoreCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                status = "恢复校验已取消";
                FinishRestore();
                return;
            }
            catch (Exception exception)
            {
                status = "恢复校验失败: " + exception.Message;
                Debug.LogException(exception);
                FinishRestore();
                EditorUtility.DisplayDialog("恢复失败", exception.Message, "确定");
                return;
            }

            if (plan.TotalAssets == 0)
            {
                status = "该任务没有可恢复的资源";
                FinishRestore();
                EditorUtility.DisplayDialog("无法恢复", "该任务没有记录任何资源。", "确定");
                return;
            }

            // 阶段二：详细确认框（数量 / 体积 / 安全vs变化 / 粗估时间 / 部分或强制）
            int safe = plan.safeAssets.Count;
            int changed = plan.changedAssets.Count;
            int missing = plan.missingNotes.Count;
            bool force = false;
            List<TextureAssetInfo> targets;

            if (changed == 0 && missing == 0)
            {
                string message = $"任务 {jobId}\n\n" +
                                 $"将用备份覆盖 {safe} 个资源（含 .meta），写入体积约 {UpscaleJobStore.FormatBytes(plan.safeBytes)}。\n" +
                                 EstimateRestoreTimeHint(safe) + "\n\n" +
                                 "恢复后这些资源标记为“已回滚”，操作可中断。";
                if (!EditorUtility.DisplayDialog("恢复任务", message, "开始恢复", "取消"))
                {
                    status = "已取消恢复";
                    FinishRestore();
                    return;
                }
                targets = plan.safeAssets;
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"任务 {jobId} 校验结果：");
                sb.AppendLine($"· 可安全恢复：{safe} 个（约 {UpscaleJobStore.FormatBytes(plan.safeBytes)}）");
                if (changed > 0)
                    sb.AppendLine($"· 任务完成后已变化：{changed} 个（默认跳过，避免覆盖你的新改动）");
                if (missing > 0)
                    sb.AppendLine($"· 备份缺失/无法恢复：{missing} 个（始终跳过）");
                sb.AppendLine();
                sb.AppendLine(EstimateRestoreTimeHint(safe));
                sb.AppendLine();
                sb.AppendLine("“恢复安全项”：仅恢复未变化的资源。");

                if (changed > 0)
                {
                    sb.AppendLine($"“强制全部恢复”：连同 {changed} 个已变化项一并用备份覆盖" +
                                  $"（丢弃这些改动，约 {UpscaleJobStore.FormatBytes(plan.restorableBytes)}）。");
                    // 返回值：0=第一个(恢复安全项)，1=第二个(取消)，2=第三个(强制全部恢复)
                    int choice = EditorUtility.DisplayDialogComplex(
                        "恢复任务", sb.ToString(), "恢复安全项", "取消", "强制全部恢复");
                    if (choice == 1)
                    {
                        status = "已取消恢复";
                        FinishRestore();
                        return;
                    }
                    if (choice == 2)
                    {
                        force = true;
                        targets = new List<TextureAssetInfo>(plan.safeAssets);
                        targets.AddRange(plan.changedAssets);
                    }
                    else
                    {
                        targets = plan.safeAssets;
                    }
                }
                else
                {
                    // 只有缺失备份的项无法恢复，其余均安全：两按钮确认即可
                    if (!EditorUtility.DisplayDialog("恢复任务", sb.ToString(), "恢复安全项", "取消"))
                    {
                        status = "已取消恢复";
                        FinishRestore();
                        return;
                    }
                    targets = plan.safeAssets;
                }
            }

            if (targets.Count == 0)
            {
                status = "没有可安全恢复的资源";
                FinishRestore();
                EditorUtility.DisplayDialog(
                    "无可恢复项",
                    "没有可安全恢复的资源。如需覆盖已改动的资源，请选择“强制全部恢复”。",
                    "确定");
                return;
            }

            // 阶段三：异步执行恢复（分片复制 + 进度 + 可中断，末尾一次导入）
            string doneSuffix = !force && changed > 0 ? $"（跳过 {changed} 个已变化）" : string.Empty;
            await ExecuteRestoreAsync(directory, targets, jobId, force ? "强制恢复中…" : "恢复中…", doneSuffix);
        }

        // 异步执行恢复的公用实现：分片复制 + 进度 + 可中断，末尾一次导入；调用前应已设置 restoring=true
        private async Task ExecuteRestoreAsync(
            string directory,
            IList<TextureAssetInfo> targets,
            string jobId,
            string busyStatus,
            string doneSuffix)
        {
            progress = 0f;
            status = busyStatus;
            restoreCancellation?.Dispose();
            restoreCancellation = new CancellationTokenSource();
            try
            {
                int restored = await UpscaleJobStore.RestoreAssetsAsync(
                    directory,
                    targets,
                    (value, message) =>
                    {
                        progress = value;
                        status = message;
                        SafeRepaint();
                    },
                    restoreCancellation.Token);
                progress = 1f;
                status = $"已恢复 {restored} 个资源：{jobId}{doneSuffix}";
            }
            catch (OperationCanceledException)
            {
                status = "恢复已中断，已恢复的资源保持不变";
            }
            catch (Exception exception)
            {
                status = "恢复失败: " + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("恢复失败", exception.Message, "确定");
            }
            finally
            {
                FinishRestore();
                RefreshJobs();
                if (assets.Count > 0)
                    RefreshAssetUpgradeStates();
                SafeRepaint();
            }
        }

        // 收尾：释放恢复取消源并复位忙碌标记
        private void FinishRestore()
        {
            restoring = false;
            restoreCancellation?.Dispose();
            restoreCancellation = null;
            SafeRepaint();
        }

        // 粗估恢复耗时：文件复制很快，真实耗时主要在末尾的一次性资源导入，波动较大，只给量级提示
        private static string EstimateRestoreTimeHint(int count)
        {
            if (count <= 0)
                return "预计耗时：几乎瞬间。";
            if (count <= 100)
                return "预计耗时：数秒（复制很快，导入约几秒）。";
            if (count <= 1000)
                return "预计耗时：数十秒（导入阶段会短暂占用主线程）。";
            return "预计耗时：数分钟（末尾一次性导入可能明显占用主线程，请耐心等待）。";
        }

        private UpscalerRunSettings BuildSettings()
        {
            List<string> configuredFolders = GetFolderPaths(sourceFolders);
            return new UpscalerRunSettings
            {
                sourceFolder = configuredFolders.FirstOrDefault() ?? string.Empty,
                sourceFolders = configuredFolders,
                comfyUrl = comfyUrl,
                workflowPath = workflowPath,
                inputNodeId = inputNodeId.Trim(),
                inputFieldName = inputFieldName.Trim(),
                outputNodeId = outputNodeId.Trim(),
                expectedScale = expectedScale,
                padding = padding,
                maxAtlasEdge = maxAtlasEdge,
                maxAtlasPixels = maxAtlasPixels,
                requestTimeoutSeconds = Mathf.Max(1, requestTimeoutSeconds),
                jobTimeoutMinutes = Mathf.Max(1, jobTimeoutMinutes),
                jpegQuality = jpegQuality,
                keepDisplaySize = keepDisplaySize
            };
        }

        // ==================== 数据刷新与辅助 ====================

        private void RefreshJobs()
        {
            jobs = UpscaleJobStore.List();
            // 任务列表变化后作废详情缓存；选中项失效时回退到最新任务
            detailRows = null;
            allDetailRows = null;
            detailStatusReady = false;
            detailStateFilter = DetailStateFilter.All;
            detailRowsJobId = string.Empty;
            if (jobs.All(job => job.manifest.jobId != selectedJobId))
                selectedJobId = jobs.FirstOrDefault()?.manifest.jobId ?? string.Empty;
        }

        // 异步任务进行中窗口可能被关闭，销毁后调用 Repaint 会报错，这里借助 UnityEngine.Object 的隐式布尔判断守卫
        private void SafeRepaint()
        {
            if (this)
                Repaint();
        }

        private void RefreshAssetUpgradeStates()
        {
            try
            {
                UpgradeAssetIndexStore.ApplyToAssets(assets);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("无法读取升级映射表，本次扫描暂按未升级显示。\n" + exception);
                foreach (TextureAssetInfo asset in assets)
                    UpgradeAssetStateUtility.Apply(asset, null);
            }
            // 应用“跳过”标记：命中的资源默认不参与升级
            UpscaleSkipStore.ApplyToAssets(assets);
            foreach (TextureAssetInfo asset in assets)
                if (asset.skipped)
                    asset.selected = false;
            RebuildAssetRows();
            atlasEstimateDirty = true;
        }

        private void OnFilterChanged()
        {
            RebuildAssetRows();
            SavePreferences();
        }

        // 依据状态筛选重建匹配集合与表格行；表内文本搜索由 Odin [Searchable] 处理
        private void RebuildAssetRows()
        {
            filteredAssets.Clear();
            foreach (TextureAssetInfo asset in assets)
            {
                if (!UpgradeAssetStateUtility.MatchesFilter(asset, assetUpgradeFilter))
                    continue;
                if (hideSkipped && asset.skipped)
                    continue;
                filteredAssets.Add(asset);
            }
            assetRows = filteredAssets.Select(asset => new AssetRow(this, asset)).ToList();
        }

        private void SetFilteredSelection(Func<TextureAssetInfo, bool> selector)
        {
            foreach (TextureAssetInfo asset in filteredAssets)
                asset.selected = selector(asset);
            atlasEstimateDirty = true;
        }

        private void MarkEstimateDirty()
        {
            atlasEstimateDirty = true;
        }

        private void RefreshAtlasEstimate()
        {
            if (!atlasEstimateDirty)
                return;

            try
            {
                if (assets.Any(asset => asset.selected))
                {
                    List<AtlasPageManifest> pages = AtlasPacker.Pack(assets, padding, maxAtlasEdge, maxAtlasPixels);
                    estimatedAtlasPages = pages.Count;
                    (_, estimatedMaxPageEdge) = GetLargestPage(pages);
                }
                else
                {
                    estimatedAtlasPages = 0;
                    estimatedMaxPageEdge = 0;
                }
                atlasEstimateError = string.Empty;
            }
            catch (Exception exception)
            {
                estimatedAtlasPages = 0;
                estimatedMaxPageEdge = 0;
                atlasEstimateError = exception.Message;
            }
            atlasEstimateDirty = false;
        }

        private static (long pixels, int edge) GetLargestPage(IEnumerable<AtlasPageManifest> pages)
        {
            long pixels = 0;
            int edge = 0;
            foreach (AtlasPageManifest page in pages)
            {
                long area = (long)page.width * page.height;
                if (area > pixels)
                    pixels = area;
                int maxSide = Mathf.Max(page.width, page.height);
                if (maxSide > edge)
                    edge = maxSide;
            }
            return (pixels, edge);
        }

        private void ApplyMemoryBudget(ComfyDeviceMemory memory)
        {
            memBudgetKnown = memory != null && memory.hasVram;
            if (!memBudgetKnown)
                return;
            vramFreeBytes = memory.vramFreeBytes;
            vramTotalBytes = memory.vramTotalBytes;
            memDeviceName = memory.deviceName ?? string.Empty;
        }

        private async Task FetchMemoryBudgetAsync()
        {
            if (fetchingMemory)
                return;
            fetchingMemory = true;
            try
            {
                var client = new ComfyUIClient(comfyUrl, Mathf.Max(1, requestTimeoutSeconds));
                ComfyDeviceMemory memory = await client.GetDeviceMemoryAsync(CancellationToken.None);
                ApplyMemoryBudget(memory);
            }
            catch (Exception exception)
            {
                memBudgetKnown = false;
                status = "读取显存失败: " + exception.Message;
            }
            finally
            {
                fetchingMemory = false;
                SafeRepaint();
            }
        }

        // 运行期间启动显存监视：清零实测峰值并开始后台采样
        private void StartMemoryMonitor()
        {
            observedPeakUsedBytes = 0;
            memWarnedHigh = false;
            memMonitorCancellation = new CancellationTokenSource();
            _ = MonitorMemoryAsync(memMonitorCancellation.Token);
        }

        private void StopMemoryMonitor()
        {
            memMonitorCancellation?.Cancel();
            memMonitorCancellation?.Dispose();
            memMonitorCancellation = null;
        }

        // 后台每 2 秒读一次 ComfyUI 显存，更新可用显存、记录占用峰值，并在接近上限时向日志提醒一次
        private async Task MonitorMemoryAsync(CancellationToken token)
        {
            var client = new ComfyUIClient(comfyUrl, Mathf.Max(1, requestTimeoutSeconds));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ComfyDeviceMemory memory = await client.GetDeviceMemoryAsync(token);
                    ApplyMemoryBudget(memory);
                    if (memory != null && memory.hasVram && memory.vramTotalBytes > 0)
                    {
                        long used = memory.vramTotalBytes - memory.vramFreeBytes;
                        if (used > observedPeakUsedBytes)
                            observedPeakUsedBytes = used;
                        if (!memWarnedHigh && memory.vramFreeBytes < memory.vramTotalBytes * 0.08)
                        {
                            memWarnedHigh = true;
                            liveLog.Add("⚠ 显存已接近上限，若某页失败(OOM) 请调小最大像素数/边长后从历史任务继续");
                            logScroll.y = float.MaxValue;
                        }
                        SafeRepaint();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 单次采样失败忽略，继续下次
                }

                try
                {
                    await Task.Delay(2000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // 组装风险提示（仅通知，不阻止执行）：输出尺寸为确定性检查；显存改为实测接近上限时提醒
        private List<string> BuildMemoryWarnings(int outputEdge)
        {
            var warnings = new List<string>();
            if (outputEdge > UpscaleMemoryEstimator.UnityMaxTextureEdge)
                warnings.Add($"输出图集单边约 {outputEdge} 超过 Unity 上限 {UpscaleMemoryEstimator.UnityMaxTextureEdge}，必然失败，请降低最大边长。");
            // 运行中实测可用显存低于总量 8% 时提醒（接近 OOM）
            if (memBudgetKnown && vramTotalBytes > 0 && vramFreeBytes > 0 &&
                vramFreeBytes < vramTotalBytes * 0.08)
                warnings.Add($"显存已接近上限（剩余 {UpscaleJobStore.FormatBytes(vramFreeBytes)}）。若某页失败(OOM)，请调小最大像素数或最大边长后从历史任务继续。");
            return warnings;
        }

        private static string BuildUpgradeSummary(TextureAssetInfo asset)
        {
            if (string.IsNullOrEmpty(asset.lastUpgradeJobId))
                return "-";
            string date = UpgradeAssetStateUtility.GetLocalDate(asset.lastUpgradeUtc);
            return $"{asset.lastInputWidth}x{asset.lastInputHeight} -> " +
                   $"{asset.lastOutputWidth}x{asset.lastOutputHeight} / " +
                   $"{asset.lastActualScale:0.##}x / {date}";
        }

        private static string BuildUpgradeTooltip(TextureAssetInfo asset)
        {
            var lines = new List<string> { UpgradeAssetStateUtility.GetLabel(asset) };
            if (!string.IsNullOrEmpty(asset.lastUpgradeJobId))
            {
                lines.Add("成功任务: " + asset.lastUpgradeJobId);
                lines.Add("完成时间: " + UpgradeAssetStateUtility.GetLocalDate(asset.lastUpgradeUtc));
                if (!string.IsNullOrEmpty(asset.workflowSha256))
                    lines.Add("工作流 SHA-256: " + asset.workflowSha256);
            }
            if (asset.lastAttemptFailed)
            {
                lines.Add("最后尝试: " + asset.lastAttemptJobId);
                lines.Add("尝试时间: " + UpgradeAssetStateUtility.GetLocalDate(asset.lastAttemptUtc));
                lines.Add("尝试状态: " + asset.lastAttemptStatus);
            }
            return string.Join("\n", lines);
        }

        private static string BuildBorderSummary(TextureAssetInfo asset)
        {
            if (asset.spriteMode == SpriteImportMode.Single.ToString())
                return HasBorder(asset.singleBorder) ? "Border " + FormatVector(asset.singleBorder) : "-";
            if (asset.spriteMode == SpriteImportMode.Multiple.ToString())
            {
                int bordered = asset.sprites.Count(sprite => HasBorder(sprite.border));
                return $"{asset.sprites.Count} Sprites / {bordered} Border";
            }
            return "-";
        }

        private static bool HasBorder(Vector4 border)
        {
            return border.x != 0f || border.y != 0f || border.z != 0f || border.w != 0f;
        }

        private static string FormatVector(Vector4 value)
        {
            return $"({value.x:g},{value.y:g},{value.z:g},{value.w:g})";
        }

        // ==================== 偏好持久化 ====================

        // 读取历史保存的扫描目录路径（新旧两个偏好键），供 LoadPreferences 转成文件夹对象引用
        private static List<string> ReadSavedFolderPaths()
        {
            var paths = new List<string>();
            string serialized = EditorPrefs.GetString(PrefPrefix + "SourceFolders", string.Empty);
            if (!string.IsNullOrEmpty(serialized))
                paths.AddRange(serialized
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => path.Trim().Replace((char)92, '/'))
                    .Where(path => !string.IsNullOrEmpty(path)));
            if (paths.Count == 0)
            {
                string legacy = EditorPrefs.GetString(PrefPrefix + "SourceFolder", string.Empty);
                if (!string.IsNullOrWhiteSpace(legacy))
                    paths.Add(legacy.Trim().Replace((char)92, '/'));
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void LoadPreferences()
        {
            sourceFolders.Clear();
            foreach (string path in ReadSavedFolderPaths())
            {
                var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(path);
                if (folder != null)
                    sourceFolders.Add(folder);
            }

            comfyUrl = EditorPrefs.GetString(PrefPrefix + "ComfyUrl", "http://127.0.0.1:8188");
            workflowPath = EditorPrefs.GetString(PrefPrefix + "WorkflowPath", string.Empty);
            inputNodeId = EditorPrefs.GetString(PrefPrefix + "InputNodeId", string.Empty);
            inputFieldName = EditorPrefs.GetString(PrefPrefix + "InputFieldName", "image");
            outputNodeId = EditorPrefs.GetString(PrefPrefix + "OutputNodeId", string.Empty);
            expectedScale = EditorPrefs.HasKey(PrefPrefix + "ExpectedScaleFloat")
                ? EditorPrefs.GetFloat(PrefPrefix + "ExpectedScaleFloat", 4f)
                : EditorPrefs.GetInt(PrefPrefix + "ExpectedScale", 4);
            padding = EditorPrefs.GetInt(PrefPrefix + "Padding", 32);
            maxAtlasEdge = EditorPrefs.GetInt(PrefPrefix + "MaxAtlasEdge", 4096);
            maxAtlasPixels = long.TryParse(EditorPrefs.GetString(PrefPrefix + "MaxAtlasPixels", "16777216"), out long pixels)
                ? pixels
                : 16777216;
            requestTimeoutSeconds = EditorPrefs.GetInt(PrefPrefix + "RequestTimeout", 120);
            jobTimeoutMinutes = EditorPrefs.GetInt(PrefPrefix + "JobTimeout", 30);
            jpegQuality = EditorPrefs.GetInt(PrefPrefix + "JpegQuality", 95);
            keepDisplaySize = EditorPrefs.GetBool(PrefPrefix + "KeepDisplaySize", true);
            assetUpgradeFilter = (UpgradeAssetFilter)Mathf.Clamp(
                EditorPrefs.GetInt(PrefPrefix + "UpgradeAssetFilter", 0),
                0,
                Enum.GetValues(typeof(UpgradeAssetFilter)).Length - 1);
        }

        private void SavePreferences()
        {
            List<string> configuredFolders = GetFolderPaths(sourceFolders);
            EditorPrefs.SetString(PrefPrefix + "SourceFolders", string.Join("\n", configuredFolders));
            EditorPrefs.SetString(PrefPrefix + "SourceFolder", configuredFolders.FirstOrDefault() ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "ComfyUrl", comfyUrl ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "WorkflowPath", workflowPath ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "InputNodeId", inputNodeId ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "InputFieldName", inputFieldName ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "OutputNodeId", outputNodeId ?? string.Empty);
            EditorPrefs.SetFloat(PrefPrefix + "ExpectedScaleFloat", expectedScale);
            EditorPrefs.SetInt(PrefPrefix + "Padding", padding);
            EditorPrefs.SetInt(PrefPrefix + "MaxAtlasEdge", maxAtlasEdge);
            EditorPrefs.SetString(PrefPrefix + "MaxAtlasPixels", maxAtlasPixels.ToString());
            EditorPrefs.SetInt(PrefPrefix + "RequestTimeout", requestTimeoutSeconds);
            EditorPrefs.SetInt(PrefPrefix + "JobTimeout", jobTimeoutMinutes);
            EditorPrefs.SetInt(PrefPrefix + "JpegQuality", jpegQuality);
            EditorPrefs.SetBool(PrefPrefix + "KeepDisplaySize", keepDisplaySize);
            EditorPrefs.SetInt(PrefPrefix + "UpgradeAssetFilter", (int)assetUpgradeFilter);
        }

        // ==================== 表格行类型 ====================

        // 资源表行：勾选列可编辑并回写到源资源，其余列在构造时算好为只读公共字段
        // （Odin TableList 对公共字段按“整格显示值、无行内标签”渲染，避免只读属性带标签的问题）
        private sealed class AssetRow
        {
            private readonly ComfyUIUpscalerWindow owner;
            private readonly TextureAssetInfo asset;

            public AssetRow(ComfyUIUpscalerWindow owner, TextureAssetInfo asset)
            {
                this.owner = owner;
                this.asset = asset;
            }

            [TableColumnWidth(56)]
            [HideLabel]
            [ShowInInspector]
            public bool Selected
            {
                get => asset.selected;
                set
                {
                    if (asset.selected == value)
                        return;
                    asset.selected = value;
                    owner.MarkEstimateDirty();
                }
            }

            // 逐行“跳过”标记：写入 UpscaleSkipStore（按 GUID），勾选时同时取消该资源的选择
            [TableColumnWidth(56)]
            [HideLabel]
            [ShowInInspector]
            [PropertyTooltip("勾选表示该资源不升级；批量选择会自动跳过它")]
            public bool Skip
            {
                get => asset.skipped;
                set
                {
                    if (asset.skipped == value)
                        return;
                    asset.skipped = value;
                    UpscaleSkipStore.SetAssetSkipped(asset.guid, value);
                    if (value)
                        asset.selected = false;
                    owner.OnSkipToggled();
                }
            }

            [TableColumnWidth(240)]
            [DisplayAsString]
            [HideLabel]
            [PropertyTooltip("$Path")]
            [ShowInInspector]
            public string Path => asset.assetPath;

            [TableColumnWidth(120)]
            [DisplayAsString]
            [HideLabel]
            [PropertyTooltip("$UpgradeTooltip")]
            [ShowInInspector]
            public string State => UpgradeAssetStateUtility.GetLabel(asset);

            [TableColumnWidth(82)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string Size => $"{asset.width}x{asset.height}";

            [TableColumnWidth(220)]
            [DisplayAsString]
            [HideLabel]
            [PropertyTooltip("$UpgradeTooltip")]
            [ShowInInspector]
            public string Record => BuildUpgradeSummary(asset);

            [TableColumnWidth(80)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string Type => asset.textureType;

            [TableColumnWidth(72)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string Sprite => asset.spriteMode;

            [TableColumnWidth(180)]
            [DisplayAsString]
            [HideLabel]
            [PropertyTooltip("$Border")]
            [ShowInInspector]
            public string Border
            {
                get
                {
                    string border = BuildBorderSummary(asset);
                    return string.IsNullOrEmpty(asset.warning) ? border : border + "  " + asset.warning;
                }
            }

            private string UpgradeTooltip => BuildUpgradeTooltip(asset);
        }

        // 历史详情里“被修改资源”单行的展示数据（构建时算好，绘制只读）
        private sealed class DetailRow
        {
            private readonly ComfyUIUpscalerWindow owner;
            private readonly JobRecord job;

            // 原始数据字段：仅用于构建与展示计算，[HideInTables] 使其不作为表格列绘制（避免变成输入框/重复列）
            [HideInTables] public string assetPath;
            [HideInTables] public string guid;
            [HideInTables] public int beforeWidth;
            [HideInTables] public int beforeHeight;
            [HideInTables] public int afterWidth;
            [HideInTables] public int afterHeight;
            [HideInTables] public float scale;
            [HideInTables] public long beforeBytes;
            [HideInTables] public long afterBytes;

            public DetailRow(ComfyUIUpscalerWindow owner, JobRecord job)
            {
                this.owner = owner;
                this.job = job;
            }

            // 展示资源名（悬浮显示完整路径）；完整路径仍参与 [Searchable] 搜索
            [TableColumnWidth(280)]
            [DisplayAsString]
            [HideLabel]
            [PropertyTooltip("$assetPath")]
            [ShowInInspector]
            public string Name => string.IsNullOrEmpty(assetPath) ? "-" : System.IO.Path.GetFileName(assetPath);

            [TableColumnWidth(150)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string Size
            {
                get
                {
                    string after = afterWidth > 0 && afterHeight > 0 ? $"{afterWidth}x{afterHeight}" : "-";
                    return $"{beforeWidth}x{beforeHeight} → {after}";
                }
            }

            [TableColumnWidth(160)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string Bytes
            {
                get
                {
                    string before = beforeBytes > 0 ? UpscaleJobStore.FormatBytes(beforeBytes) : "-";
                    string after = afterBytes > 0 ? UpscaleJobStore.FormatBytes(afterBytes) : "-";
                    return $"{before} → {after}";
                }
            }

            [TableColumnWidth(56)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string Scale => scale > 0f ? $"{scale:0.##}x" : "-";

            // 相对本任务的当前状态；点“刷新状态”后由 BuildRestorePlanAsync 计算填充
            [HideInTables] public DetailStatus status = DetailStatus.Unknown;

            [TableColumnWidth(84)]
            [DisplayAsString]
            [HideLabel]
            [PropertyTooltip("相对本任务的当前状态：可恢复=当前文件仍与本任务输出一致；已变化=已改动或已回滚；无备份=无法恢复。需点“刷新状态”计算。")]
            [ShowInInspector]
            public string StatusText => DetailStatusLabel(status);

            // 变化详情：区分“已移动/图片内容/元数据(.meta)”等具体原因，刷新状态后填充
            [HideInTables] public string changeDetail = string.Empty;

            [TableColumnWidth(190)]
            [DisplayAsString]
            [HideLabel]
            [PropertyTooltip("$changeDetail")]
            [ShowInInspector]
            public string Detail => string.IsNullOrEmpty(changeDetail) ? "-" : changeDetail;

            [TableColumnWidth(60, false)]
            [Button("定位")]
            [PropertyTooltip("在工程窗口中定位并高亮该资源")]
            private void Locate() => owner?.PingAsset(guid, assetPath);

            // 仅当该资源存在备份且未在忙碌时允许单独回滚
            [TableColumnWidth(60, false)]
            [EnableIf(nameof(CanRestore))]
            [Button("恢复")]
            [PropertyTooltip("用本任务的备份单独回滚该资源（含 .meta）")]
            private void RestoreOne() => owner?.RestoreSingleAsset(job, this);

            private bool CanRestore => owner != null && !owner.Busy && beforeBytes > 0;
        }
    }
}
