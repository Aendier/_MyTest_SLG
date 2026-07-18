using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UIR.EditorTools
{
    /// <summary>
    /// 以图搜图工具：给定一张查询图，在指定目录下检索感知上相似的图片。
    /// 采用感知哈希（pHash / DCT，64 位）计算相似度，支持异步（时间切片）检索、
    /// 阈值设置、预估工作量确认、本地选图与剪贴板粘贴。
    /// </summary>
    public class ImageSimilaritySearchWindow : OdinEditorWindow
    {
        // 参与哈希计算的图片可解码扩展名（磁盘直接解码仅支持 PNG/JPG）。
        private static readonly string[] SupportedExtensions =
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".tif", ".tiff", ".exr"
        };

        // 时间切片：每帧最多占用的处理时长（秒），保证编辑器不卡顿。
        private const double TickTimeBudgetSeconds = 0.03;

        // pHash 参数：低频块尺寸按“高精度”开关取 8(64 位) 或 16(256 位)；
        // 采样尺寸 = 低频块 × 4，保证两种模式取用相同的低频带，避免高精度引入高频噪声。
        private const int LowFreqNormal = 8;
        private const int LowFreqHigh = 16;
        private const int SampleScale = 4;

        // 结果缩略图尺寸。
        private const int ThumbnailSize = 96;

        /// <summary>单条检索结果（结果区由原生 IMGUI 逐行绘制，见 OnEndDrawEditors）。</summary>
        private class MatchItem
        {
            public string FullPath;     // 完整磁盘路径
            public float Similarity;    // 相似度（0~1）
            public Texture2D Thumbnail; // 结果缩略图
        }

        // ===== 查询图 =====
        // 预览框支持直接拖入纹理资源；set 时统一走 SetQuery 计算哈希并管理临时纹理生命周期。
        [Title("查询图", "拖拽资源到预览框 / 本地选图 / 粘贴剪贴板", TitleAlignments.Left)]
        [PreviewField(96f, ObjectFieldAlignment.Left), HideLabel]
        [ShowInInspector, PropertyOrder(0)]
        private Texture2D QueryTexture
        {
            get => m_queryTexture;
            set => SetQuery(value, IsAssetTexture(value), GetAssetFullPath(value));
        }

        private Texture2D m_queryTexture;      // 当前查询图（可能是资源，也可能是本工具创建的临时纹理）
        private bool m_queryTextureOwned;      // 查询图是否由本工具创建（需要主动销毁）
        private string m_querySourcePath;      // 查询图来源磁盘路径（用于检索时排除自身），可能为空
        private ulong[] m_queryHash;           // 查询图的 pHash（64 或 256 位打包）
        private bool m_hasQueryHash;
        private string m_queryInfo = "尚未选择查询图。";

        [ShowInInspector, HideLabel, DisplayAsString(false), PropertyOrder(4)]
        private string QueryInfo => m_queryInfo;

        // ===== 检索设置 =====
        [Title("检索设置")]
        [FolderPath(AbsolutePath = true, RequireExistingPath = true)]
        [LabelText("检索目录"), ShowInInspector, PropertyOrder(10)]
        private string m_searchFolder = "";

        [LabelText("包含子目录"), ShowInInspector, PropertyOrder(11)]
        private bool m_recursive = true;

        [LabelText("排除查询图自身"), ShowInInspector, PropertyOrder(11.5f)]
        [Tooltip("勾选后不显示与查询图同一路径的源文件；想验证管线或查找完全相同的重复图时请取消勾选")]
        private bool m_excludeSelf = false;

        [PropertyRange(0f, 1f)]
        [LabelText("相似度阈值"), ShowInInspector, PropertyOrder(12)]
        private float m_threshold = 0.80f;

        // ===== 精度增强 =====
        [Title("精度增强")]
        [LabelText("保持宽高比 (letterbox)"), ShowInInspector, PropertyOrder(13)]
        [Tooltip("按原始比例缩放并留边填充，避免长条图被拉伸失真")]
        private bool m_keepAspect = true;

        [LabelText("合成透明背景"), ShowInInspector, PropertyOrder(14)]
        [Tooltip("按 alpha 将图片合成到背景色，消除透明区域的无效像素干扰")]
        private bool m_compositeAlpha = true;

        [LabelText("背景色 / 填充色"), ShowInInspector, PropertyOrder(15)]
        [EnableIf("@this.m_keepAspect || this.m_compositeAlpha")]
        private Color m_backgroundColor = Color.white;

        [LabelText("高精度哈希 (256 位)"), ShowInInspector, PropertyOrder(16)]
        [Tooltip("低频块 8×8→16×16、采样 32→64，区分度更高但更严格；命中偏少时可关闭或调低阈值")]
        private bool m_highPrecision = false;

        // ===== 检索运行时状态 =====
        private bool m_isSearching;
        private List<string> m_candidates = new List<string>();
        private int m_processIndex;
        private double m_searchStartTime;
        private readonly List<MatchItem> m_matches = new List<MatchItem>();
        private int m_scannedCount;
        private int m_failedCount;
        private string m_statusText = "";
        private Vector2 m_resultScroll;  // 结果独立滚动列表的滚动位置

        // DCT 预计算表（懒加载）。
        private static float[,] s_dctCos;   // [k, n]
        private static float[] s_dctAlpha;  // [k]

        // 关闭窗口整体滚动：配置区固定在顶部，结果用原生 IMGUI 的独立滚动列表（见 OnEndDrawEditors），
        // 避免与窗口滚动条重叠，且原生滚动条/按钮点击稳定可靠。
        public override bool UseScrollView => false;

        /// <summary>打开窗口。菜单入口在 UIRMenuRegister 中统一注册。</summary>
        public static void Open()
        {
            var window = GetWindow<ImageSimilaritySearchWindow>("图搜图");
            // 配置区较高 + 结果表格自带滚动，给足最小高度，避免表格滚动条被挤出窗口底部。
            window.minSize = new Vector2(460f, 720f);
            if (window.position.height < 720f)
            {
                Rect pos = window.position;
                pos.height = 820f;
                window.position = pos;
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            // 域重载（重新编译/进出播放模式）后中断残留的检索状态，避免卡在“搜索中”。
            m_isSearching = false;
        }

        protected override void OnDestroy()
        {
            // 窗口关闭时停止检索并释放所有纹理资源。
            StopSearch();
            ReleaseQueryTexture();
            ClearMatches();
            base.OnDestroy();
        }

        [ButtonGroup("QueryButtons"), PropertyOrder(1)]
        [Button("本地选图")]
        private void PickImageFromFile()
        {
            string path = EditorUtility.OpenFilePanelWithFilters(
                "选择查询图",
                Application.dataPath,
                new[] { "Image", "png,jpg,jpeg", "All files", "*" });

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            LoadQueryFromFile(path);
        }

        private void LoadQueryFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                EditorUtility.DisplayDialog("图搜图", "文件不存在：" + path, "确定");
                return;
            }

            Texture2D tex = LoadTextureFromFileBytes(path);
            if (tex == null)
            {
                EditorUtility.DisplayDialog("图搜图", "无法解码该图片（本地文件仅支持 PNG / JPG）。", "确定");
                return;
            }

            SetQuery(tex, true, Path.GetFullPath(path));
        }

        [ButtonGroup("QueryButtons"), PropertyOrder(1)]
        [Button("粘贴剪贴板")]
        private void PasteImageFromClipboard()
        {
            Texture2D tex = ClipboardImage.TryGetImage(out string sourcePath, out string error);
            if (tex == null)
            {
                EditorUtility.DisplayDialog("图搜图", "读取剪贴板图片失败：\n" + error, "确定");
                return;
            }

            SetQuery(tex, true, sourcePath);
        }

        /// <summary>设置当前查询图并立即计算其感知哈希。</summary>
        private void SetQuery(Texture2D tex, bool owned, string sourcePath)
        {
            ReleaseQueryTexture();

            m_queryTexture = tex;
            m_queryTextureOwned = owned;
            m_querySourcePath = sourcePath;

            if (tex == null)
            {
                m_hasQueryHash = false;
                m_queryInfo = "尚未选择查询图。";
                return;
            }

            m_queryHash = ComputePerceptualHash(tex);
            m_hasQueryHash = true;
            m_queryInfo = $"尺寸 {tex.width}x{tex.height}\n来源：{(string.IsNullOrEmpty(sourcePath) ? "剪贴板/内存" : sourcePath)}";
            Repaint();
        }

        private void ReleaseQueryTexture()
        {
            if (m_queryTexture != null && m_queryTextureOwned)
            {
                DestroyImmediate(m_queryTexture);
            }

            m_queryTexture = null;
            m_queryTextureOwned = false;
            m_querySourcePath = null;
        }

        [PropertyOrder(3)]
        [Button("清除查询图"), EnableIf(nameof(m_hasQueryHash))]
        private void ClearQuery()
        {
            SetQuery(null, false, null);
        }

        // ============================================================
        // 执行区（开始 / 进度 / 取消），由 Odin 特性驱动展示
        // ============================================================

        /// <summary>“开始搜索”按钮：未在检索时可见，需要已选择查询图才可用。</summary>
        [Title("执行")]
        [PropertyOrder(20), HideIf(nameof(m_isSearching))]
        [InfoBox("请先选择 / 粘贴一张查询图。", InfoMessageType.Warning, VisibleIf = "@!this.m_hasQueryHash")]
        [Button("开始搜索", ButtonHeight = 34), EnableIf(nameof(m_hasQueryHash))]
        private void StartSearchButton()
        {
            PrepareAndConfirmSearch();
        }

        /// <summary>检索进度条（仅检索中显示），叠加显示已处理/命中/预计剩余。</summary>
        [PropertyOrder(21), ShowIf(nameof(m_isSearching)), ShowInInspector, HideLabel]
        [ProgressBar(0, 1, r: 0.30f, g: 0.65f, b: 1f, Height = 22, CustomValueStringGetter = "$ProgressLabel")]
        private float SearchProgress => m_candidates.Count > 0 ? (float)m_processIndex / m_candidates.Count : 0f;

        private string ProgressLabel
        {
            get
            {
                int total = m_candidates.Count;
                double elapsed = EditorApplication.timeSinceStartup - m_searchStartTime;
                string eta = "…";
                if (m_processIndex > 0 && m_processIndex < total)
                {
                    double perItem = elapsed / m_processIndex;
                    eta = FormatDuration((total - m_processIndex) * perItem * 1000.0);
                }

                return $"已处理 {m_processIndex}/{total} · 命中 {m_matches.Count} · 剩余 {eta}";
            }
        }

        [PropertyOrder(22), ShowIf(nameof(m_isSearching))]
        [Button("取消", ButtonHeight = 24)]
        private void CancelButton()
        {
            int total = m_candidates.Count;
            StopSearch();
            m_statusText = $"已取消。已处理 {m_processIndex}/{total}，命中 {m_matches.Count}。";
        }

        [PropertyOrder(23), ShowInInspector, HideLabel, DisplayAsString(false)]
        [ShowIf("@!this.m_isSearching && !string.IsNullOrEmpty(this.m_statusText)")]
        private string StatusText => m_statusText;

        /// <summary>
        /// 结果区渲染：在 Odin 配置区之后用原生 IMGUI 绘制，独立成一个可滚动列表。
        /// 之所以不用 Odin TableList：其表格内的滚动条与「定位/打开」按钮在本工程环境下收不到点击。
        /// 原生 GUILayout 控件是 Unity 最底层的交互，点击与滚动稳定可靠。
        /// 窗口整体滚动已关闭（UseScrollView=false），配置区固定在顶部，此滚动视图占满剩余高度。
        /// 检索过程中每命中一张即时追加显示（由 SearchUpdate 每帧 Repaint 驱动），无需等全部结束。
        /// </summary>
        protected override void OnEndDrawEditors()
        {
            base.OnEndDrawEditors();

            GUILayout.Space(6f);
            EditorGUILayout.LabelField(m_isSearching ? $"结果（{m_matches.Count}，搜索中…）" : $"结果（{m_matches.Count}）", EditorStyles.boldLabel);

            if (m_matches.Count == 0)
            {
                if (!m_isSearching)
                {
                    EditorGUILayout.HelpBox("暂无结果。选择查询图并点击「开始搜索」。", UnityEditor.MessageType.Info);
                }

                return;
            }

            // 独立滚动列表：ExpandHeight 占满配置区之下的剩余高度，内容超出即在此区域内滚动。
            m_resultScroll = GUILayout.BeginScrollView(m_resultScroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < m_matches.Count; i++)
            {
                DrawMatchRow(m_matches[i]);
            }

            GUILayout.EndScrollView();
        }

        /// <summary>绘制单条结果行：缩略图 + 文件名 + 相似度条 + 定位/打开按钮。</summary>
        private static void DrawMatchRow(MatchItem item)
        {
            using (new GUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Rect thumbRect = GUILayoutUtility.GetRect(58f, 58f, GUILayout.Width(58f), GUILayout.Height(58f));
                if (item.Thumbnail != null)
                {
                    GUI.DrawTexture(thumbRect, item.Thumbnail, ScaleMode.ScaleToFit);
                }

                using (new GUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(Path.GetFileName(item.FullPath), EditorStyles.boldLabel);

                    Rect barRect = GUILayoutUtility.GetRect(80f, 16f, GUILayout.ExpandWidth(true));
                    EditorGUI.ProgressBar(barRect, item.Similarity, $"相似度 {item.Similarity:P1}");

                    EditorGUILayout.LabelField(item.FullPath, EditorStyles.miniLabel);
                }

                using (new GUILayout.VerticalScope(GUILayout.Width(72f)))
                {
                    if (GUILayout.Button("定位", GUILayout.Height(24f)))
                    {
                        LocateResult(item.FullPath);
                    }

                    if (GUILayout.Button("打开", GUILayout.Height(24f)))
                    {
                        OpenResult(item.FullPath);
                    }
                }
            }
        }

        /// <summary>枚举候选、抽样预估耗时，并弹出确认对话框；确认后启动异步检索。</summary>
        private void PrepareAndConfirmSearch()
        {
            if (!m_hasQueryHash || m_queryTexture == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(m_searchFolder) || !Directory.Exists(m_searchFolder))
            {
                EditorUtility.DisplayDialog("图搜图", "检索目录无效，请重新选择。", "确定");
                return;
            }

            // 用当前精度设置重新计算查询图哈希，确保与候选图使用一致的参数。
            m_queryHash = ComputePerceptualHash(m_queryTexture);

            // 枚举候选文件。
            List<string> candidates = EnumerateCandidates(m_searchFolder, m_recursive);
            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog("图搜图", "该目录下没有可检索的图片。", "确定");
                return;
            }

            // 抽样实测平均单张耗时，用于预估总时长。
            double avgMs = BenchmarkAverageMs(candidates);
            double estimateMs = avgMs * candidates.Count;

            string msg =
                $"待检索图片：{candidates.Count} 张\n" +
                $"预估耗时：约 {FormatDuration(estimateMs)}（单张约 {avgMs:F1} ms）\n" +
                $"相似度阈值：{m_threshold:P0}\n" +
                $"包含子目录：{(m_recursive ? "是" : "否")}\n\n" +
                "是否开始搜索？";

            if (!EditorUtility.DisplayDialog("图搜图 - 确认", msg, "开始", "取消"))
            {
                return;
            }

            StartSearch(candidates);
        }

        /// <summary>定位：工程内资源在 Project 面板高亮；工程外文件在资源管理器中选中。</summary>
        private static void LocateResult(string fullPath)
        {
            string assetPath = ToAssetPath(fullPath);
            if (!string.IsNullOrEmpty(assetPath))
            {
                UnityEngine.Object obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (obj != null)
                {
                    Selection.activeObject = obj;
                    EditorGUIUtility.PingObject(obj);
                    return;
                }
            }

            EditorUtility.RevealInFinder(fullPath);
        }

        /// <summary>打开：工程内资源用默认程序打开；工程外文件用系统默认看图程序打开。</summary>
        private static void OpenResult(string fullPath)
        {
            string assetPath = ToAssetPath(fullPath);
            if (!string.IsNullOrEmpty(assetPath))
            {
                UnityEngine.Object obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (obj != null)
                {
                    AssetDatabase.OpenAsset(obj);
                    return;
                }
            }

            // 工程外文件：交给系统默认程序打开图片本身。
            Application.OpenURL("file:///" + fullPath.Replace('\\', '/'));
        }

        // ============================================================
        // 异步检索状态机（由 EditorApplication.update 驱动，时间切片）
        // ============================================================
        private void StartSearch(List<string> candidates)
        {
            StopSearch();
            ClearMatches();

            m_candidates = candidates;
            m_processIndex = 0;
            m_scannedCount = 0;
            m_failedCount = 0;
            m_isSearching = true;
            m_searchStartTime = EditorApplication.timeSinceStartup;
            m_statusText = "";

            EditorApplication.update += SearchUpdate;
        }

        private void StopSearch()
        {
            if (m_isSearching)
            {
                EditorApplication.update -= SearchUpdate;
            }

            m_isSearching = false;
        }

        private void SearchUpdate()
        {
            double tickStart = EditorApplication.timeSinceStartup;

            while (m_processIndex < m_candidates.Count)
            {
                ProcessCandidate(m_candidates[m_processIndex]);
                m_processIndex++;

                if (EditorApplication.timeSinceStartup - tickStart > TickTimeBudgetSeconds)
                {
                    break;
                }
            }

            if (m_processIndex >= m_candidates.Count)
            {
                FinishSearch();
            }

            Repaint();
        }

        private void ProcessCandidate(string fullPath)
        {
            // 仅在开启“排除查询图自身”时，跳过与查询图同一路径的文件。
            if (m_excludeSelf && !string.IsNullOrEmpty(m_querySourcePath) &&
                string.Equals(Path.GetFullPath(fullPath), m_querySourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Texture source = LoadSourceTexture(fullPath, out bool owned);
            if (source == null)
            {
                m_failedCount++;
                return;
            }

            try
            {
                ulong[] hash = ComputePerceptualHash(source);
                float similarity = Similarity(m_queryHash, hash);
                m_scannedCount++;

                if (similarity >= m_threshold)
                {
                    m_matches.Add(new MatchItem
                    {
                        FullPath = fullPath,
                        Similarity = similarity,
                        Thumbnail = DownscaleToTexture(source, ThumbnailSize)
                    });
                }
            }
            finally
            {
                if (owned)
                {
                    DestroyImmediate(source);
                }
            }
        }

        private void FinishSearch()
        {
            StopSearch();
            m_matches.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));

            double elapsed = EditorApplication.timeSinceStartup - m_searchStartTime;
            m_statusText = $"完成：扫描 {m_scannedCount} 张，命中 {m_matches.Count} 张，失败 {m_failedCount} 张，用时 {FormatDuration(elapsed * 1000.0)}。";
            Repaint();
        }

        private void ClearMatches()
        {
            foreach (MatchItem item in m_matches)
            {
                if (item.Thumbnail != null)
                {
                    DestroyImmediate(item.Thumbnail);
                }
            }

            m_matches.Clear();
        }

        // ============================================================
        // 候选枚举 / 加载 / 预估
        // ============================================================
        private static List<string> EnumerateCandidates(string folder, bool recursive)
        {
            var result = new List<string>();
            SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.*", option);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[图搜图] 枚举文件失败：" + ex.Message);
                return result;
            }

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(SupportedExtensions, ext) < 0)
                {
                    continue;
                }

                // Assets 之外的目录仅能解码 PNG/JPG，其它格式跳过避免无效解码。
                if (!IsUnderAssets(file) && !IsFileDecodable(ext))
                {
                    continue;
                }

                result.Add(file);
            }

            return result;
        }

        /// <summary>抽样实测平均单张处理耗时（毫秒）。</summary>
        private double BenchmarkAverageMs(List<string> candidates)
        {
            int sampleCount = Mathf.Min(candidates.Count, 8);
            if (sampleCount <= 0)
            {
                return 1.0;
            }

            var sw = Stopwatch.StartNew();
            int processed = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                Texture source = LoadSourceTexture(candidates[i], out bool owned);
                if (source == null)
                {
                    continue;
                }

                ComputePerceptualHash(source);
                if (owned)
                {
                    DestroyImmediate(source);
                }

                processed++;
            }

            sw.Stop();
            return processed > 0 ? sw.Elapsed.TotalMilliseconds / processed : 1.0;
        }

        /// <summary>
        /// 加载用于哈希/缩略图的源纹理。Assets 下的资源直接用导入结果（支持更多格式、无需可读），
        /// 其余走磁盘字节解码（PNG/JPG）。owned 表示调用方需要负责销毁。
        /// </summary>
        private static Texture LoadSourceTexture(string fullPath, out bool owned)
        {
            owned = false;

            string assetPath = ToAssetPath(fullPath);
            if (!string.IsNullOrEmpty(assetPath))
            {
                Texture asset = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
                if (asset != null)
                {
                    return asset;
                }
            }

            Texture2D tex = LoadTextureFromFileBytes(fullPath);
            owned = tex != null;
            return tex;
        }

        private static Texture2D LoadTextureFromFileBytes(string fullPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(bytes))
                {
                    return tex;
                }

                DestroyImmediate(tex);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[图搜图] 读取失败 {fullPath}: {ex.Message}");
                return null;
            }
        }

        // ============================================================
        // 感知哈希（pHash / DCT）
        // ============================================================
        /// <summary>按当前精度设置计算纹理的感知哈希（64 或 256 位）。</summary>
        private ulong[] ComputePerceptualHash(Texture source)
        {
            int low = m_highPrecision ? LowFreqHigh : LowFreqNormal;
            int sample = low * SampleScale; // 采样尺寸随低频块等比放大，保持相同低频带
            float[] gray = DownscaleToGray(source, sample);
            return PerceptualHashFromGray(gray, sample, low);
        }

        /// <summary>
        /// 把任意纹理缩放为 size×size 灰度数组（GPU 缩放，源纹理无需可读）。
        /// 依据设置可选：保持宽高比（letterbox 填充背景色）、按 alpha 合成到背景色。
        /// </summary>
        private float[] DownscaleToGray(Texture source, int size)
        {
            Color32 bg = m_backgroundColor;
            float bgGray = ToGray(bg.r, bg.g, bg.b);

            if (!m_keepAspect)
            {
                // 直接拉伸到方形（旧行为）。
                Color32[] pixels = ReadDownscaledPixels(source, size, size);
                return PixelsToGray(pixels, m_compositeAlpha, bg);
            }

            // 保持宽高比：按比例缩放到 size 内，再居中留边填充背景灰度。
            float aspect = (float)source.width / Mathf.Max(1, source.height);
            int rw, rh;
            if (aspect >= 1f)
            {
                rw = size;
                rh = Mathf.Clamp(Mathf.RoundToInt(size / aspect), 1, size);
            }
            else
            {
                rh = size;
                rw = Mathf.Clamp(Mathf.RoundToInt(size * aspect), 1, size);
            }

            Color32[] scaled = ReadDownscaledPixels(source, rw, rh);
            float[] small = PixelsToGray(scaled, m_compositeAlpha, bg);

            var full = new float[size * size];
            for (int i = 0; i < full.Length; i++)
            {
                full[i] = bgGray;
            }

            int offsetX = (size - rw) / 2;
            int offsetY = (size - rh) / 2;
            for (int y = 0; y < rh; y++)
            {
                for (int x = 0; x < rw; x++)
                {
                    full[(offsetY + y) * size + (offsetX + x)] = small[y * rw + x];
                }
            }

            return full;
        }

        /// <summary>像素数组转灰度；compositeAlpha 为真时按 alpha 合成到背景色。</summary>
        private static float[] PixelsToGray(Color32[] pixels, bool compositeAlpha, Color32 bg)
        {
            var gray = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                float r = c.r;
                float g = c.g;
                float b = c.b;

                if (compositeAlpha && c.a < 255)
                {
                    float a = c.a / 255f;
                    float inv = 1f - a;
                    r = c.r * a + bg.r * inv;
                    g = c.g * a + bg.g * inv;
                    b = c.b * a + bg.b * inv;
                }

                gray[i] = ToGray(r, g, b);
            }

            return gray;
        }

        private static float ToGray(float r, float g, float b)
        {
            return 0.299f * r + 0.587f * g + 0.114f * b;
        }

        /// <summary>生成一张 size×size 的缩略图纹理（调用方负责销毁）。</summary>
        private static Texture2D DownscaleToTexture(Texture source, int size)
        {
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;

                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
                tex.Apply(false);
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Color32[] ReadDownscaledPixels(Texture source, int width, int height)
        {
            // 用 sRGB（gamma）空间读取：pHash 在感知均匀空间上的中间调对比更强，相似/不相似区分更明显。
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Texture2D tmp = null;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;

                tmp = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                tmp.Apply(false);
                return tmp.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (tmp != null)
                {
                    DestroyImmediate(tmp);
                }
            }
        }

        /// <summary>由灰度数组计算 pHash：对 size×size 做 DCT，取左上 low×low 低频，按均值二值化，打包为 ulong[]。</summary>
        private static ulong[] PerceptualHashFromGray(float[] gray, int size, int low)
        {
            EnsureDctTables(size, low);

            // 第一步：对每行做水平 DCT，只保留前 low 个低频系数。
            var rowDct = new float[size, low];
            for (int y = 0; y < size; y++)
            {
                for (int v = 0; v < low; v++)
                {
                    float sum = 0f;
                    for (int x = 0; x < size; x++)
                    {
                        sum += gray[y * size + x] * s_dctCos[v, x];
                    }

                    rowDct[y, v] = s_dctAlpha[v] * sum;
                }
            }

            // 第二步：对列做垂直 DCT，得到 low×low 低频块。
            var block = new float[low * low];
            double total = 0.0;
            for (int u = 0; u < low; u++)
            {
                for (int v = 0; v < low; v++)
                {
                    float sum = 0f;
                    for (int y = 0; y < size; y++)
                    {
                        sum += rowDct[y, v] * s_dctCos[u, y];
                    }

                    float value = s_dctAlpha[u] * sum;
                    block[u * low + v] = value;

                    // 均值排除直流分量 [0,0]。
                    if (!(u == 0 && v == 0))
                    {
                        total += value;
                    }
                }
            }

            float average = (float)(total / (low * low - 1));

            int bits = low * low;
            var hash = new ulong[(bits + 63) / 64];
            for (int i = 0; i < bits; i++)
            {
                if (block[i] > average)
                {
                    hash[i >> 6] |= 1UL << (i & 63);
                }
            }

            return hash;
        }

        private static void EnsureDctTables(int size, int low)
        {
            if (s_dctCos != null && s_dctCos.GetLength(0) == low && s_dctCos.GetLength(1) == size)
            {
                return;
            }

            s_dctCos = new float[low, size];
            for (int k = 0; k < low; k++)
            {
                for (int n = 0; n < size; n++)
                {
                    s_dctCos[k, n] = Mathf.Cos(((2 * n + 1) * k * Mathf.PI) / (2f * size));
                }
            }

            s_dctAlpha = new float[low];
            s_dctAlpha[0] = Mathf.Sqrt(1f / size);
            for (int k = 1; k < low; k++)
            {
                s_dctAlpha[k] = Mathf.Sqrt(2f / size);
            }
        }

        /// <summary>由两个哈希的汉明距离得相似度（0~1）。</summary>
        private static float Similarity(ulong[] a, ulong[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
            {
                return 0f;
            }

            int distance = 0;
            for (int i = 0; i < a.Length; i++)
            {
                distance += PopCount(a[i] ^ b[i]);
            }

            return 1f - (float)distance / (a.Length * 64);
        }

        private static int PopCount(ulong x)
        {
            int count = 0;
            while (x != 0UL)
            {
                x &= x - 1UL;
                count++;
            }

            return count;
        }

        // ============================================================
        // 工具方法
        // ============================================================
        private static bool IsUnderAssets(string fullPath)
        {
            return !string.IsNullOrEmpty(ToAssetPath(fullPath));
        }

        /// <summary>判断纹理是否为工程内资源（决定其生命周期是否由本工具管理）。</summary>
        private static bool IsAssetTexture(Texture2D tex)
        {
            return tex != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(tex));
        }

        /// <summary>获取资源纹理的磁盘绝对路径；非资源返回空。</summary>
        private static string GetAssetFullPath(Texture2D tex)
        {
            if (tex == null)
            {
                return null;
            }

            string assetPath = AssetDatabase.GetAssetPath(tex);
            return string.IsNullOrEmpty(assetPath) ? null : Path.GetFullPath(assetPath);
        }

        /// <summary>把磁盘绝对路径转换为 "Assets/..." 资源路径；不在项目内则返回空。</summary>
        private static string ToAssetPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return null;
            }

            string normalized = fullPath.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');
            if (normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Assets" + normalized.Substring(dataPath.Length);
            }

            return null;
        }

        private static bool IsFileDecodable(string lowerExt)
        {
            return lowerExt == ".png" || lowerExt == ".jpg" || lowerExt == ".jpeg";
        }

        private static string FormatDuration(double milliseconds)
        {
            if (milliseconds < 1000.0)
            {
                return $"{milliseconds:F0} ms";
            }

            double seconds = milliseconds / 1000.0;
            if (seconds < 60.0)
            {
                return $"{seconds:F1} 秒";
            }

            double minutes = seconds / 60.0;
            return $"{minutes:F1} 分钟";
        }

        // ============================================================
        // 剪贴板图片读取（仅 Windows 编辑器）
        // ============================================================
        private static class ClipboardImage
        {
            /// <summary>尝试从系统剪贴板读取一张图片，失败返回 null 并给出错误说明。</summary>
            public static Texture2D TryGetImage(out string sourcePath, out string error)
            {
                sourcePath = null;
                error = null;

#if UNITY_EDITOR_WIN
                try
                {
                    // 优先处理复制的图片文件（CF_HDROP），可保留原始格式。
                    Texture2D fromFile = TryGetFromFileDrop(out sourcePath, out error);
                    if (fromFile != null)
                    {
                        return fromFile;
                    }

                    // 其次处理位图数据（CF_DIB），例如截图工具。
                    return TryGetFromDib(out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return null;
                }
#else
                error = "当前仅支持 Windows 编辑器读取剪贴板图片。";
                return null;
#endif
            }

#if UNITY_EDITOR_WIN
            private const uint CF_DIB = 8;
            private const uint CF_HDROP = 15;

            [DllImport("user32.dll")]
            private static extern bool OpenClipboard(IntPtr hWndNewOwner);

            [DllImport("user32.dll")]
            private static extern bool CloseClipboard();

            [DllImport("user32.dll")]
            private static extern IntPtr GetClipboardData(uint uFormat);

            [DllImport("user32.dll")]
            private static extern bool IsClipboardFormatAvailable(uint format);

            [DllImport("kernel32.dll")]
            private static extern IntPtr GlobalLock(IntPtr hMem);

            [DllImport("kernel32.dll")]
            private static extern bool GlobalUnlock(IntPtr hMem);

            [DllImport("kernel32.dll")]
            private static extern UIntPtr GlobalSize(IntPtr hMem);

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

            private static Texture2D TryGetFromFileDrop(out string sourcePath, out string error)
            {
                sourcePath = null;
                error = null;

                if (!IsClipboardFormatAvailable(CF_HDROP))
                {
                    return null;
                }

                if (!OpenClipboard(IntPtr.Zero))
                {
                    return null;
                }

                try
                {
                    IntPtr hDrop = GetClipboardData(CF_HDROP);
                    if (hDrop == IntPtr.Zero)
                    {
                        return null;
                    }

                    var sb = new StringBuilder(1024);
                    uint got = DragQueryFile(hDrop, 0, sb, (uint)sb.Capacity);
                    if (got == 0)
                    {
                        return null;
                    }

                    string path = sb.ToString();
                    if (!File.Exists(path))
                    {
                        return null;
                    }

                    Texture2D tex = LoadTextureFromFileBytes(path);
                    if (tex == null)
                    {
                        error = "剪贴板文件无法解码（仅支持 PNG / JPG）。";
                        return null;
                    }

                    sourcePath = Path.GetFullPath(path);
                    return tex;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            private static Texture2D TryGetFromDib(out string error)
            {
                error = null;

                if (!IsClipboardFormatAvailable(CF_DIB))
                {
                    error = "剪贴板中没有图片。";
                    return null;
                }

                if (!OpenClipboard(IntPtr.Zero))
                {
                    error = "无法打开剪贴板。";
                    return null;
                }

                try
                {
                    IntPtr handle = GetClipboardData(CF_DIB);
                    if (handle == IntPtr.Zero)
                    {
                        error = "剪贴板数据为空。";
                        return null;
                    }

                    IntPtr ptr = GlobalLock(handle);
                    if (ptr == IntPtr.Zero)
                    {
                        error = "锁定剪贴板数据失败。";
                        return null;
                    }

                    try
                    {
                        int size = (int)GlobalSize(handle).ToUInt64();
                        var dib = new byte[size];
                        Marshal.Copy(ptr, dib, 0, size);
                        return DibToTexture(dib, out error);
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }

            /// <summary>解析 DIB（BITMAPINFOHEADER + 像素数据），仅支持 24/32 位未压缩位图。</summary>
            private static Texture2D DibToTexture(byte[] dib, out string error)
            {
                error = null;
                if (dib == null || dib.Length < 40)
                {
                    error = "DIB 数据不完整。";
                    return null;
                }

                int headerSize = BitConverter.ToInt32(dib, 0);
                int width = BitConverter.ToInt32(dib, 4);
                int height = BitConverter.ToInt32(dib, 8);
                short bitCount = BitConverter.ToInt16(dib, 14);
                int compression = BitConverter.ToInt32(dib, 16);

                if (bitCount != 24 && bitCount != 32)
                {
                    error = $"暂不支持 {bitCount} 位位图（仅支持 24/32 位）。";
                    return null;
                }

                // BI_RGB(0) 与 BI_BITFIELDS(3) 支持；后者头部后额外含 12 字节颜色掩码。
                if (compression != 0 && compression != 3)
                {
                    error = "暂不支持压缩位图。";
                    return null;
                }

                bool topDown = height < 0;
                int absHeight = Mathf.Abs(height);
                int bytesPerPixel = bitCount / 8;

                int pixelOffset = headerSize;
                if (compression == 3)
                {
                    pixelOffset += 12;
                }

                // 每行按 4 字节对齐。
                int rowSize = ((width * bitCount + 31) / 32) * 4;

                long needed = (long)pixelOffset + (long)rowSize * absHeight;
                if (needed > dib.Length)
                {
                    error = "DIB 像素数据长度不足。";
                    return null;
                }

                var pixels = new Color32[width * absHeight];
                for (int y = 0; y < absHeight; y++)
                {
                    // Unity 纹理原点在左下；DIB 未翻转时首行为底行。
                    int srcRow = topDown ? (absHeight - 1 - y) : y;
                    int rowStart = pixelOffset + srcRow * rowSize;

                    for (int x = 0; x < width; x++)
                    {
                        int idx = rowStart + x * bytesPerPixel;
                        byte b = dib[idx];
                        byte g = dib[idx + 1];
                        byte r = dib[idx + 2];
                        // 截图类 32 位位图 alpha 常为 0，这里统一按不透明处理。
                        pixels[y * width + x] = new Color32(r, g, b, 255);
                    }
                }

                var tex = new Texture2D(width, absHeight, TextureFormat.RGBA32, false);
                tex.SetPixels32(pixels);
                tex.Apply(false);
                return tex;
            }
#endif
        }
    }
}
