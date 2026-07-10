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

        // ==================== EditorPrefs 键 ====================

        private const string PrefKeySystem = "UIFolderCreator.System";
        private const string PrefKeyAbbr = "UIFolderCreator.Abbr";
        private const string PrefKeyHierarchy = "UIFolderCreator.Hierarchy";
        private const string PrefKeyHistory = "UIFolderCreator.History";

        /// <summary>Hierarchy / History 在 EditorPrefs 中的分隔符（换行不会出现在合法名称里）</summary>
        private const char PrefListSeparator = '\n';

        /// <summary>History 最大保存条数</summary>
        private const int MaxHistoryCount = 10;

        // ==================== 运行时字段 ====================

        [Tooltip("系统完整名称，例如 RechargeShop，无需输入 UI_ 前缀")]
        private string _systemName = string.Empty;

        [Tooltip("系统缩写，例如 RS，可为空；为空时使用完整系统名")]
        private string _abbreviation = string.Empty;

        [Tooltip("层级列表，从第二层开始逐层递进拼接")]
        private readonly List<string> _hierarchy = new List<string> { string.Empty };

        /// <summary>最近使用过的系统名历史记录</summary>
        private readonly List<string> _history = new List<string>();

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

            List<string> preview = GenerateFolderNames();
            DrawPreview(preview);
            EditorGUILayout.Space();

            DrawButtons(preview);

            EditorGUILayout.EndScrollView();
        }

        // ==================== 区域绘制 ====================

        /// <summary>绘制 System Name / Abbreviation / History</summary>
        private void DrawSystem()
        {
            EditorGUILayout.LabelField("System", EditorStyles.boldLabel);

            _systemName = EditorGUILayout.TextField(
                new GUIContent("System Name", "系统完整名称，例如 RechargeShop（无需输入 UI_）"),
                _systemName);

            _abbreviation = EditorGUILayout.TextField(
                new GUIContent("Abbreviation", "系统缩写，例如 RS；为空时使用完整系统名"),
                _abbreviation);

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

        /// <summary>绘制实时预览</summary>
        private void DrawPreview(List<string> preview)
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            if (preview.Count == 0)
            {
                EditorGUILayout.HelpBox("请填写 System Name。", MessageType.Info);
                return;
            }

            EditorGUILayout.TextArea(string.Join("\n", preview), GUILayout.MinHeight(80f));
        }

        /// <summary>绘制操作按钮</summary>
        private void DrawButtons(List<string> preview)
        {
            using (new EditorGUI.DisabledScope(preview.Count == 0))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Copy Preview", GUILayout.Height(28f)))
                    {
                        EditorGUIUtility.systemCopyBuffer = string.Join("\n", preview);
                    }

                    if (GUILayout.Button("Create", GUILayout.Height(28f)))
                    {
                        CreateFolders(preview);
                    }
                }
            }
        }

        // ==================== 命名逻辑 ====================

        /// <summary>
        /// 依据递进命名规则生成从第一层到最后一层的完整目录名列表
        /// 规则：第一层固定 UI_完整系统名；第二层 UI_前缀_子名；第三层起在上一层名基础上追加 _子名
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
            string current = "UI_" + system;
            result.Add(current);

            // 第二层起使用的前缀：有缩写用缩写，否则用完整系统名
            string abbr = NormalizeInput(_abbreviation);
            string prefix = string.IsNullOrEmpty(abbr) ? system : abbr;

            bool first = true;
            foreach (string raw in _hierarchy)
            {
                string child = NormalizeInput(raw);
                if (string.IsNullOrEmpty(child))
                {
                    continue;
                }

                if (first)
                {
                    // 第二层重新以前缀拼接
                    current = "UI_" + prefix + "_" + child;
                    first = false;
                }
                else
                {
                    // 第三层起在上一层名称上递进追加
                    current += "_" + child;
                }

                result.Add(current);
            }

            return result;
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

        /// <summary>按预览名列表在每个根目录下镜像创建目录树</summary>
        private void CreateFolders(List<string> names)
        {
            if (names == null || names.Count == 0)
            {
                return;
            }

            var created = new List<string>();
            var existed = new List<string>();
            string lastFolderOfFirstRoot = null;

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int r = 0; r < RootFolders.Length; r++)
                {
                    string root = RootFolders[r];
                    string label = GetRootLabel(root);

                    // 确保根目录本身存在
                    EnsureFolderPath(root);

                    string parent = root;
                    foreach (string name in names)
                    {
                        bool wasCreated = EnsureFolder(parent, name);
                        string full = parent + "/" + name;

                        if (wasCreated)
                        {
                            created.Add(label + "/" + name);
                        }
                        else
                        {
                            existed.Add(label + "/" + name);
                        }

                        parent = full;
                    }

                    // 记录首个根目录最深一级，用于创建后选中
                    if (r == 0)
                    {
                        lastFolderOfFirstRoot = parent;
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

            // 自动选中首个根目录下最深一级目录
            if (!string.IsNullOrEmpty(lastFolderOfFirstRoot))
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(lastFolderOfFirstRoot);
                if (obj != null)
                {
                    Selection.activeObject = obj;
                    EditorGUIUtility.PingObject(obj);
                }
            }
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
            _abbreviation = EditorPrefs.GetString(PrefKeyAbbr, string.Empty);

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
        }

        /// <summary>将当前输入写入 EditorPrefs</summary>
        private void SavePrefs()
        {
            EditorPrefs.SetString(PrefKeySystem, _systemName ?? string.Empty);
            EditorPrefs.SetString(PrefKeyAbbr, _abbreviation ?? string.Empty);
            EditorPrefs.SetString(PrefKeyHierarchy, string.Join(PrefListSeparator.ToString(), _hierarchy));
            EditorPrefs.SetString(PrefKeyHistory, string.Join(PrefListSeparator.ToString(), _history));
        }
    }
}
