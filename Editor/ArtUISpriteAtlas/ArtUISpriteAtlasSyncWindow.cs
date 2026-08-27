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
    /// Art UI 图集一键同步工具（新规范）。
    /// 图集根 = 源图根目录下一层完整语义文件夹（名称自定，工具不加前缀）；
    /// 产出 = 图集根/&lt;文件夹名&gt;.spriteatlas（与文件夹同名，平铺）；
    /// Sprite/Atlas 路径可在窗口中自填；更深子目录仅整理；禁止父子目录跨图集引用。
    /// </summary>
    public class ArtUISpriteAtlasSyncWindow : EditorWindow
    {
        private const string DefaultSpriteRoot = "Assets/GameAssets/Art/UI/SpriteAtlas/Sprite";
        private const string DefaultAtlasRoot = "Assets/GameAssets/Art/UI/SpriteAtlas/Atlas";

        private const string PrefKeySpriteRoot = "ArtUISpriteAtlasSync.SpriteRoot";
        private const string PrefKeyAtlasRoot = "ArtUISpriteAtlasSync.AtlasRoot";
        private const string PrefKeyPadding = "ArtUISpriteAtlasSync.Padding";
        private const string PrefKeyMaxSize = "ArtUISpriteAtlasSync.MaxSize";
        private const string PrefKeyExportMap = "ArtUISpriteAtlasSync.ExportMap";
        private const string PrefKeyDeleteOrphans = "ArtUISpriteAtlasSync.DeleteOrphans";
        private const string PrefKeyDeleteEmptyFolders = "ArtUISpriteAtlasSync.DeleteEmptyFolders";

        private const int LargeImageWarnThreshold = 1024;

        private static readonly int[] MaxSizeOptions = { 1024, 2048, 4096 };
        private static readonly int[] PaddingOptions = { 2, 4, 8 };

        private string _spriteRoot = DefaultSpriteRoot;
        private string _atlasRoot = DefaultAtlasRoot;
        private int _maxSize = 2048;
        private int _padding = 4;
        private bool _exportMap = true;
        private bool _deleteOrphans;
        /// <summary>同步时删除源图根/图集根下的空文件夹（不含根自身）</summary>
        private bool _deleteEmptyFolders;

        private Vector2 _scroll;
        private string _lastReport = string.Empty;

        private List<AtlasPlan> _plans = new List<AtlasPlan>();
        private List<string> _looseSpriteWarnings = new List<string>();
        private List<string> _largeImageWarnings = new List<string>();
        private List<string> _orphanAtlasPaths = new List<string>();
        private List<string> _emptyFolderPaths = new List<string>();
        private readonly List<RuleIssue> _ruleIssues = new List<RuleIssue>();

        private static MethodInfo _getWidthAndHeightMethod;

        private enum RuleSeverity
        {
            Error,
            Warning
        }

        private class RuleIssue
        {
            public RuleSeverity Severity;
            public string Message;
            public string AssetPath;
        }

        private class AtlasPlan
        {
            public string ModuleName;
            public string ModuleFolder;
            public string AtlasName;
            public string AtlasPath;
            public string LegacySourcePath;
            public int SpriteCount;
            public bool AtlasExists;
            public bool NeedsPackableUpdate;
            public bool NeedsMigrate;
        }

        public static void Open()
        {
            var window = GetWindow<ArtUISpriteAtlasSyncWindow>("同步 Art UI 图集");
            window.minSize = new Vector2(520f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            _spriteRoot = NormalizeAssetPath(EditorPrefs.GetString(PrefKeySpriteRoot, DefaultSpriteRoot));
            _atlasRoot = NormalizeAssetPath(EditorPrefs.GetString(PrefKeyAtlasRoot, DefaultAtlasRoot));
            if (string.IsNullOrEmpty(_spriteRoot))
            {
                _spriteRoot = DefaultSpriteRoot;
            }

            if (string.IsNullOrEmpty(_atlasRoot))
            {
                _atlasRoot = DefaultAtlasRoot;
            }

            _maxSize = EditorPrefs.GetInt(PrefKeyMaxSize, 2048);
            _padding = EditorPrefs.GetInt(PrefKeyPadding, 4);
            _exportMap = EditorPrefs.GetBool(PrefKeyExportMap, true);
            _deleteOrphans = EditorPrefs.GetBool(PrefKeyDeleteOrphans, false);
            _deleteEmptyFolders = EditorPrefs.GetBool(PrefKeyDeleteEmptyFolders, false);
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(PrefKeySpriteRoot, NormalizeAssetPath(_spriteRoot));
            EditorPrefs.SetString(PrefKeyAtlasRoot, NormalizeAssetPath(_atlasRoot));
            EditorPrefs.SetInt(PrefKeyMaxSize, _maxSize);
            EditorPrefs.SetInt(PrefKeyPadding, _padding);
            EditorPrefs.SetBool(PrefKeyExportMap, _exportMap);
            EditorPrefs.SetBool(PrefKeyDeleteOrphans, _deleteOrphans);
            EditorPrefs.SetBool(PrefKeyDeleteEmptyFolders, _deleteEmptyFolders);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Art UI 图集同步", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "新规范：\n" +
                "1. 图集根 = 源图根目录下一层完整语义文件夹（名称自定，工具不加 UI_ 前缀）\n" +
                "2. 产出 = 图集根/<文件夹名>.spriteatlas（与文件夹同名，平铺）\n" +
                "3. 更深子目录只整理，并入该图集；每个图集只引用一个文件夹\n" +
                "4. 引用必须在源图根目录下；禁止父子目录被不同图集同时引用\n" +
                "Sprite / Atlas 路径可在下方自填或拖入文件夹。",
                MessageType.Info);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("路径（可自填 / 拖文件夹）", EditorStyles.boldLabel);
            _spriteRoot = DrawFolderPathField("源图根目录", _spriteRoot, DefaultSpriteRoot);
            _atlasRoot = DrawFolderPathField("图集根目录", _atlasRoot, DefaultAtlasRoot);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("恢复默认路径", GUILayout.Width(120f)))
                {
                    _spriteRoot = DefaultSpriteRoot;
                    _atlasRoot = DefaultAtlasRoot;
                    GUI.FocusControl(null);
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("设置", EditorStyles.boldLabel);
            _maxSize = IntPopup("单页最大尺寸", _maxSize, MaxSizeOptions);
            _padding = IntPopup("Padding", _padding, PaddingOptions);
            _exportMap = EditorGUILayout.ToggleLeft("同步后导出 sprite_atlas_map", _exportMap);
            _deleteOrphans = EditorGUILayout.ToggleLeft("删除孤儿图集（目录已不存在）", _deleteOrphans);
            _deleteEmptyFolders = EditorGUILayout.ToggleLeft(
                "删除空文件夹（源图根 / 图集根下，不含根自身）", _deleteEmptyFolders);

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("预览扫描", GUILayout.Height(28f)))
                {
                    ScanPlans();
                }

                bool canSync = _plans.Count > 0
                               || (_deleteOrphans && _orphanAtlasPaths.Count > 0)
                               || (_deleteEmptyFolders && _emptyFolderPaths.Count > 0);
                using (new EditorGUI.DisabledScope(!canSync))
                {
                    if (GUILayout.Button("执行同步", GUILayout.Height(28f)))
                    {
                        ExecuteSync();
                    }
                }
            }

            DrawScanSummary();

            if (!string.IsNullOrEmpty(_lastReport))
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("上次结果", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(_lastReport, MessageType.None);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>路径文本 + 文件夹 ObjectField，支持拖拽与手填</summary>
        private string DrawFolderPathField(string label, string path, string defaultPath)
        {
            path = NormalizeAssetPath(path);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);

            string newPath = EditorGUILayout.TextField(path);
            DefaultAsset folderAsset = null;
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
            {
                folderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(path);
            }

            DefaultAsset picked = (DefaultAsset)EditorGUILayout.ObjectField(
                folderAsset, typeof(DefaultAsset), false, GUILayout.Width(180f));
            EditorGUILayout.EndHorizontal();

            if (picked != folderAsset)
            {
                string pickedPath = picked != null ? AssetDatabase.GetAssetPath(picked) : string.Empty;
                if (!string.IsNullOrEmpty(pickedPath) && AssetDatabase.IsValidFolder(pickedPath))
                {
                    return NormalizeAssetPath(pickedPath);
                }

                if (picked == null)
                {
                    return string.IsNullOrEmpty(newPath) ? defaultPath : NormalizeAssetPath(newPath);
                }
            }

            return string.IsNullOrEmpty(newPath) ? path : NormalizeAssetPath(newPath);
        }

        private int CountIssues(RuleSeverity severity)
        {
            int count = 0;
            for (int i = 0; i < _ruleIssues.Count; i++)
            {
                if (_ruleIssues[i].Severity == severity)
                {
                    count++;
                }
            }

            return count;
        }

        private void DrawScanSummary()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("扫描结果", EditorStyles.boldLabel);

            int errorCount = CountIssues(RuleSeverity.Error);
            int warnCount = CountIssues(RuleSeverity.Warning);
            EditorGUILayout.LabelField(
                $"模块计划: {_plans.Count}，规范错误: {errorCount}，规范警告: {warnCount}，" +
                $"孤儿: {_orphanAtlasPaths.Count}，空文件夹: {_emptyFolderPaths.Count}，" +
                $"根目录散图: {_looseSpriteWarnings.Count}，大图: {_largeImageWarnings.Count}");

            if (errorCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"发现 {errorCount} 个规范错误（见下方）。同步会尽量修复计划内图集的 packable；无法自动修复的请人工处理。",
                    MessageType.Error);
            }
            else if (warnCount > 0 || _looseSpriteWarnings.Count > 0 || _largeImageWarnings.Count > 0 ||
                     _orphanAtlasPaths.Count > 0 || _emptyFolderPaths.Count > 0)
            {
                EditorGUILayout.HelpBox("存在警告项，建议处理后再出包。", MessageType.Warning);
            }

            DrawRuleIssueList(RuleSeverity.Error, "规范错误");
            DrawRuleIssueList(RuleSeverity.Warning, "规范警告");

            if (_plans.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("将创建/更新", EditorStyles.miniBoldLabel);
                int show = Mathf.Min(_plans.Count, 40);
                for (int i = 0; i < show; i++)
                {
                    AtlasPlan p = _plans[i];
                    string state;
                    if (p.NeedsMigrate)
                    {
                        state = "迁入图集根";
                    }
                    else if (!p.AtlasExists)
                    {
                        state = "新建";
                    }
                    else if (p.NeedsPackableUpdate)
                    {
                        state = "更新 packable";
                    }
                    else
                    {
                        state = "已存在";
                    }

                    EditorGUILayout.LabelField($"  [{state}] {p.AtlasName}.spriteatlas  ({p.SpriteCount} sprites)");
                }

                if (_plans.Count > show)
                {
                    EditorGUILayout.LabelField($"  ... 另有 {_plans.Count - show} 项");
                }
            }

            DrawWarningList("源图根目录散图（请放入某个模块文件夹）", _looseSpriteWarnings);
            DrawWarningList(
                $"大图警告（任一边 > {LargeImageWarnThreshold}，仍会入集；建议改走 RawTexture）",
                _largeImageWarnings);
            DrawWarningList("孤儿图集（源图侧已无对应模块文件夹）", _orphanAtlasPaths);
            DrawWarningList(
                _deleteEmptyFolders ? "空文件夹（同步时将删除）" : "空文件夹（勾选「删除空文件夹」后同步可清理）",
                _emptyFolderPaths);
        }

        private void DrawRuleIssueList(RuleSeverity severity, string title)
        {
            var items = new List<RuleIssue>();
            for (int i = 0; i < _ruleIssues.Count; i++)
            {
                if (_ruleIssues[i].Severity == severity)
                {
                    items.Add(_ruleIssues[i]);
                }
            }

            if (items.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"{title} ({items.Count})", EditorStyles.miniBoldLabel);
            int show = Mathf.Min(items.Count, 30);
            for (int i = 0; i < show; i++)
            {
                RuleIssue issue = items[i];
                string line = string.IsNullOrEmpty(issue.AssetPath)
                    ? issue.Message
                    : $"{issue.Message}  |  {issue.AssetPath}";
                EditorGUILayout.LabelField("  " + line, EditorStyles.wordWrappedLabel);
            }

            if (items.Count > show)
            {
                EditorGUILayout.LabelField($"  ... 另有 {items.Count - show} 项");
            }
        }

        private static void DrawWarningList(string title, List<string> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"{title} ({items.Count})", EditorStyles.miniBoldLabel);
            int show = Mathf.Min(items.Count, 20);
            for (int i = 0; i < show; i++)
            {
                EditorGUILayout.LabelField("  " + items[i]);
            }

            if (items.Count > show)
            {
                EditorGUILayout.LabelField($"  ... 另有 {items.Count - show} 项");
            }
        }

        private void ScanPlans()
        {
            _plans = new List<AtlasPlan>();
            _looseSpriteWarnings = new List<string>();
            _largeImageWarnings = new List<string>();
            _orphanAtlasPaths = new List<string>();
            _emptyFolderPaths = new List<string>();
            _ruleIssues.Clear();
            _lastReport = string.Empty;

            _spriteRoot = NormalizeAssetPath(_spriteRoot);
            _atlasRoot = NormalizeAssetPath(_atlasRoot);

            if (string.IsNullOrEmpty(_spriteRoot) || !AssetDatabase.IsValidFolder(_spriteRoot))
            {
                AddIssue(RuleSeverity.Error, $"源图根目录无效或不存在: {_spriteRoot}", null);
                EditorUtility.DisplayDialog("同步 Art UI 图集", $"源图根目录无效或不存在:\n{_spriteRoot}", "确定");
                Repaint();
                return;
            }

            if (string.IsNullOrEmpty(_atlasRoot))
            {
                AddIssue(RuleSeverity.Error, "图集根目录不能为空", null);
                Repaint();
                return;
            }

            EnsureFolderExists(_atlasRoot);
            CollectLooseSpritesAtSpriteRoot();
            WarnLegacyTwoLevelLayout();

            var expectedAtlasPaths = new HashSet<string>();
            var pendingMigrateSources = new HashSet<string>();
            var validatedAtlasPaths = new HashSet<string>();

            foreach (string moduleName in ListSubFolderNames(_spriteRoot))
            {
                if (moduleName.StartsWith(".") || moduleName == "Resources")
                {
                    continue;
                }

                string moduleFolder = _spriteRoot + "/" + moduleName;
                int spriteCount = CountSprites(moduleFolder);
                if (spriteCount <= 0)
                {
                    AddIssue(
                        RuleSeverity.Warning,
                        $"模块目录下无 Sprite，不会生成图集: {moduleName}",
                        moduleFolder);
                    continue;
                }

                string atlasName = moduleName;
                string atlasPath = _atlasRoot + "/" + atlasName + ".spriteatlas";
                expectedAtlasPaths.Add(atlasPath);

                string legacySource = FindFirstExistingAtlasPath(CollectLegacyAtlasCandidates(moduleName).ToArray());
                if (legacySource == atlasPath)
                {
                    legacySource = null;
                }

                bool existsAtTarget = File.Exists(ToAbsolutePath(atlasPath));
                bool needsMigrate = !existsAtTarget && !string.IsNullOrEmpty(legacySource);
                bool exists = existsAtTarget || needsMigrate;

                bool needsUpdate = true;
                if (existsAtTarget)
                {
                    needsUpdate = !PackableAlreadyMatches(atlasPath, moduleFolder);
                    ValidateAtlasAgainstRules(atlasPath, moduleFolder);
                    validatedAtlasPaths.Add(atlasPath);
                }
                else if (needsMigrate)
                {
                    needsUpdate = !PackableAlreadyMatches(legacySource, moduleFolder);
                    pendingMigrateSources.Add(legacySource);
                    ValidateAtlasAgainstRules(legacySource, moduleFolder);
                    validatedAtlasPaths.Add(legacySource);
                    AddIssue(
                        RuleSeverity.Warning,
                        $"图集不在图集根规范路径，同步将迁移: {legacySource} → {atlasPath}",
                        legacySource);
                }

                _plans.Add(new AtlasPlan
                {
                    ModuleName = moduleName,
                    ModuleFolder = moduleFolder,
                    AtlasName = atlasName,
                    AtlasPath = atlasPath,
                    LegacySourcePath = needsMigrate ? legacySource : null,
                    SpriteCount = spriteCount,
                    AtlasExists = exists,
                    NeedsPackableUpdate = needsUpdate,
                    NeedsMigrate = needsMigrate
                });

                CollectLargeImages(moduleFolder);
            }

            if (AssetDatabase.IsValidFolder(_atlasRoot))
            {
                string[] atlasGuids = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { _atlasRoot });
                foreach (string guid in atlasGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!IsManagedAtlasPath(path))
                    {
                        continue;
                    }

                    if (expectedAtlasPaths.Contains(path) || pendingMigrateSources.Contains(path))
                    {
                        continue;
                    }

                    _orphanAtlasPaths.Add(path);
                    if (!validatedAtlasPaths.Contains(path))
                    {
                        ValidateAtlasAgainstRules(path, null);
                        validatedAtlasPaths.Add(path);
                        AddIssue(
                            RuleSeverity.Warning,
                            "孤儿图集：源图侧无对应同名模块文件夹，可勾选删除或手动处理",
                            path);
                    }
                }
            }

            ValidatePackableFolderNesting();
            CollectEmptyFolders();
            _plans.Sort((a, b) => string.CompareOrdinal(a.AtlasName, b.AtlasName));
            Repaint();
        }

        private void ExecuteSync()
        {
            ScanPlans();
            bool nothingToDo = _plans.Count == 0
                               && (!_deleteOrphans || _orphanAtlasPaths.Count == 0)
                               && (!_deleteEmptyFolders || _emptyFolderPaths.Count == 0);
            if (nothingToDo)
            {
                EditorUtility.DisplayDialog("同步 Art UI 图集", "没有可同步的模块图集。", "确定");
                return;
            }

            EnsureFolderExists(_atlasRoot);

            int created = 0;
            int updated = 0;
            int migrated = 0;
            int skipped = 0;
            int deleted = 0;
            int deletedEmptyFolders = 0;

            try
            {
                for (int i = 0; i < _plans.Count; i++)
                {
                    AtlasPlan plan = _plans[i];
                    if (!plan.NeedsMigrate || string.IsNullOrEmpty(plan.LegacySourcePath))
                    {
                        continue;
                    }

                    EditorUtility.DisplayProgressBar(
                        "同步 Art UI 图集",
                        "迁移 " + plan.AtlasName,
                        _plans.Count > 0 ? (float)(i + 1) / _plans.Count : 1f);

                    string moveError = AssetDatabase.MoveAsset(plan.LegacySourcePath, plan.AtlasPath);
                    if (!string.IsNullOrEmpty(moveError))
                    {
                        Debug.LogError(
                            $"[ArtUISpriteAtlas] 迁移失败 {plan.LegacySourcePath} -> {plan.AtlasPath}: {moveError}");
                        continue;
                    }

                    migrated++;
                    plan.AtlasExists = true;
                    plan.NeedsMigrate = false;
                    plan.LegacySourcePath = null;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < _plans.Count; i++)
                {
                    AtlasPlan plan = _plans[i];
                    EditorUtility.DisplayProgressBar(
                        "同步 Art UI 图集",
                        plan.AtlasName,
                        _plans.Count > 0 ? (float)(i + 1) / _plans.Count : 1f);

                    if (plan.AtlasExists && !plan.NeedsPackableUpdate)
                    {
                        skipped++;
                        continue;
                    }

                    if (plan.AtlasExists || File.Exists(ToAbsolutePath(plan.AtlasPath)))
                    {
                        UpdateAtlasPackable(plan);
                        updated++;
                    }
                    else
                    {
                        CreateAtlas(plan);
                        created++;
                    }
                }

                if (_deleteOrphans)
                {
                    foreach (string orphanPath in _orphanAtlasPaths)
                    {
                        if (AssetDatabase.DeleteAsset(orphanPath))
                        {
                            deleted++;
                        }
                    }
                }

                if (_deleteEmptyFolders)
                {
                    // 可能因删孤儿/迁移后出现新的空目录，多轮从深到浅清理
                    deletedEmptyFolders = DeleteEmptyFoldersUnderRoots();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            bool mapOk = true;
            if (_exportMap)
            {
                mapOk = ArtUISpriteAtlasMapExporter.ExportMap();
            }

            ScanPlans();

            int errorCount = CountIssues(RuleSeverity.Error);
            int warnCount = CountIssues(RuleSeverity.Warning);

            var sb = new StringBuilder();
            sb.AppendLine(
                $"新建: {created}，迁移到图集根: {migrated}，更新 packable: {updated}，跳过: {skipped}，" +
                $"删除孤儿: {deleted}，删除空文件夹: {deletedEmptyFolders}");
            sb.AppendLine(
                $"同步后规范错误: {errorCount}，规范警告: {warnCount}，根目录散图: {_looseSpriteWarnings.Count}，" +
                $"大图: {_largeImageWarnings.Count}，孤儿: {_orphanAtlasPaths.Count}，空文件夹: {_emptyFolderPaths.Count}");
            sb.AppendLine(_exportMap
                ? (mapOk ? "map 导出成功: " + ArtUISpriteAtlasMapExporter.OutputPath : "map 导出失败")
                : "未导出 map");
            _lastReport = sb.ToString();

            Debug.Log("[ArtUISpriteAtlas] " + _lastReport.Replace('\n', ' '));

            if (errorCount > 0)
            {
                EditorUtility.DisplayDialog(
                    "同步 Art UI 图集",
                    $"同步完成，但仍有 {errorCount} 个规范错误，请查看窗口「规范错误」列表并处理。",
                    "确定");
            }
            else if (warnCount > 0 || _looseSpriteWarnings.Count > 0 || _largeImageWarnings.Count > 0 ||
                     _orphanAtlasPaths.Count > 0 || _emptyFolderPaths.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    "同步 Art UI 图集",
                    "同步完成，存在警告项，建议在窗口中查看后再出包。",
                    "确定");
            }

            Repaint();
        }

        private void CreateAtlas(AtlasPlan plan)
        {
            var atlas = new SpriteAtlas();
            ApplyAtlasSettings(atlas);
            atlas.SetIncludeInBuild(true);
            AssetDatabase.CreateAsset(atlas, plan.AtlasPath);

            Object folderObj = AssetDatabase.LoadAssetAtPath<Object>(plan.ModuleFolder);
            if (folderObj != null)
            {
                atlas.Add(new[] { folderObj });
            }

            EditorUtility.SetDirty(atlas);
        }

        private void UpdateAtlasPackable(AtlasPlan plan)
        {
            SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(plan.AtlasPath);
            if (atlas == null)
            {
                CreateAtlas(plan);
                return;
            }

            Object[] oldPackables = atlas.GetPackables();
            if (oldPackables != null && oldPackables.Length > 0)
            {
                atlas.Remove(oldPackables);
            }

            Object folderObj = AssetDatabase.LoadAssetAtPath<Object>(plan.ModuleFolder);
            if (folderObj != null)
            {
                atlas.Add(new[] { folderObj });
            }

            ApplyAtlasSettings(atlas);
            EditorUtility.SetDirty(atlas);
        }

        private void ApplyAtlasSettings(SpriteAtlas atlas)
        {
            atlas.SetPackingSettings(new SpriteAtlasPackingSettings
            {
                blockOffset = 1,
                enableRotation = false,
                enableTightPacking = false,
                padding = _padding
            });

            atlas.SetTextureSettings(new SpriteAtlasTextureSettings
            {
                readable = false,
                generateMipMaps = false,
                sRGB = true,
                filterMode = FilterMode.Bilinear
            });

            atlas.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "DefaultTexturePlatform",
                overridden = false,
                maxTextureSize = _maxSize,
                format = TextureImporterFormat.Automatic,
                textureCompression = TextureImporterCompression.Compressed
            });

            atlas.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "Android",
                overridden = true,
                maxTextureSize = _maxSize,
                format = TextureImporterFormat.ASTC_6x6,
                textureCompression = TextureImporterCompression.Compressed
            });

            atlas.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "iPhone",
                overridden = true,
                maxTextureSize = _maxSize,
                format = TextureImporterFormat.ASTC_6x6,
                textureCompression = TextureImporterCompression.Compressed
            });
        }

        private void AddIssue(RuleSeverity severity, string message, string assetPath)
        {
            _ruleIssues.Add(new RuleIssue
            {
                Severity = severity,
                Message = message,
                AssetPath = assetPath
            });
        }

        private void ValidateAtlasAgainstRules(string atlasPath, string expectedModuleFolder)
        {
            SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            if (atlas == null)
            {
                AddIssue(RuleSeverity.Error, "无法加载图集", atlasPath);
                return;
            }

            string atlasName = Path.GetFileNameWithoutExtension(atlasPath);
            Object[] packables = atlas.GetPackables();
            if (packables == null || packables.Length == 0)
            {
                AddIssue(RuleSeverity.Error, "图集未引用任何对象（规范要求引用一个源图根下文件夹）", atlasPath);
                return;
            }

            if (packables.Length > 1)
            {
                AddIssue(
                    RuleSeverity.Error,
                    $"图集引用了 {packables.Length} 个对象（规范要求只引用一个文件夹）",
                    atlasPath);
            }

            string packablePath = AssetDatabase.GetAssetPath(packables[0]);
            if (string.IsNullOrEmpty(packablePath) || !AssetDatabase.IsValidFolder(packablePath))
            {
                AddIssue(RuleSeverity.Error, "第一个引用不是文件夹", atlasPath);
                return;
            }

            string folderName = Path.GetFileName(packablePath);
            if (!string.Equals(atlasName, folderName, System.StringComparison.Ordinal))
            {
                AddIssue(
                    RuleSeverity.Error,
                    $"图集名「{atlasName}」与引用文件夹名「{folderName}」不一致",
                    atlasPath);
            }

            string normalizedPackable = packablePath.Replace('\\', '/');
            if (!normalizedPackable.StartsWith(_spriteRoot + "/", System.StringComparison.Ordinal) &&
                normalizedPackable != _spriteRoot)
            {
                AddIssue(
                    RuleSeverity.Error,
                    $"引用文件夹不在源图根目录下: {packablePath}",
                    atlasPath);
            }

            string atlasDir = Path.GetDirectoryName(atlasPath);
            if (!string.IsNullOrEmpty(atlasDir))
            {
                atlasDir = atlasDir.Replace('\\', '/');
                if (atlasDir != _atlasRoot)
                {
                    AddIssue(
                        RuleSeverity.Warning,
                        "图集不在图集根目录（新规范要求平铺在图集根下，与模块文件夹同名）",
                        atlasPath);
                }
            }

            if (normalizedPackable.StartsWith(_spriteRoot + "/", System.StringComparison.Ordinal))
            {
                string relative = normalizedPackable.Substring(_spriteRoot.Length + 1);
                if (relative.Contains("/"))
                {
                    AddIssue(
                        RuleSeverity.Error,
                        $"引用的是源图根下更深层目录（规范要求引用一层模块文件夹）: {packablePath}",
                        atlasPath);
                }
            }

            if (!string.IsNullOrEmpty(expectedModuleFolder) &&
                !string.Equals(
                    normalizedPackable,
                    expectedModuleFolder.Replace('\\', '/'),
                    System.StringComparison.Ordinal))
            {
                AddIssue(
                    RuleSeverity.Error,
                    $"引用文件夹不是期望模块路径（期望 {expectedModuleFolder}，实际 {packablePath}）",
                    atlasPath);
            }
        }

        private void ValidatePackableFolderNesting()
        {
            if (!AssetDatabase.IsValidFolder(_atlasRoot))
            {
                return;
            }

            var atlasFolders = new List<KeyValuePair<string, string>>();
            string[] atlasGuids = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { _atlasRoot });
            foreach (string guid in atlasGuids)
            {
                string atlasPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsManagedAtlasPath(atlasPath))
                {
                    continue;
                }

                SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                if (atlas == null)
                {
                    continue;
                }

                Object[] packables = atlas.GetPackables();
                if (packables == null)
                {
                    continue;
                }

                for (int i = 0; i < packables.Length; i++)
                {
                    if (packables[i] == null)
                    {
                        continue;
                    }

                    string packablePath = AssetDatabase.GetAssetPath(packables[i]);
                    if (string.IsNullOrEmpty(packablePath) || !AssetDatabase.IsValidFolder(packablePath))
                    {
                        continue;
                    }

                    atlasFolders.Add(new KeyValuePair<string, string>(
                        atlasPath,
                        packablePath.Replace('\\', '/')));
                }
            }

            for (int i = 0; i < _plans.Count; i++)
            {
                AtlasPlan plan = _plans[i];
                string folder = plan.ModuleFolder.Replace('\\', '/');
                bool alreadyListed = false;
                for (int j = 0; j < atlasFolders.Count; j++)
                {
                    if (atlasFolders[j].Key == plan.AtlasPath && atlasFolders[j].Value == folder)
                    {
                        alreadyListed = true;
                        break;
                    }
                }

                if (!alreadyListed)
                {
                    atlasFolders.Add(new KeyValuePair<string, string>(plan.AtlasPath, folder));
                }
            }

            for (int i = 0; i < atlasFolders.Count; i++)
            {
                string atlasA = atlasFolders[i].Key;
                string folderA = atlasFolders[i].Value;

                for (int j = i + 1; j < atlasFolders.Count; j++)
                {
                    string atlasB = atlasFolders[j].Key;
                    string folderB = atlasFolders[j].Value;
                    if (atlasA == atlasB)
                    {
                        continue;
                    }

                    if (string.Equals(folderA, folderB, System.StringComparison.Ordinal))
                    {
                        AddIssue(
                            RuleSeverity.Error,
                            $"同一文件夹被多个图集引用: {folderA} ← [{Path.GetFileNameWithoutExtension(atlasA)}] 与 [{Path.GetFileNameWithoutExtension(atlasB)}]",
                            atlasA);
                        continue;
                    }

                    if (IsFolderAncestor(folderA, folderB))
                    {
                        AddIssue(
                            RuleSeverity.Error,
                            $"父子目录冲突：[{Path.GetFileNameWithoutExtension(atlasA)}] 引用父目录 {folderA}，" +
                            $"[{Path.GetFileNameWithoutExtension(atlasB)}] 引用其子目录 {folderB}",
                            atlasB);
                    }
                    else if (IsFolderAncestor(folderB, folderA))
                    {
                        AddIssue(
                            RuleSeverity.Error,
                            $"父子目录冲突：[{Path.GetFileNameWithoutExtension(atlasB)}] 引用父目录 {folderB}，" +
                            $"[{Path.GetFileNameWithoutExtension(atlasA)}] 引用其子目录 {folderA}",
                            atlasA);
                    }
                }
            }
        }

        private static bool IsFolderAncestor(string ancestor, string descendant)
        {
            if (string.IsNullOrEmpty(ancestor) || string.IsNullOrEmpty(descendant))
            {
                return false;
            }

            ancestor = ancestor.Replace('\\', '/').TrimEnd('/');
            descendant = descendant.Replace('\\', '/').TrimEnd('/');
            if (ancestor.Length >= descendant.Length)
            {
                return false;
            }

            return descendant.StartsWith(ancestor + "/", System.StringComparison.Ordinal);
        }

        private static bool PackableAlreadyMatches(string atlasPath, string moduleFolder)
        {
            SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            if (atlas == null)
            {
                return false;
            }

            Object[] packables = atlas.GetPackables();
            if (packables == null || packables.Length != 1)
            {
                return false;
            }

            return AssetDatabase.GetAssetPath(packables[0]) == moduleFolder;
        }

        private void CollectLooseSpritesAtSpriteRoot()
        {
            string abs = ToAbsolutePath(_spriteRoot);
            if (!Directory.Exists(abs))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(abs))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".tga")
                {
                    continue;
                }

                _looseSpriteWarnings.Add(_spriteRoot + "/" + Path.GetFileName(file));
            }
        }

        /// <summary>收集源图根与图集根下所有空文件夹（不含根自身），深路径优先排列</summary>
        private void CollectEmptyFolders()
        {
            _emptyFolderPaths = new List<string>();
            CollectEmptyFoldersRecursive(_spriteRoot, _emptyFolderPaths);
            CollectEmptyFoldersRecursive(_atlasRoot, _emptyFolderPaths);
            _emptyFolderPaths.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        private static void CollectEmptyFoldersRecursive(string root, List<string> result)
        {
            if (string.IsNullOrEmpty(root) || !AssetDatabase.IsValidFolder(root))
            {
                return;
            }

            foreach (string childName in ListSubFolderNames(root))
            {
                string childPath = root + "/" + childName;
                CollectEmptyFoldersRecursive(childPath, result);
                if (IsFolderEmpty(childPath))
                {
                    result.Add(childPath);
                }
            }
        }

        /// <summary>
        /// 多轮删除空文件夹：先删深路径，子目录删完后父目录可能变空。
        /// 不删除源图根 / 图集根自身。
        /// </summary>
        private int DeleteEmptyFoldersUnderRoots()
        {
            int deleted = 0;
            const int maxRounds = 32;
            for (int round = 0; round < maxRounds; round++)
            {
                var empties = new List<string>();
                CollectEmptyFoldersRecursive(_spriteRoot, empties);
                CollectEmptyFoldersRecursive(_atlasRoot, empties);
                if (empties.Count == 0)
                {
                    break;
                }

                empties.Sort((a, b) => b.Length.CompareTo(a.Length));
                int roundDeleted = 0;
                for (int i = 0; i < empties.Count; i++)
                {
                    string path = empties[i];
                    if (path == _spriteRoot || path == _atlasRoot)
                    {
                        continue;
                    }

                    if (!IsFolderEmpty(path))
                    {
                        continue;
                    }

                    if (AssetDatabase.DeleteAsset(path))
                    {
                        deleted++;
                        roundDeleted++;
                    }
                }

                if (roundDeleted == 0)
                {
                    break;
                }
            }

            return deleted;
        }

        /// <summary>磁盘意义上为空（忽略 .meta / .DS_Store 等杂文件）</summary>
        private static bool IsFolderEmpty(string assetFolder)
        {
            string abs = ToAbsolutePath(assetFolder);
            if (!Directory.Exists(abs))
            {
                return false;
            }

            foreach (string dir in Directory.GetDirectories(abs))
            {
                return false;
            }

            foreach (string file in Directory.GetFiles(abs))
            {
                string name = Path.GetFileName(file);
                if (name == ".DS_Store" || name == "Thumbs.db" || name.EndsWith(".meta"))
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private void WarnLegacyTwoLevelLayout()
        {
            foreach (string systemName in ListSubFolderNames(_spriteRoot))
            {
                if (!LooksLikeOldSystemFolderName(systemName))
                {
                    continue;
                }

                string systemPath = _spriteRoot + "/" + systemName;
                foreach (string featureName in ListSubFolderNames(systemPath))
                {
                    string featurePath = systemPath + "/" + featureName;
                    if (CountSprites(featurePath) <= 0)
                    {
                        continue;
                    }

                    string flatName = systemName + "_" + featureName;
                    string flatPath = _spriteRoot + "/" + flatName;
                    if (AssetDatabase.IsValidFolder(flatPath))
                    {
                        continue;
                    }

                    AddIssue(
                        RuleSeverity.Warning,
                        $"检测到旧二层目录 {systemName}/{featureName}，新规范请改为 {_spriteRoot}/{flatName}/",
                        featurePath);
                }
            }
        }

        private static bool LooksLikeOldSystemFolderName(string folderName)
        {
            if (string.IsNullOrEmpty(folderName) || !folderName.StartsWith("UI_") || folderName.Length <= 3)
            {
                return false;
            }

            string rest = folderName.Substring(3);
            return rest.IndexOf('_') < 0;
        }

        private void CollectLargeImages(string folderPath)
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!TryGetImageSize(path, out int w, out int h))
                {
                    continue;
                }

                if (w > LargeImageWarnThreshold || h > LargeImageWarnThreshold)
                {
                    _largeImageWarnings.Add($"{w}x{h}  {path}");
                }
            }
        }

        private static bool TryGetImageSize(string assetPath, out int width, out int height)
        {
            width = 0;
            height = 0;

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
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

            if (_getWidthAndHeightMethod != null)
            {
                object[] args = { 0, 0 };
                _getWidthAndHeightMethod.Invoke(importer, args);
                width = (int)args[0];
                height = (int)args[1];
                return width > 0 && height > 0;
            }

            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex == null)
            {
                return false;
            }

            width = tex.width;
            height = tex.height;
            return true;
        }

        private List<string> CollectLegacyAtlasCandidates(string moduleName)
        {
            var list = new List<string>();
            list.Add(_atlasRoot + "/" + moduleName + ".spriteatlas");

            string[] parts = moduleName.Split('_');
            for (int take = 1; take < parts.Length; take++)
            {
                var leftParts = new string[take];
                for (int i = 0; i < take; i++)
                {
                    leftParts[i] = parts[i];
                }

                var rightParts = new string[parts.Length - take];
                for (int i = take; i < parts.Length; i++)
                {
                    rightParts[i - take] = parts[i];
                }

                string left = string.Join("_", leftParts);
                string right = string.Join("_", rightParts);
                if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                {
                    continue;
                }

                list.Add(_atlasRoot + "/" + left + "/" + right + ".spriteatlas");
            }

            if (moduleName.StartsWith("UI_"))
            {
                list.Add(_atlasRoot + "/UI_SA_" + moduleName.Substring(3) + ".spriteatlas");
                list.Add(_atlasRoot + "/SA_" + moduleName + ".spriteatlas");
            }

            return list;
        }

        private bool IsManagedAtlasPath(string atlasAssetPath)
        {
            string dir = Path.GetDirectoryName(atlasAssetPath);
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }

            dir = dir.Replace('\\', '/');
            return dir == _atlasRoot || dir.StartsWith(_atlasRoot + "/");
        }

        private static string FindFirstExistingAtlasPath(params string[] candidates)
        {
            if (candidates == null)
            {
                return null;
            }

            foreach (string path in candidates)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(ToAbsolutePath(path)))
                {
                    return path;
                }
            }

            return null;
        }

        private static int CountSprites(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                return 0;
            }

            return AssetDatabase.FindAssets("t:Sprite", new[] { folder }).Length;
        }

        private static List<string> ListSubFolderNames(string assetFolder)
        {
            var result = new List<string>();
            string abs = ToAbsolutePath(assetFolder);
            if (!Directory.Exists(abs))
            {
                return result;
            }

            foreach (string dir in Directory.GetDirectories(abs))
            {
                result.Add(Path.GetFileName(dir));
            }

            result.Sort();
            return result;
        }

        private static void EnsureFolderExists(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder))
            {
                return;
            }

            string[] parts = assetFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            return path.Replace('\\', '/').Trim().TrimEnd('/');
        }

        private static string ToAbsolutePath(string assetPath)
        {
            if (assetPath.StartsWith("Assets/") || assetPath == "Assets")
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            }

            return Path.GetFullPath(assetPath);
        }

        private static int IntPopup(string label, int value, int[] options)
        {
            var labels = new string[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                labels[i] = options[i].ToString();
            }

            int index = System.Array.IndexOf(options, value);
            if (index < 0)
            {
                index = 0;
            }

            index = EditorGUILayout.Popup(label, index, labels);
            return options[index];
        }
    }
}
