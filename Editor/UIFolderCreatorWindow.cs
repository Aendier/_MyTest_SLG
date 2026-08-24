using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools
{
    /// <summary>
    /// UI 目录创建工具
    /// 在多个镜像根目录下，按“递进命名规则”批量创建 UI 资源目录树
    /// 采用 IMGUI 实现，仅依赖 Unity 内置 API
    /// </summary>
    public class UIFolderCreatorWindow : EditorWindow
    {
        // ==================== 配置：根目录 ====================

        /// <summary>
        /// 需要镜像创建目录树的根目录列表
        /// 扩展时只需在此追加新的根目录，无需修改创建逻辑
        /// </summary>
        private static readonly string[] RootFolders =
        {
            "Assets/GameAssets/Art/UI/Prefab",
            "Assets/GameAssets/Art/UI/RawTexture",
            "Assets/GameAssets/Art/UI/SpriteAtlas/Sprite"
        };

        /// <summary>单个根目录的创建模式</summary>
        private enum CreateMode
        {
            // 逐层嵌套，子层使用纯名字：Root/UI_Alliance/War/Reward
            Nested,

            // 平铺在根目录，使用完整语义名：Root/UI_Alliance、Root/UI_Alliance_War、Root/UI_Alliance_War_Reward
            FullSemantic
        }

        // ==================== EditorPrefs 键 ====================

        private const string PrefKeySystem = "UIFolderCreator.System";
        private const string PrefKeyHierarchy = "UIFolderCreator.Hierarchy";
        private const string PrefKeyHistory = "UIFolderCreator.History";
        private const string PrefKeyRoots = "UIFolderCreator.Roots";
        private const string PrefKeyRootModes = "UIFolderCreator.RootModes";

        /// <summary>Hierarchy / History 在 EditorPrefs 中的分隔符（换行不会出现在合法名称里）</summary>
        private const char PrefListSeparator = '\n';

        /// <summary>History 最大保存条数</summary>
        private const int MaxHistoryCount = 10;

        // ==================== 运行时字段 ====================

        [Tooltip("系统完整名称，例如 RechargeShop，无需输入 UI_ 前缀")]
        private string _systemName = string.Empty;

        [Tooltip("层级列表，从第二层开始每层使用各自的纯名字")]
        private readonly List<string> _hierarchy = new List<string> { string.Empty };

        /// <summary>最近使用过的系统名历史记录</summary>
        private readonly List<string> _history = new List<string>();

        /// <summary>各根目录是否参与创建，索引与 RootFolders 一一对应，默认全部开启</summary>
        private bool[] _rootEnabled;

        /// <summary>各根目录的创建模式，索引与 RootFolders 一一对应，默认 Nested</summary>
        private CreateMode[] _rootMode;

        private Vector2 _scroll;

        // ==================== 窗口入口 ====================

        /// <summary>打开窗口（菜单注册统一在 UIRMenuRegister 中）</summary>
        public static void Open()
        {
            var window = GetWindow<UIFolderCreatorWindow>("UI Folder Creator");
            window.minSize = new Vector2(420f, 420f);
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

        // ==================== GUI 主入口 ====================

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawSystem();
            EditorGUILayout.Space();

            DrawHierarchy();
            EditorGUILayout.Space();

            DrawRoots();
            EditorGUILayout.Space();

            List<string> preview = GenerateFolderNames();
            DrawPreview(preview);
            EditorGUILayout.Space();

            DrawButtons(preview);

            EditorGUILayout.EndScrollView();
        }

        // ==================== 区域绘制 ====================

        /// <summary>绘制 System Name / History</summary>
        private void DrawSystem()
        {
            EditorGUILayout.LabelField("System", EditorStyles.boldLabel);

            _systemName = EditorGUILayout.TextField(
                new GUIContent("System Name", "系统完整名称，例如 RechargeShop（无需输入 UI_）"),
                _systemName);

            DrawHistory();
        }

        /// <summary>绘制历史记录下拉，便于快速填充系统名</summary>
        private void DrawHistory()
        {
            if (_history.Count == 0)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    new GUIContent("History", "最近使用过的系统名，选择后自动填入 System Name"),
                    GUILayout.Width(EditorGUIUtility.labelWidth));

                // 首项为占位提示，避免误触
                var options = new string[_history.Count + 1];
                options[0] = "(Select...)";
                for (int i = 0; i < _history.Count; i++)
                {
                    options[i + 1] = _history[i];
                }

                int selected = EditorGUILayout.Popup(0, options);
                if (selected > 0)
                {
                    _systemName = _history[selected - 1];
                    GUI.FocusControl(null);
                }
            }
        }

        /// <summary>绘制可增删的层级列表</summary>
        private void DrawHierarchy()
        {
            EditorGUILayout.LabelField("Hierarchy", EditorStyles.boldLabel);

            int removeIndex = -1;
            for (int i = 0; i < _hierarchy.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _hierarchy[i] = EditorGUILayout.TextField(_hierarchy[i]);

                    if (GUILayout.Button("+", GUILayout.Width(24f)))
                    {
                        // 在当前行下方插入新层
                        _hierarchy.Insert(i + 1, string.Empty);
                    }

                    // 至少保留一行，不允许删空
                    using (new EditorGUI.DisabledScope(_hierarchy.Count <= 1))
                    {
                        if (GUILayout.Button("-", GUILayout.Width(24f)))
                        {
                            removeIndex = i;
                        }
                    }
                }
            }

            if (removeIndex >= 0)
            {
                _hierarchy.RemoveAt(removeIndex);
            }

            if (GUILayout.Button("Add"))
            {
                _hierarchy.Add(string.Empty);
            }
        }

        /// <summary>绘制根目录列表：每个根目录可单独勾选是否创建，并单独选择创建模式</summary>
        private void DrawRoots()
        {
            EnsureRootArrays();

            EditorGUILayout.LabelField("Roots", EditorStyles.boldLabel);

            for (int i = 0; i < RootFolders.Length; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _rootEnabled[i] = EditorGUILayout.ToggleLeft(
                        new GUIContent(GetRootLabel(RootFolders[i]), RootFolders[i]),
                        _rootEnabled[i],
                        GUILayout.Width(140f));

                    // 未勾选时禁用模式选择
                    using (new EditorGUI.DisabledScope(!_rootEnabled[i]))
                    {
                        _rootMode[i] = (CreateMode)EditorGUILayout.EnumPopup(_rootMode[i]);
                    }
                }
            }

            if (!AnyRootEnabled())
            {
                EditorGUILayout.HelpBox("至少勾选一个根目录才能创建。", MessageType.Warning);
            }
        }

        /// <summary>绘制实时预览：按每个已勾选根目录及其模式分组展示</summary>
        private void DrawPreview(List<string> segments)
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            if (segments.Count == 0)
            {
                EditorGUILayout.HelpBox("请填写 System Name。", MessageType.Info);
                return;
            }

            if (!AnyRootEnabled())
            {
                EditorGUILayout.HelpBox("请至少勾选一个根目录。", MessageType.Info);
                return;
            }

            EditorGUILayout.TextArea(BuildPreviewText(segments), GUILayout.MinHeight(80f));
        }

        /// <summary>绘制操作按钮</summary>
        private void DrawButtons(List<string> segments)
        {
            using (new EditorGUI.DisabledScope(segments.Count == 0 || !AnyRootEnabled()))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Copy Preview", GUILayout.Height(28f)))
                    {
                        EditorGUIUtility.systemCopyBuffer = BuildPreviewText(segments);
                    }

                    if (GUILayout.Button("Create", GUILayout.Height(28f)))
                    {
                        CreateFolders(segments);
                    }
                }
            }
        }

        // ==================== 命名逻辑 ====================

        /// <summary>
        /// 生成层级分段列表（Nested 模式直接使用）
        /// 规则：第一层固定 UI_完整系统名；第二层起每层直接使用各自的纯名字
        /// 例如 [UI_Alliance, War, Reward]
        /// </summary>
        private List<string> GenerateFolderNames()
        {
            var result = new List<string>();

            string system = NormalizeInput(_systemName);
            if (string.IsNullOrEmpty(system))
            {
                return result;
            }

            // 第一层：永远为 UI_完整系统名
            result.Add("UI_" + system);

            // 第二层起：每层直接使用各自的纯名字，不再累积拼接语义
            foreach (string raw in _hierarchy)
            {
                string child = NormalizeInput(raw);
                if (string.IsNullOrEmpty(child))
                {
                    continue;
                }

                result.Add(child);
            }

            return result;
        }

        /// <summary>
        /// 由分段列表构造完整语义名列表（FullSemantic 模式使用）
        /// 逐段用下划线累积，例如 [UI_Alliance, War, Reward] -> [UI_Alliance, UI_Alliance_War, UI_Alliance_War_Reward]
        /// </summary>
        private static List<string> BuildFullSemanticNames(List<string> segments)
        {
            var result = new List<string>(segments.Count);
            string cumulative = null;
            foreach (string seg in segments)
            {
                cumulative = cumulative == null ? seg : cumulative + "_" + seg;
                result.Add(cumulative);
            }

            return result;
        }

        /// <summary>构造预览文本：按每个已勾选根目录及其模式分组</summary>
        private string BuildPreviewText(List<string> segments)
        {
            var full = BuildFullSemanticNames(segments);
            var sb = new StringBuilder();

            bool firstGroup = true;
            for (int i = 0; i < RootFolders.Length; i++)
            {
                if (!_rootEnabled[i])
                {
                    continue;
                }

                if (!firstGroup)
                {
                    sb.AppendLine();
                }

                firstGroup = false;

                sb.AppendLine(GetRootLabel(RootFolders[i]) + "  [" + ModeLabel(_rootMode[i]) + "]");

                if (_rootMode[i] == CreateMode.Nested)
                {
                    // 嵌套模式：按 / 逐层展示相对路径
                    string cumulative = null;
                    foreach (string seg in segments)
                    {
                        cumulative = cumulative == null ? seg : cumulative + "/" + seg;
                        sb.AppendLine("  " + cumulative);
                    }
                }
                else
                {
                    // 完整语义模式：各完整名平铺在根目录下
                    foreach (string name in full)
                    {
                        sb.AppendLine("  " + name);
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>创建模式的显示名</summary>
        private static string ModeLabel(CreateMode mode)
        {
            return mode == CreateMode.Nested ? "Nested" : "Full Semantic";
        }

        /// <summary>
        /// 规范化用户输入：去除 UI_ 前缀、空格与下划线
        /// 例如 "UI_Recharge Shop" / "Recharge__Shop" / "RechargeShop_" 均得到 "RechargeShop"
        /// </summary>
        private static string NormalizeInput(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            string value = input.Trim();

            // 反复剥离可能出现的 UI_ 前缀（大小写不敏感）
            while (value.Length >= 3 &&
                   (value[0] == 'U' || value[0] == 'u') &&
                   (value[1] == 'I' || value[1] == 'i') &&
                   value[2] == '_')
            {
                value = value.Substring(3).TrimStart();
            }

            // 去除所有空白与下划线，得到单一 token
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || c == '_')
                {
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        // ==================== 目录创建 ====================

        /// <summary>按每个已勾选根目录各自的模式创建目录</summary>
        private void CreateFolders(List<string> segments)
        {
            if (segments == null || segments.Count == 0)
            {
                return;
            }

            EnsureRootArrays();
            if (!AnyRootEnabled())
            {
                return;
            }

            var full = BuildFullSemanticNames(segments);
            var created = new List<string>();
            var existed = new List<string>();
            string deepestFolderOfFirstEnabledRoot = null;

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int r = 0; r < RootFolders.Length; r++)
                {
                    // 跳过未勾选的根目录
                    if (!_rootEnabled[r])
                    {
                        continue;
                    }

                    string root = RootFolders[r];
                    string label = GetRootLabel(root);

                    // 确保根目录本身存在
                    EnsureFolderPath(root);

                    string deepest;
                    if (_rootMode[r] == CreateMode.Nested)
                    {
                        deepest = CreateNested(root, label, segments, created, existed);
                    }
                    else
                    {
                        deepest = CreateFullSemantic(root, label, full, created, existed);
                    }

                    // 记录首个已勾选根目录的最深一级，用于创建后选中
                    if (deepestFolderOfFirstEnabledRoot == null)
                    {
                        deepestFolderOfFirstEnabledRoot = deepest;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            // 记录历史并持久化
            AddHistory(NormalizeInput(_systemName));
            SavePrefs();

            LogResult(created, existed);

            // 自动选中首个已勾选根目录下最深一级目录
            if (!string.IsNullOrEmpty(deepestFolderOfFirstEnabledRoot))
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(deepestFolderOfFirstEnabledRoot);
                if (obj != null)
                {
                    Selection.activeObject = obj;
                    EditorGUIUtility.PingObject(obj);
                }
            }
        }

        /// <summary>
        /// Nested 模式：在 root 下逐层嵌套创建各分段目录
        /// 返回最深一级目录的完整路径
        /// </summary>
        private static string CreateNested(string root, string label, List<string> segments,
            List<string> created, List<string> existed)
        {
            string parent = root;
            foreach (string seg in segments)
            {
                bool wasCreated = EnsureFolder(parent, seg);
                parent += "/" + seg;

                // 日志显示根目录下的相对路径，例如 Prefab/UI_Alliance/War
                string relative = parent.Substring(root.Length + 1);
                (wasCreated ? created : existed).Add(label + "/" + relative);
            }

            return parent;
        }

        /// <summary>
        /// FullSemantic 模式：将各完整语义名平铺创建在 root 下（互为同级）
        /// 返回最深（最后一个）完整名目录的完整路径
        /// </summary>
        private static string CreateFullSemantic(string root, string label, List<string> fullNames,
            List<string> created, List<string> existed)
        {
            string deepest = root;
            foreach (string name in fullNames)
            {
                bool wasCreated = EnsureFolder(root, name);
                deepest = root + "/" + name;
                (wasCreated ? created : existed).Add(label + "/" + name);
            }

            return deepest;
        }

        /// <summary>
        /// 确保 parent 下存在名为 folderName 的子目录
        /// 返回 true 表示本次新建，false 表示已存在
        /// </summary>
        private static bool EnsureFolder(string parent, string folderName)
        {
            string full = parent + "/" + folderName;
            if (AssetDatabase.IsValidFolder(full))
            {
                return false;
            }

            AssetDatabase.CreateFolder(parent, folderName);
            return true;
        }

        /// <summary>确保一条以 Assets 开头的目录路径逐级存在（用于根目录本身可能缺失的情况）</summary>
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

        /// <summary>确保勾选/模式数组已初始化且长度与 RootFolders 一致（新增根目录默认：开启 + Nested）</summary>
        private void EnsureRootArrays()
        {
            if (_rootEnabled == null || _rootEnabled.Length != RootFolders.Length)
            {
                var resized = new bool[RootFolders.Length];
                for (int i = 0; i < resized.Length; i++)
                {
                    // 沿用已有值，超出部分默认开启
                    resized[i] = _rootEnabled != null && i < _rootEnabled.Length ? _rootEnabled[i] : true;
                }

                _rootEnabled = resized;
            }

            if (_rootMode == null || _rootMode.Length != RootFolders.Length)
            {
                var resized = new CreateMode[RootFolders.Length];
                for (int i = 0; i < resized.Length; i++)
                {
                    // 沿用已有值，超出部分默认 Nested
                    resized[i] = _rootMode != null && i < _rootMode.Length ? _rootMode[i] : CreateMode.Nested;
                }

                _rootMode = resized;
            }
        }

        /// <summary>是否至少勾选了一个根目录</summary>
        private bool AnyRootEnabled()
        {
            EnsureRootArrays();
            for (int i = 0; i < _rootEnabled.Length; i++)
            {
                if (_rootEnabled[i])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>取根目录用于日志显示的短标签（末级目录名）</summary>
        private static string GetRootLabel(string root)
        {
            int idx = root.LastIndexOf('/');
            return idx >= 0 ? root.Substring(idx + 1) : root;
        }

        /// <summary>输出创建结果到 Console</summary>
        private static void LogResult(List<string> created, List<string> existed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================");
            sb.AppendLine("UI Folder Creator");
            sb.AppendLine("========================");

            sb.AppendLine();
            sb.AppendLine("Created:");
            if (created.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (string c in created)
                {
                    sb.AppendLine(c);
                }
            }

            sb.AppendLine();
            sb.AppendLine("Already Exists:");
            if (existed.Count == 0)
            {
                sb.AppendLine("(none)");
            }
            else
            {
                foreach (string e in existed)
                {
                    sb.AppendLine(e);
                }
            }

            sb.AppendLine();
            sb.AppendLine("Finished.");

            Debug.Log(sb.ToString());
        }

        // ==================== 历史记录 ====================

        /// <summary>将系统名加入历史（去重、置顶、限长）</summary>
        private void AddHistory(string system)
        {
            if (string.IsNullOrEmpty(system))
            {
                return;
            }

            _history.RemoveAll(h => h == system);
            _history.Insert(0, system);

            while (_history.Count > MaxHistoryCount)
            {
                _history.RemoveAt(_history.Count - 1);
            }
        }

        // ==================== EditorPrefs 读写 ====================

        /// <summary>从 EditorPrefs 恢复上次输入</summary>
        private void LoadPrefs()
        {
            _systemName = EditorPrefs.GetString(PrefKeySystem, string.Empty);

            _hierarchy.Clear();
            string hierarchyRaw = EditorPrefs.GetString(PrefKeyHierarchy, string.Empty);
            foreach (string item in hierarchyRaw.Split(PrefListSeparator))
            {
                _hierarchy.Add(item);
            }

            // 至少保留一行
            if (_hierarchy.Count == 0)
            {
                _hierarchy.Add(string.Empty);
            }

            _history.Clear();
            string historyRaw = EditorPrefs.GetString(PrefKeyHistory, string.Empty);
            if (!string.IsNullOrEmpty(historyRaw))
            {
                foreach (string item in historyRaw.Split(PrefListSeparator))
                {
                    if (!string.IsNullOrEmpty(item))
                    {
                        _history.Add(item);
                    }
                }
            }

            LoadRootSettings();
        }

        /// <summary>从 EditorPrefs 恢复根目录勾选与模式（无记录或长度不足时用默认：开启 + Nested）</summary>
        private void LoadRootSettings()
        {
            _rootEnabled = new bool[RootFolders.Length];
            // 勾选：以 '1'/'0' 字符序列保存，索引与 RootFolders 对应
            string flags = EditorPrefs.GetString(PrefKeyRoots, string.Empty);
            for (int i = 0; i < _rootEnabled.Length; i++)
            {
                _rootEnabled[i] = i < flags.Length ? flags[i] == '1' : true;
            }

            _rootMode = new CreateMode[RootFolders.Length];
            // 模式：'1' 表示 FullSemantic，其余（含缺省）为 Nested
            string modes = EditorPrefs.GetString(PrefKeyRootModes, string.Empty);
            for (int i = 0; i < _rootMode.Length; i++)
            {
                _rootMode[i] = i < modes.Length && modes[i] == '1' ? CreateMode.FullSemantic : CreateMode.Nested;
            }
        }

        /// <summary>将当前输入写入 EditorPrefs</summary>
        private void SavePrefs()
        {
            EditorPrefs.SetString(PrefKeySystem, _systemName ?? string.Empty);
            EditorPrefs.SetString(PrefKeyHierarchy, string.Join(PrefListSeparator.ToString(), _hierarchy));
            EditorPrefs.SetString(PrefKeyHistory, string.Join(PrefListSeparator.ToString(), _history));

            EnsureRootArrays();
            var flags = new StringBuilder(_rootEnabled.Length);
            var modes = new StringBuilder(_rootMode.Length);
            for (int i = 0; i < _rootEnabled.Length; i++)
            {
                flags.Append(_rootEnabled[i] ? '1' : '0');
                modes.Append(_rootMode[i] == CreateMode.FullSemantic ? '1' : '0');
            }

            EditorPrefs.SetString(PrefKeyRoots, flags.ToString());
            EditorPrefs.SetString(PrefKeyRootModes, modes.ToString());
        }
    }
}
