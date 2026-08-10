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
    /// Art UI 图集一键同步工具。
    /// 规则：Sprite/UI_&lt;System&gt;/&lt;Feature&gt;/ 为图集根；
    /// 产出 Atlas/UI_&lt;System&gt;/UI_SA_System_Feature.spriteatlas
    /// （命名第二字段为类型 SA，与资源规范一致；至少按系统分一层目录）；
    /// 不挪源图；手动触发；同步后可选导出 sprite_atlas_map。
    /// </summary>
    public class ArtUISpriteAtlasSyncWindow : EditorWindow
    {
        private const string SpriteRoot = "Assets/GameAssets/Art/UI/SpriteAtlas/Sprite";
        private const string AtlasRoot = "Assets/GameAssets/Art/UI/SpriteAtlas/Atlas";

        private const string PrefKeyPadding = "ArtUISpriteAtlasSync.Padding";
        private const string PrefKeyMaxSize = "ArtUISpriteAtlasSync.MaxSize";
        private const string PrefKeyExportMap = "ArtUISpriteAtlasSync.ExportMap";
        private const string PrefKeyDeleteOrphans = "ArtUISpriteAtlasSync.DeleteOrphans";

        /// <summary>单图任一边超过该像素则列入大图警告（第一期只警告，仍入集）</summary>
        private const int LargeImageWarnThreshold = 1024;

        private static readonly int[] MaxSizeOptions = { 1024, 2048, 4096 };
        private static readonly int[] PaddingOptions = { 2, 4, 8 };

        private int _maxSize = 2048;
        private int _padding = 4;
        private bool _exportMap = true;
        private bool _deleteOrphans;

        private Vector2 _scroll;
        private string _lastReport = string.Empty;

        private List<FeatureAtlasPlan> _plans = new List<FeatureAtlasPlan>();
        private List<string> _looseSpriteWarnings = new List<string>();
        private List<string> _largeImageWarnings = new List<string>();
        private List<string> _orphanAtlasPaths = new List<string>();

        /// <summary>TextureImporter.GetWidthAndHeight 反射缓存</summary>
        private static MethodInfo _getWidthAndHeightMethod;

        /// <summary>单个二层 Feature 对应的图集计划</summary>
        private class FeatureAtlasPlan
        {
            public string SystemName;
            public string FeatureName;
            public string FeatureFolder;
            public string AtlasName;
            /// <summary>目标路径：Atlas/UI_System/UI_SA_System_Feature.spriteatlas</summary>
            public string AtlasPath;
            /// <summary>旧路径（扁平或旧命名 SA_UI_*），同步时 Move 到目标路径</summary>
            public string LegacySourcePath;
            public int SpriteCount;
            public bool AtlasExists;
            public bool NeedsPackableUpdate;
            /// <summary>图集不在目标路径，需迁移（改名/改目录）</summary>
            public bool NeedsMigrate;
        }

        public static void Open()
        {
            var window = GetWindow<ArtUISpriteAtlasSyncWindow>("同步 Art UI 图集");
            window.minSize = new Vector2(520f, 480f);
            window.Show();
        }

        private void OnEnable()
        {
            _maxSize = EditorPrefs.GetInt(PrefKeyMaxSize, 2048);
            _padding = EditorPrefs.GetInt(PrefKeyPadding, 4);
            _exportMap = EditorPrefs.GetBool(PrefKeyExportMap, true);
            _deleteOrphans = EditorPrefs.GetBool(PrefKeyDeleteOrphans, false);
        }

        private void OnDisable()
        {
            EditorPrefs.SetInt(PrefKeyMaxSize, _maxSize);
            EditorPrefs.SetInt(PrefKeyPadding, _padding);
            EditorPrefs.SetBool(PrefKeyExportMap, _exportMap);
            EditorPrefs.SetBool(PrefKeyDeleteOrphans, _deleteOrphans);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Art UI 图集同步", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "图集根 = Sprite/UI_<System>/<Feature>/\n" +
                "产出 = Atlas/UI_<System>/UI_SA_<System>_<Feature>.spriteatlas\n" +
                "（第二字段 SA = 图集类型；不移动源图；仅手动执行）",
                MessageType.Info);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("路径", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Sprite", SpriteRoot);
            EditorGUILayout.LabelField("Atlas", AtlasRoot);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("设置", EditorStyles.boldLabel);
            _maxSize = IntPopup("单页最大尺寸", _maxSize, MaxSizeOptions);
            _padding = IntPopup("Padding", _padding, PaddingOptions);
            _exportMap = EditorGUILayout.ToggleLeft("同步后导出 sprite_atlas_map", _exportMap);
            _deleteOrphans = EditorGUILayout.ToggleLeft("删除孤儿图集（目录已不存在）", _deleteOrphans);

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("预览扫描", GUILayout.Height(28f)))
                {
                    ScanPlans();
                }

                using (new EditorGUI.DisabledScope(_plans.Count == 0 && _orphanAtlasPaths.Count == 0))
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

        /// <summary>绘制扫描摘要：计划 / 警告 / 孤儿</summary>
        private void DrawScanSummary()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("扫描结果", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Feature 图集计划: {_plans.Count}，孤儿图集: {_orphanAtlasPaths.Count}，" +
                $"一层散图警告: {_looseSpriteWarnings.Count}，大图警告: {_largeImageWarnings.Count}");

            if (_plans.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("将创建/更新", EditorStyles.miniBoldLabel);
                int show = Mathf.Min(_plans.Count, 40);
                for (int i = 0; i < show; i++)
                {
                    FeatureAtlasPlan p = _plans[i];
                    string state;
                    if (p.NeedsMigrate)
                    {
                        state = "迁入目标路径";
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

                    // 展示相对 Atlas 根的分类路径，便于确认文件夹分层
                    string relative = p.AtlasPath.StartsWith(AtlasRoot + "/")
                        ? p.AtlasPath.Substring(AtlasRoot.Length + 1)
                        : p.AtlasPath;
                    EditorGUILayout.LabelField($"  [{state}] {relative}  ({p.SpriteCount} sprites)");
                }

                if (_plans.Count > show)
                {
                    EditorGUILayout.LabelField($"  ... 另有 {_plans.Count - show} 项");
                }
            }

            DrawWarningList("一层散图（需放入二层 Feature 目录）", _looseSpriteWarnings);
            DrawWarningList($"大图警告（任一边 > {LargeImageWarnThreshold}，仍会入集）", _largeImageWarnings);
            DrawWarningList("孤儿图集", _orphanAtlasPaths);
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

        /// <summary>扫描 Sprite 目录，生成同步计划与警告列表</summary>
        private void ScanPlans()
        {
            _plans = new List<FeatureAtlasPlan>();
            _looseSpriteWarnings = new List<string>();
            _largeImageWarnings = new List<string>();
            _orphanAtlasPaths = new List<string>();
            _lastReport = string.Empty;

            if (!AssetDatabase.IsValidFolder(SpriteRoot))
            {
                EditorUtility.DisplayDialog("同步 Art UI 图集", $"Sprite 根目录不存在:\n{SpriteRoot}", "确定");
                return;
            }

            EnsureFolderExists(AtlasRoot);

            // 期望的完整图集路径（含系统子目录），用于孤儿判定
            var expectedAtlasPaths = new HashSet<string>();
            // 即将迁入目标的旧路径，扫描时不当作孤儿展示
            var pendingMigrateSources = new HashSet<string>();

            // 用文件系统列举一层（FindAssets 对纯文件夹不可靠）
            foreach (string systemName in ListSubFolderNames(SpriteRoot))
            {
                if (!systemName.StartsWith("UI_"))
                {
                    continue;
                }

                string systemPath = SpriteRoot + "/" + systemName;
                CollectLooseSprites(systemPath, systemName);

                foreach (string featureName in ListSubFolderNames(systemPath))
                {
                    string featurePath = systemPath + "/" + featureName;
                    int spriteCount = CountSprites(featurePath);
                    if (spriteCount <= 0)
                    {
                        continue;
                    }

                    string atlasName = BuildAtlasName(systemName, featureName);
                    string legacyAtlasName = BuildLegacyAtlasName(systemName, featureName);
                    // 按系统分一层：Atlas/UI_System/UI_SA_System_Feature.spriteatlas
                    string atlasPath = AtlasRoot + "/" + systemName + "/" + atlasName + ".spriteatlas";
                    expectedAtlasPaths.Add(atlasPath);

                    // 兼容旧路径：系统目录旧名 / 扁平旧名 SA_UI_* / 扁平新名
                    string legacySource = FindFirstExistingAtlasPath(
                        AtlasRoot + "/" + systemName + "/" + legacyAtlasName + ".spriteatlas",
                        AtlasRoot + "/" + legacyAtlasName + ".spriteatlas",
                        AtlasRoot + "/" + atlasName + ".spriteatlas");

                    bool existsAtTarget = File.Exists(ToAbsolutePath(atlasPath));
                    bool needsMigrate = !existsAtTarget && !string.IsNullOrEmpty(legacySource);
                    bool exists = existsAtTarget || needsMigrate;

                    bool needsUpdate = true;
                    if (existsAtTarget)
                    {
                        needsUpdate = !PackableAlreadyMatches(atlasPath, featurePath);
                    }
                    else if (needsMigrate)
                    {
                        needsUpdate = !PackableAlreadyMatches(legacySource, featurePath);
                        pendingMigrateSources.Add(legacySource);
                    }

                    _plans.Add(new FeatureAtlasPlan
                    {
                        SystemName = systemName,
                        FeatureName = featureName,
                        FeatureFolder = featurePath,
                        AtlasName = atlasName,
                        AtlasPath = atlasPath,
                        LegacySourcePath = needsMigrate ? legacySource : null,
                        SpriteCount = spriteCount,
                        AtlasExists = exists,
                        NeedsPackableUpdate = needsUpdate,
                        NeedsMigrate = needsMigrate
                    });

                    CollectLargeImages(featurePath);
                }
            }

            // 孤儿：本工具相关命名（UI_SA_* 或旧 SA_UI_*）且路径不在期望集合
            if (AssetDatabase.IsValidFolder(AtlasRoot))
            {
                string[] atlasGuids = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { AtlasRoot });
                foreach (string guid in atlasGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!IsManagedAtlasName(name))
                    {
                        continue;
                    }

                    if (expectedAtlasPaths.Contains(path) || pendingMigrateSources.Contains(path))
                    {
                        continue;
                    }

                    _orphanAtlasPaths.Add(path);
                }
            }

            _plans.Sort((a, b) => string.CompareOrdinal(a.AtlasName, b.AtlasName));
            Repaint();
        }

        /// <summary>按计划创建/更新图集，可选清理孤儿并导出 map</summary>
        private void ExecuteSync()
        {
            // 执行前再扫一次，避免预览过期
            ScanPlans();
            if (_plans.Count == 0 && (!_deleteOrphans || _orphanAtlasPaths.Count == 0))
            {
                EditorUtility.DisplayDialog("同步 Art UI 图集", "没有可同步的 Feature 图集。", "确定");
                return;
            }

            EnsureFolderExists(AtlasRoot);

            int created = 0;
            int updated = 0;
            int migrated = 0;
            int skipped = 0;
            int deleted = 0;

            // 先迁目录/改名（MoveAsset 不宜包在 StartAssetEditing 内）
            try
            {
                for (int i = 0; i < _plans.Count; i++)
                {
                    FeatureAtlasPlan plan = _plans[i];
                    if (!plan.NeedsMigrate || string.IsNullOrEmpty(plan.LegacySourcePath))
                    {
                        continue;
                    }

                    EditorUtility.DisplayProgressBar(
                        "同步 Art UI 图集",
                        "迁移 " + plan.AtlasName,
                        _plans.Count > 0 ? (float)(i + 1) / _plans.Count : 1f);

                    EnsureFolderExists(AtlasRoot + "/" + plan.SystemName);
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
                    FeatureAtlasPlan plan = _plans[i];
                    EditorUtility.DisplayProgressBar(
                        "同步 Art UI 图集",
                        plan.AtlasName,
                        _plans.Count > 0 ? (float)(i + 1) / _plans.Count : 1f);

                    EnsureFolderExists(AtlasRoot + "/" + plan.SystemName);

                    if (plan.AtlasExists && !plan.NeedsPackableUpdate)
                    {
                        // packable 已正确：不改设置，避免无意义 SVN diff
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
                        // 已成功迁走的扁平路径不应再出现在孤儿列表；若仍在则删除
                        if (AssetDatabase.DeleteAsset(orphanPath))
                        {
                            deleted++;
                        }
                    }
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

            // 同步后刷新状态
            ScanPlans();

            var sb = new StringBuilder();
            sb.AppendLine(
                $"新建: {created}，迁移分类目录: {migrated}，更新 packable: {updated}，跳过: {skipped}，删除孤儿: {deleted}");
            sb.AppendLine($"一层散图警告: {_looseSpriteWarnings.Count}，大图警告: {_largeImageWarnings.Count}");
            sb.AppendLine(_exportMap
                ? (mapOk ? "map 导出成功: " + ArtUISpriteAtlasMapExporter.OutputPath : "map 导出失败")
                : "未导出 map");
            _lastReport = sb.ToString();

            Debug.Log("[ArtUISpriteAtlas] " + _lastReport.Replace('\n', ' '));
            Repaint();
        }

        /// <summary>新建图集并引用 Feature 文件夹</summary>
        private void CreateAtlas(FeatureAtlasPlan plan)
        {
            var atlas = new SpriteAtlas();
            ApplyAtlasSettings(atlas);
            atlas.SetIncludeInBuild(true);
            AssetDatabase.CreateAsset(atlas, plan.AtlasPath);

            Object folderObj = AssetDatabase.LoadAssetAtPath<Object>(plan.FeatureFolder);
            if (folderObj != null)
            {
                atlas.Add(new[] { folderObj });
            }

            EditorUtility.SetDirty(atlas);
        }

        /// <summary>已有图集：替换 packables 为当前 Feature 文件夹，并刷新设置</summary>
        private void UpdateAtlasPackable(FeatureAtlasPlan plan)
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

            Object folderObj = AssetDatabase.LoadAssetAtPath<Object>(plan.FeatureFolder);
            if (folderObj != null)
            {
                atlas.Add(new[] { folderObj });
            }

            ApplyAtlasSettings(atlas);
            EditorUtility.SetDirty(atlas);
        }

        /// <summary>写入统一的 packing / texture / 平台尺寸</summary>
        private void ApplyAtlasSettings(SpriteAtlas atlas)
        {
            var packing = new SpriteAtlasPackingSettings
            {
                blockOffset = 1,
                enableRotation = false,
                enableTightPacking = false,
                padding = _padding
            };
            atlas.SetPackingSettings(packing);

            var textureSettings = new SpriteAtlasTextureSettings
            {
                readable = false,
                generateMipMaps = false,
                sRGB = true,
                filterMode = FilterMode.Bilinear
            };
            atlas.SetTextureSettings(textureSettings);

            // Default
            atlas.SetPlatformSettings(new TextureImporterPlatformSettings
            {
                name = "DefaultTexturePlatform",
                overridden = false,
                maxTextureSize = _maxSize,
                format = TextureImporterFormat.Automatic,
                textureCompression = TextureImporterCompression.Compressed
            });

            // 移动端与正式图集习惯对齐：限制单页尺寸
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

        /// <summary>判断图集是否已唯一引用目标 Feature 文件夹</summary>
        private static bool PackableAlreadyMatches(string atlasPath, string featureFolder)
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

            string path = AssetDatabase.GetAssetPath(packables[0]);
            return path == featureFolder;
        }

        /// <summary>系统目录下一层散落的精灵（未进 Feature 子目录）</summary>
        private void CollectLooseSprites(string systemPath, string systemName)
        {
            if (!Directory.Exists(ToAbsolutePath(systemPath)))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(ToAbsolutePath(systemPath)))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".tga")
                {
                    continue;
                }

                string assetPath = systemPath + "/" + Path.GetFileName(file);
                _looseSpriteWarnings.Add($"{systemName}: {assetPath}");
            }
        }

        /// <summary>收集 Feature 下超过阈值的大图路径</summary>
        private void CollectLargeImages(string featurePath)
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { featurePath });
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

        /// <summary>读取源图像素尺寸（反射 GetWidthAndHeight，不受 maxTextureSize 影响）</summary>
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

        /// <summary>
        /// 图集名：UI_SA_&lt;System&gt;_&lt;Feature&gt;（第二字段 SA 表示图集类型）。
        /// systemName 形如 UI_Alliance → 得到 UI_SA_Alliance_War。
        /// </summary>
        private static string BuildAtlasName(string systemName, string featureName)
        {
            string systemToken = StripUiPrefix(systemName);
            return "UI_SA_" + systemToken + "_" + featureName;
        }

        /// <summary>旧命名：SA_UI_Alliance_War（SA 在首位，已废弃）</summary>
        private static string BuildLegacyAtlasName(string systemName, string featureName)
        {
            return "SA_" + systemName + "_" + featureName;
        }

        /// <summary>去掉目录名上的 UI_ 前缀</summary>
        private static string StripUiPrefix(string name)
        {
            if (!string.IsNullOrEmpty(name) &&
                name.Length > 3 &&
                (name[0] == 'U' || name[0] == 'u') &&
                (name[1] == 'I' || name[1] == 'i') &&
                name[2] == '_')
            {
                return name.Substring(3);
            }

            return name;
        }

        /// <summary>是否为本工具管理的图集命名（含旧 SA_UI_*）</summary>
        private static bool IsManagedAtlasName(string atlasName)
        {
            return atlasName.StartsWith("UI_SA_") || atlasName.StartsWith("SA_UI_");
        }

        /// <summary>返回第一个真实存在的 Asset 路径；都没有则 null</summary>
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

        /// <summary>列出 Asset 相对路径下一层子文件夹名</summary>
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

        /// <summary>确保 Assets 下文件夹存在（逐级创建）</summary>
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
