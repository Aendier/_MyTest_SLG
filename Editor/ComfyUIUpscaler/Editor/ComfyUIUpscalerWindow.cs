using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    // ComfyUI 侧内存预估（纯计算）。仅用于提示，不参与实际处理。
    internal static class UpscaleMemoryEstimator
    {
        // float32 RGB 每像素字节数：3 通道 × 4 字节
        public const int Float32RgbBytesPerPixel = 12;

        // Unity Texture2D 单边上限，输出图集超过必然失败
        public const int UnityMaxTextureEdge = 16384;

        // 估算单页在 ComfyUI 侧的峰值张量占用：输入页像素 × 峰值倍率² × 12 字节 × 安全系数
        public static long EstimatePeakBytes(long maxPagePixels, float peakScale, int safetyFactor)
        {
            if (maxPagePixels <= 0 || peakScale <= 0f)
                return 0;
            double bytes = (double)maxPagePixels * peakScale * peakScale *
                           Float32RgbBytesPerPixel * Mathf.Max(1, safetyFactor);
            return bytes >= long.MaxValue ? long.MaxValue : (long)bytes;
        }

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

        // 三块配置各自的盒子标题（用 Odin BoxGroup 分区）
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

        [BoxGroup(BoxSource)]
        [PropertyOrder(100)]
        [ShowIf(nameof(ShowTaskTab))]
        [LabelText("扫描目录")]
        [PropertyTooltip("填入或选择 Assets 下的文件夹（工程相对路径）；非法目录会被自动剔除")]
        [ShowInInspector]
        [ListDrawerSettings(ShowFoldout = false, DraggableItems = false)]
        [FolderPath(RequireExistingPath = true)]
        [EnableIf(nameof(NotBusy))]
        [OnValueChanged(nameof(OnFoldersChanged), IncludeChildren = true)]
        private readonly List<string> sourceFolders = new List<string>();

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
        private float peakScale = 4f;
        private int padding = 32;
        private int maxAtlasEdge = 4096;
        private long maxAtlasPixels = 16777216;
        private int memorySafetyFactor = 4;
        private int jpegQuality = 95;

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
                    expectedScale = Mathf.Max(1f, EditorGUILayout.FloatField("预期放大倍率", expectedScale));
                    peakScale = Mathf.Max(1f, EditorGUILayout.FloatField(
                        new GUIContent("峰值倍率(内存估算)", "工作流内部的最大放大倍率，仅用于内存预估，不参与实际处理。若模型内部先放大再缩小，请填内部峰值而非最终倍率。"),
                        peakScale));
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    padding = Mathf.Max(0, EditorGUILayout.IntField("Padding", padding));
                    maxAtlasEdge = EditorGUILayout.IntPopup("最大边长", maxAtlasEdge, AtlasEdgeLabels, AtlasEdgeValues);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    maxAtlasPixels = Math.Max(4096L, EditorGUILayout.LongField("最大像素数", maxAtlasPixels));
                    memorySafetyFactor = Mathf.Clamp(EditorGUILayout.IntField(
                        new GUIContent("内存安全系数", "并发中间张量份数的经验系数，仅用于内存预估，越大越保守（建议 3~6）。"),
                        memorySafetyFactor), 1, 16);
                }
                jpegQuality = EditorGUILayout.IntSlider("JPG 质量", jpegQuality, 1, 100);
                if (EditorGUI.EndChangeCheck())
                    MarkEstimateDirty();
            }
        }

        // ==================== 待处理资源 ====================

        private static readonly string[] UpgradeFilterLabels =
            { "全部状态", "未升级", "已升级", "已变化", "上次失败", "已回滚" };

        private string AssetSummary =>
            $"已选 {assets.Count(asset => asset.selected)}/{assets.Count}，筛选后 {filteredAssets.Count}";

        private UpgradeAssetFilter assetUpgradeFilter = UpgradeAssetFilter.All;

        // 一行横排的资源工具条：统计 + 状态筛选下拉 + 批量选择按钮（用 IMGUI 保证紧凑横向布局）
        [PropertyOrder(420)]
        [ShowIf(nameof(ShowTaskTab))]
        [OnInspectorGUI]
        private void DrawAssetToolbar()
        {
            EditorGUILayout.LabelField("待处理资源", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(Busy))
            {
                EditorGUILayout.LabelField(AssetSummary, GUILayout.MinWidth(180f));
                GUILayout.Label("状态", GUILayout.Width(30f));
                EditorGUI.BeginChangeCheck();
                int updated = EditorGUILayout.Popup((int)assetUpgradeFilter, UpgradeFilterLabels, GUILayout.Width(90f));
                if (EditorGUI.EndChangeCheck())
                {
                    assetUpgradeFilter = (UpgradeAssetFilter)updated;
                    OnFilterChanged();
                }
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

        private void SelectAll() => SetFilteredSelection(_ => true);

        private void SelectSafeOnly() => SetFilteredSelection(asset => string.IsNullOrEmpty(asset.warning));

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

            // 内存预估与显存提示（仅通知，不阻止执行）
            long peakBytes = UpscaleMemoryEstimator.EstimatePeakBytes(estimatedMaxPagePixels, peakScale, memorySafetyFactor);
            int outputEdge = UpscaleMemoryEstimator.EstimateMaxOutputEdge(estimatedMaxPageEdge, expectedScale);
            string peakText = estimatedMaxPagePixels > 0
                ? $"单页峰值≈{UpscaleJobStore.FormatBytes(peakBytes)}（{peakScale:0.##}x² × k{memorySafetyFactor}）"
                : "单页峰值：未选择资源";
            string budgetText = memBudgetKnown
                ? $"可用显存 {UpscaleJobStore.FormatBytes(vramFreeBytes)}/{UpscaleJobStore.FormatBytes(vramTotalBytes)}（{memDeviceName}）"
                : "可用显存未知（点“测试连接”或“刷新显存”）";
            EditorGUILayout.LabelField(peakText + "｜" + budgetText);
            foreach (string warning in BuildMemoryWarnings(peakBytes, outputEdge))
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

        // ==================== 历史记录页签 ====================

        [PropertyOrder(600)]
        [ShowIf(nameof(ShowHistoryTab))]
        [OnInspectorGUI]
        private void DrawHistoryHeader()
        {
            EditorGUILayout.LabelField("历史任务与恢复", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("打开映射", GUILayout.Width(90f)))
                    OpenIndexMap();
                if (GUILayout.Button("刷新", GUILayout.Width(70f)))
                    RefreshJobs();
                GUILayout.FlexibleSpace();
            }
        }

        private void OpenIndexMap()
        {
            if (File.Exists(UpgradeAssetIndexStore.IndexPath))
                EditorUtility.RevealInFinder(UpgradeAssetIndexStore.IndexPath);
            else
                ShowNotification(new GUIContent("映射表尚未生成"));
        }

        [PropertyOrder(610)]
        [ShowIf(nameof(ShowHistoryTab))]
        [ShowInInspector]
        [TableList(IsReadOnly = true, AlwaysExpanded = true, HideToolbar = true)]
        [HideLabel]
        private List<HistoryRow> historyRows = new List<HistoryRow>();

        // ==================== 运行时状态（不参与绘制） ====================

        private List<TextureAssetInfo> assets = new List<TextureAssetInfo>();
        private readonly List<TextureAssetInfo> filteredAssets = new List<TextureAssetInfo>();
        private List<JobRecord> jobs = new List<JobRecord>();
        private readonly List<string> liveLog = new List<string>();
        private bool atlasEstimateDirty = true;
        private int estimatedAtlasPages;
        private long estimatedMaxPagePixels;
        private int estimatedMaxPageEdge;
        private string atlasEstimateError = string.Empty;
        private bool memBudgetKnown;
        private bool fetchingMemory;
        private long vramFreeBytes;
        private long vramTotalBytes;
        private string memDeviceName = string.Empty;
        private Vector2 logScroll;
        private bool running;
        private bool scanning;
        private bool testingConnection;
        private float progress;
        private string status = "就绪";
        private CancellationTokenSource runCancellation;
        private CancellationTokenSource connectionCancellation;
        private CancellationTokenSource scanCancellation;

        // 运行或扫描期间都视为忙碌，统一禁用配置编辑与开始操作
        private bool Busy => running || scanning;
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
            SavePreferences();
            base.OnDisable();
        }

        // ==================== 扫描 / 连接 / 执行 ====================

        private async Task ScanAsync()
        {
            // 手动扫描已由禁用按钮阻止；此处仅防止重复扫描（任务结束后的自动重扫允许在 running 期间进行）
            if (scanning)
                return;

            List<string> configuredFolders = sourceFolders
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
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

            // 开始前尽力刷新显存并计算内存风险提示（仅通知，不阻止）
            (long maxPagePixels, int maxPageEdge) = GetLargestPage(pages);
            await FetchMemoryBudgetAsync();
            long peakBytes = UpscaleMemoryEstimator.EstimatePeakBytes(maxPagePixels, peakScale, memorySafetyFactor);
            int outputEdge = UpscaleMemoryEstimator.EstimateMaxOutputEdge(maxPageEdge, expectedScale);
            List<string> memoryWarnings = BuildMemoryWarnings(peakBytes, outputEdge);

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
                running = false;
                runCancellation.Dispose();
                runCancellation = null;
                RefreshJobs();
                if (assets.Count > 0)
                    RefreshAssetUpgradeStates();
                SafeRepaint();
            }
        }

        private void Restore(JobRecord job)
        {
            try
            {
                List<string> conflicts = UpscaleJobStore.GetRestoreConflicts(job.directory);
                if (conflicts.Count > 0)
                {
                    string details = string.Join("\n", conflicts.Take(8));
                    if (conflicts.Count > 8)
                        details += $"\n……另有 {conflicts.Count - 8} 项";
                    EditorUtility.DisplayDialog(
                        "恢复已阻止",
                        "任务完成后有文件或导入设置发生变化：\n\n" + details,
                        "确定");
                    return;
                }

                string message = $"已确认当前文件仍与任务输出一致。将使用任务 {job.manifest.jobId} 的备份覆盖 " +
                                 $"{job.manifest.assets.Count} 个原文件及其 .meta。";
                if (!EditorUtility.DisplayDialog("恢复任务", message, "恢复", "取消"))
                    return;

                UpscaleJobStore.Restore(job.directory);
                status = "已恢复: " + job.manifest.jobId;
                RefreshJobs();
                _ = ScanAsync();
            }
            catch (Exception exception)
            {
                status = "恢复失败";
                EditorUtility.DisplayDialog("恢复失败", exception.Message, "确定");
            }
        }

        private UpscalerRunSettings BuildSettings()
        {
            List<string> configuredFolders = sourceFolders
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
                jpegQuality = jpegQuality
            };
        }

        // ==================== 数据刷新与辅助 ====================

        private void RefreshJobs()
        {
            jobs = UpscaleJobStore.List();
            historyRows = jobs.Take(10).Select(job => new HistoryRow(this, job)).ToList();
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
                if (UpgradeAssetStateUtility.MatchesFilter(asset, assetUpgradeFilter))
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
                    (estimatedMaxPagePixels, estimatedMaxPageEdge) = GetLargestPage(pages);
                }
                else
                {
                    estimatedAtlasPages = 0;
                    estimatedMaxPagePixels = 0;
                    estimatedMaxPageEdge = 0;
                }
                atlasEstimateError = string.Empty;
            }
            catch (Exception exception)
            {
                estimatedAtlasPages = 0;
                estimatedMaxPagePixels = 0;
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

        // 组装内存风险提示（仅通知，不阻止执行）
        private List<string> BuildMemoryWarnings(long peakBytes, int outputEdge)
        {
            var warnings = new List<string>();
            if (outputEdge > UpscaleMemoryEstimator.UnityMaxTextureEdge)
                warnings.Add($"输出图集单边约 {outputEdge} 超过 Unity 上限 {UpscaleMemoryEstimator.UnityMaxTextureEdge}，必然失败，请降低最大边长。");
            if (memBudgetKnown && peakBytes > 0 && vramFreeBytes > 0 && peakBytes > vramFreeBytes)
                warnings.Add($"预计单页峰值≈{UpscaleJobStore.FormatBytes(peakBytes)}，超过 ComfyUI 可用显存 " +
                             $"{UpscaleJobStore.FormatBytes(vramFreeBytes)}，可能 OOM。建议降低最大像素数/边长，或核对峰值倍率。");
            return warnings;
        }

        private void OnFoldersChanged()
        {
            // 归一化并仅保留 Assets 下的有效文件夹，非法/重复条目自动剔除
            var cleaned = new List<string>();
            bool droppedInvalid = false;
            foreach (string raw in sourceFolders)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string path = raw.Trim().Replace((char)92, '/');
                if (!AssetDatabase.IsValidFolder(path))
                {
                    droppedInvalid = true;
                    continue;
                }
                if (!cleaned.Contains(path, StringComparer.OrdinalIgnoreCase))
                    cleaned.Add(path);
            }
            if (droppedInvalid)
                ShowNotification(new GUIContent("请选择 Assets 下的目录"));

            sourceFolders.Clear();
            sourceFolders.AddRange(cleaned);

            assets.Clear();
            RebuildAssetRows();
            atlasEstimateDirty = true;
            SavePreferences();
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

        private void LoadPreferences()
        {
            sourceFolders.Clear();
            string serializedFolders = EditorPrefs.GetString(PrefPrefix + "SourceFolders", string.Empty);
            if (!string.IsNullOrEmpty(serializedFolders))
            {
                sourceFolders.AddRange(serializedFolders
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => path.Trim().Replace((char)92, '/'))
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            }
            if (sourceFolders.Count == 0)
            {
                string legacyFolder = EditorPrefs.GetString(PrefPrefix + "SourceFolder", "Assets");
                if (!string.IsNullOrWhiteSpace(legacyFolder))
                    sourceFolders.Add(legacyFolder);
            }
            if (sourceFolders.Count == 0)
                sourceFolders.Add("Assets");

            comfyUrl = EditorPrefs.GetString(PrefPrefix + "ComfyUrl", "http://127.0.0.1:8188");
            workflowPath = EditorPrefs.GetString(PrefPrefix + "WorkflowPath", string.Empty);
            inputNodeId = EditorPrefs.GetString(PrefPrefix + "InputNodeId", string.Empty);
            inputFieldName = EditorPrefs.GetString(PrefPrefix + "InputFieldName", "image");
            outputNodeId = EditorPrefs.GetString(PrefPrefix + "OutputNodeId", string.Empty);
            expectedScale = EditorPrefs.HasKey(PrefPrefix + "ExpectedScaleFloat")
                ? EditorPrefs.GetFloat(PrefPrefix + "ExpectedScaleFloat", 4f)
                : EditorPrefs.GetInt(PrefPrefix + "ExpectedScale", 4);
            // 峰值倍率默认取预期倍率；仅用于内存预估
            peakScale = Mathf.Max(1f, EditorPrefs.HasKey(PrefPrefix + "PeakScaleFloat")
                ? EditorPrefs.GetFloat(PrefPrefix + "PeakScaleFloat", expectedScale)
                : expectedScale);
            memorySafetyFactor = Mathf.Clamp(EditorPrefs.GetInt(PrefPrefix + "MemorySafetyFactor", 4), 1, 16);
            padding = EditorPrefs.GetInt(PrefPrefix + "Padding", 32);
            maxAtlasEdge = EditorPrefs.GetInt(PrefPrefix + "MaxAtlasEdge", 4096);
            maxAtlasPixels = long.TryParse(EditorPrefs.GetString(PrefPrefix + "MaxAtlasPixels", "16777216"), out long pixels)
                ? pixels
                : 16777216;
            requestTimeoutSeconds = EditorPrefs.GetInt(PrefPrefix + "RequestTimeout", 120);
            jobTimeoutMinutes = EditorPrefs.GetInt(PrefPrefix + "JobTimeout", 30);
            jpegQuality = EditorPrefs.GetInt(PrefPrefix + "JpegQuality", 95);
            assetUpgradeFilter = (UpgradeAssetFilter)Mathf.Clamp(
                EditorPrefs.GetInt(PrefPrefix + "UpgradeAssetFilter", 0),
                0,
                Enum.GetValues(typeof(UpgradeAssetFilter)).Length - 1);
        }

        private void SavePreferences()
        {
            List<string> configuredFolders = sourceFolders
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim().Replace((char)92, '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            EditorPrefs.SetString(PrefPrefix + "SourceFolders", string.Join("\n", configuredFolders));
            EditorPrefs.SetString(PrefPrefix + "SourceFolder", configuredFolders.FirstOrDefault() ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "ComfyUrl", comfyUrl ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "WorkflowPath", workflowPath ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "InputNodeId", inputNodeId ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "InputFieldName", inputFieldName ?? string.Empty);
            EditorPrefs.SetString(PrefPrefix + "OutputNodeId", outputNodeId ?? string.Empty);
            EditorPrefs.SetFloat(PrefPrefix + "ExpectedScaleFloat", expectedScale);
            EditorPrefs.SetFloat(PrefPrefix + "PeakScaleFloat", peakScale);
            EditorPrefs.SetInt(PrefPrefix + "MemorySafetyFactor", memorySafetyFactor);
            EditorPrefs.SetInt(PrefPrefix + "Padding", padding);
            EditorPrefs.SetInt(PrefPrefix + "MaxAtlasEdge", maxAtlasEdge);
            EditorPrefs.SetString(PrefPrefix + "MaxAtlasPixels", maxAtlasPixels.ToString());
            EditorPrefs.SetInt(PrefPrefix + "RequestTimeout", requestTimeoutSeconds);
            EditorPrefs.SetInt(PrefPrefix + "JobTimeout", jobTimeoutMinutes);
            EditorPrefs.SetInt(PrefPrefix + "JpegQuality", jpegQuality);
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

        // 历史表行：按任务状态条件启用“继续/恢复”，并提供打开目录
        private sealed class HistoryRow
        {
            private readonly ComfyUIUpscalerWindow owner;
            private readonly JobRecord job;

            public HistoryRow(ComfyUIUpscalerWindow owner, JobRecord job)
            {
                this.owner = owner;
                this.job = job;
            }

            [TableColumnWidth(190)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string JobId => job.manifest.jobId;

            [TableColumnWidth(100)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string Status => job.manifest.status;

            [TableColumnWidth(120)]
            [DisplayAsString]
            [HideLabel]
            [ShowInInspector]
            public string Scale => $"{job.manifest.assets.Count} 文件 / {job.manifest.pages.Count} 页";

            [TableColumnWidth(260)]
            [DisplayAsString]
            [HideLabel]
            [PropertyTooltip("处理前 -> 处理后（变化量、变化率）")]
            [ShowInInspector]
            public string SizeSummary => UpscaleJobStore.FormatSizeSummary(job.manifest);

            [TableColumnWidth(72, false)]
            [Button("继续")]
            [PropertyTooltip("从中断处继续，已完成的图集页不会重跑")]
            [EnableIf(nameof(CanContinue))]
            private void Continue() => owner.Resume(job);

            [TableColumnWidth(72, false)]
            [Button("恢复")]
            [PropertyTooltip("仅已完成且当前文件未变化的任务可恢复")]
            [EnableIf(nameof(CanRestore))]
            private void RestoreJob() => owner.Restore(job);

            [TableColumnWidth(80, false)]
            [Button("打开目录")]
            private void OpenDirectory() => EditorUtility.RevealInFinder(job.directory);

            private bool CanContinue => owner != null && !owner.Busy &&
                                        UpscaleJobStore.CanAttemptResume(job.manifest);

            private bool CanRestore => owner != null && !owner.Busy &&
                                       job.manifest.status == JobStatus.Completed;
        }
    }
}
