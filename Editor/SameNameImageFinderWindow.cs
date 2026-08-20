using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using vietlabs.fr2;
using Object = UnityEngine.Object;

namespace UIR.EditorTools
{
    /// <summary>
    /// 查找指定目录下的同名图片，预览对比后保留一张，
    /// 通过 Find Reference 2 把被删图片的引用改到保留图上。
    /// </summary>
    public class SameNameImageFinderWindow : EditorWindow
    {
        private const string PrefsSearchPath = "SameNameImageFinder.SearchPath";
        private const float PreviewSize = 160f;
        private const float CardWidth = 280f;
        private const float GroupHintWidth = 58f;

        private static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".exr"
        };

        [SerializeField] private DefaultAsset mSearchFolder;
        [SerializeField] private string mSearchPath = "Assets";
        [SerializeField] private string mFilterText = "";

        private readonly List<SameNameGroup> mGroups = new List<SameNameGroup>();
        private readonly List<AtlasFolderPack> mAtlasFolders = new List<AtlasFolderPack>();
        private Vector2 mGroupScroll;
        private Vector2 mPreviewScroll;
        private int mSelectedGroupIndex = -1;
        private string mStatus = "选择目录后点击查找。";
        private bool mMergeRunning;
        private bool mWasFr2Ready;

        public static void Open()
        {
            SameNameImageFinderWindow window = GetWindow<SameNameImageFinderWindow>("同名图片合并");
            window.minSize = new Vector2(960f, 560f);
            window.Show();
        }

        private void OnEnable()
        {
            mSearchPath = EditorPrefs.GetString(PrefsSearchPath, "Assets");
            if (mSearchFolder == null && AssetDatabase.IsValidFolder(mSearchPath))
            {
                mSearchFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(mSearchPath);
            }
        }

        private void Update()
        {
            // 缩略图异步生成时刷新，保证并排预览尽快出来
            if (AssetPreview.IsLoadingAssetPreviews())
            {
                Repaint();
            }
        }

        private void OnGUI()
        {
            bool fr2Ready = Fr2CacheUtil.IsReady();
            if (fr2Ready && !mWasFr2Ready)
            {
                PrefetchAllGroupHints();
                if (mSelectedGroupIndex >= 0 && mSelectedGroupIndex < mGroups.Count)
                {
                    SelectGroup(mSelectedGroupIndex);
                }
            }

            mWasFr2Ready = fr2Ready;
            DrawToolbar();
            DrawFr2Status();
            EditorGUILayout.LabelField(mStatus, EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            DrawGroupList();
            DrawGroupPreview();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            mSearchFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                "扫描目录", mSearchFolder, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                ApplyFolderAsset();
            }

            if (GUILayout.Button("浏览", GUILayout.Width(60f)))
            {
                PickFolder();
            }

            using (new EditorGUI.DisabledScope(mMergeRunning))
            {
                if (GUILayout.Button("查找同名图片", GUILayout.Width(120f), GUILayout.Height(20f)))
                {
                    ScanSameNameImages();
                }
            }

            EditorGUILayout.EndHorizontal();
            mSearchPath = EditorGUILayout.TextField("路径", mSearchPath);
            mFilterText = EditorGUILayout.TextField("过滤组名", mFilterText);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("全选可见", GUILayout.Width(80f)))
            {
                SetVisibleGroupsChecked(true);
            }

            if (GUILayout.Button("取消全选", GUILayout.Width(80f)))
            {
                SetVisibleGroupsChecked(false);
            }

            int checkedCount = CountCheckedGroups();
            using (new EditorGUI.DisabledScope(mMergeRunning || checkedCount == 0))
            {
                if (GUILayout.Button($"批量合并已勾选（{checkedCount}）", GUILayout.Height(22f)))
                {
                    TryBatchMerge();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawFr2Status()
        {
            if (Fr2CacheUtil.IsReady())
            {
                EditorGUILayout.HelpBox(
                    "Find Reference 2 缓存已就绪。左侧勾选可批量合并（只刷新一次）；点组名预览，点「设为保留」后再合并。材质球等特殊引用会单独提示。",
                    UnityEditor.MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox(
                "Find Reference 2 缓存未就绪。可以先查找同名图，但查看引用和合并前请先扫描 FR2 缓存。",
                UnityEditor.MessageType.Warning);
            if (GUILayout.Button("打开 Find Reference 2", GUILayout.Width(160f), GUILayout.Height(38f)))
            {
                EditorApplication.ExecuteMenuItem("Window/Find Reference 2");
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawGroupList()
        {
            const float listWidth = 320f;
            EditorGUILayout.BeginVertical("box", GUILayout.Width(listWidth));
            EditorGUILayout.LabelField($"同名组（{GetVisibleGroups().Count()}）", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("勾选后可批量合并；点组名预览，点图片设为保留。", EditorStyles.wordWrappedMiniLabel);

            // 只保留竖向滚动，行宽跟列表走，避免底部横滑条。
            mGroupScroll = EditorGUILayout.BeginScrollView(
                mGroupScroll,
                GUIStyle.none,
                GUI.skin.verticalScrollbar);
            int visibleIndex = 0;
            for (int i = 0; i < mGroups.Count; i++)
            {
                SameNameGroup group = mGroups[i];
                if (!IsGroupVisible(group))
                {
                    continue;
                }

                EditorGUILayout.BeginHorizontal();
                bool checkedNow = EditorGUILayout.Toggle(group.Checked, GUILayout.Width(18f));
                if (checkedNow != group.Checked)
                {
                    group.Checked = checkedNow;
                    if (group.Checked)
                    {
                        EnsureGroupReady(group);
                    }
                }

                bool selected = i == mSelectedGroupIndex;
                GUI.backgroundColor = selected ? new Color(0.6f, 0.8f, 1f) : Color.white;
                string keepMark = string.IsNullOrEmpty(group.KeepGuid) ? "" : " ·留";
                if (GUILayout.Button(
                    $"{group.Name} ({group.Items.Count}){keepMark}",
                    selected ? EditorStyles.miniButtonMid : EditorStyles.miniButton,
                    GUILayout.ExpandWidth(true),
                    GUILayout.MinWidth(40f)))
                {
                    SelectGroup(i);
                }

                GUI.backgroundColor = Color.white;
                DrawGroupHintBadge(group);
                EditorGUILayout.EndHorizontal();
                visibleIndex++;
            }

            if (visibleIndex == 0)
            {
                EditorGUILayout.HelpBox("没有同名图片组。", UnityEditor.MessageType.None);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawGroupPreview()
        {
            EditorGUILayout.BeginVertical("box");
            if (mSelectedGroupIndex < 0 || mSelectedGroupIndex >= mGroups.Count)
            {
                EditorGUILayout.HelpBox("从左侧选一组同名图片，右侧会并排预览方便对比。", UnityEditor.MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            SameNameGroup group = mGroups[mSelectedGroupIndex];
            EditorGUILayout.LabelField($"对比：{group.Name}（{group.Items.Count} 张）", EditorStyles.boldLabel);
            DrawGroupSpecialHints(group);
            using (new EditorGUI.DisabledScope(mMergeRunning))
            {
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(group.KeepGuid)))
                {
                    if (GUILayout.Button("立即合并当前组", GUILayout.Height(24f)))
                    {
                        ImageItem keepItem = group.FindItem(group.KeepGuid);
                        if (keepItem != null)
                        {
                            TryKeepImage(group, keepItem);
                        }
                    }
                }

                mPreviewScroll = EditorGUILayout.BeginScrollView(mPreviewScroll);
                EditorGUILayout.BeginHorizontal();
                foreach (ImageItem item in group.Items)
                {
                    DrawImageCard(group, item);
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawImageCard(SameNameGroup group, ImageItem item)
        {
            bool isKeep = group.KeepGuid == item.Guid;
            Color oldColor = GUI.backgroundColor;
            GUI.backgroundColor = isKeep ? new Color(0.45f, 0.85f, 0.5f) : Color.white;
            EditorGUILayout.BeginVertical("box", GUILayout.Width(CardWidth));
            GUI.backgroundColor = oldColor;
            Rect previewRect = GUILayoutUtility.GetRect(PreviewSize, PreviewSize, GUILayout.Width(CardWidth - 12f));
            DrawPreview(previewRect, item);

            if (GUI.Button(previewRect, GUIContent.none, GUIStyle.none))
            {
                PingItem(item);
            }

            EditorGUILayout.LabelField($"{item.Width} x {item.Height}    {FormatSize(item.FileSize)}");
            EditorGUILayout.LabelField(item.AssetPath, EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"FR2 引用：{item.RefCount}", EditorStyles.boldLabel);
            DrawItemSpecialHints(item);
            DrawRefList(item);

            using (new EditorGUI.DisabledScope(mMergeRunning))
            {
                if (isKeep)
                {
                    EditorGUILayout.LabelField("已设为保留", EditorStyles.boldLabel);
                }
                else if (GUILayout.Button("设为保留", GUILayout.Height(26f)))
                {
                    group.KeepGuid = item.Guid;
                    group.Checked = true;
                }
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawPreview(Rect rect, ImageItem item)
        {
            EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));
            Texture texture = item.Texture;
            if (texture == null)
            {
                texture = AssetPreview.GetAssetPreview(item.MainAsset);
            }

            if (texture != null)
            {
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.Label(rect, "无预览", EditorStyles.centeredGreyMiniLabel);
            }
        }

        private static void DrawRefList(ImageItem item)
        {
            if (item.RefPaths.Count == 0)
            {
                EditorGUILayout.LabelField("无引用", EditorStyles.miniLabel);
                return;
            }

            int showCount = Mathf.Min(item.RefPaths.Count, 8);
            for (int i = 0; i < showCount; i++)
            {
                string path = item.RefPaths[i];
                if (GUILayout.Button(Path.GetFileName(path), EditorStyles.miniLabel))
                {
                    Object obj = AssetDatabase.LoadMainAssetAtPath(path);
                    if (obj != null)
                    {
                        EditorGUIUtility.PingObject(obj);
                        Selection.activeObject = obj;
                    }
                }
            }

            if (item.RefPaths.Count > showCount)
            {
                EditorGUILayout.LabelField($"... 还有 {item.RefPaths.Count - showCount} 个", EditorStyles.miniLabel);
            }
        }

        private static void DrawGroupHintBadge(SameNameGroup group)
        {
            bool hasMaterial = group.HasMaterialRef;
            bool hasAtlas = group.HasAtlasRef;
            string text = " ";
            Color color = Color.white;
            if (hasMaterial && hasAtlas)
            {
                text = "材质+图集";
                color = new Color(1f, 0.7f, 0.35f);
            }
            else if (hasMaterial)
            {
                text = "材质";
                color = new Color(1f, 0.75f, 0.2f);
            }
            else if (hasAtlas)
            {
                text = "图集";
                color = new Color(0.6f, 0.85f, 1f);
            }

            GUI.color = color;
            GUILayout.Label(text, EditorStyles.miniBoldLabel, GUILayout.Width(GroupHintWidth));
            GUI.color = Color.white;
        }

        private static void DrawGroupSpecialHints(SameNameGroup group)
        {
            string warning = BuildSpecialWarning(group, null);
            if (!string.IsNullOrEmpty(warning))
            {
                EditorGUILayout.HelpBox(warning, UnityEditor.MessageType.Warning);
            }
        }

        private static void DrawItemSpecialHints(ImageItem item)
        {
            if (item.MaterialPaths.Count > 0)
            {
                EditorGUILayout.HelpBox("被材质球引用：\n" + JoinNames(item.MaterialPaths), UnityEditor.MessageType.Warning);
            }

            if (item.AtlasFolderHints.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "位于图集打包文件夹内（图集引用的是文件夹，不是单张图）：\n" + JoinNames(item.AtlasFolderHints),
                    UnityEditor.MessageType.Info);
            }

            if (item.AnimPaths.Count > 0)
            {
                EditorGUILayout.HelpBox("被动画/控制器引用：\n" + JoinNames(item.AnimPaths), UnityEditor.MessageType.Info);
            }
        }

        private static string JoinNames(List<string> paths)
        {
            int show = Mathf.Min(paths.Count, 6);
            List<string> names = new List<string>(show);
            for (int i = 0; i < show; i++)
            {
                names.Add(Path.GetFileName(paths[i]));
            }

            string text = string.Join("、", names.ToArray());
            if (paths.Count > show)
            {
                text += $" 等 {paths.Count} 个";
            }

            return text;
        }

        private static string BuildSpecialWarning(SameNameGroup group, ImageItem keepItem)
        {
            List<string> materials = new List<string>();
            List<string> anims = new List<string>();
            List<string> atlasLost = new List<string>();
            List<string> atlasKeep = new List<string>();
            for (int i = 0; i < group.Items.Count; i++)
            {
                ImageItem item = group.Items[i];
                bool isKeep = keepItem != null && item.Guid == keepItem.Guid;
                if (!isKeep)
                {
                    AppendUnique(materials, item.MaterialPaths);
                    AppendUnique(anims, item.AnimPaths);
                }

                for (int j = 0; j < item.AtlasFolderHints.Count; j++)
                {
                    string atlasPath = item.AtlasFolderHints[j];
                    if (isKeep)
                    {
                        if (!atlasKeep.Contains(atlasPath))
                        {
                            atlasKeep.Add(atlasPath);
                        }
                    }
                    else if (!atlasLost.Contains(atlasPath) && (keepItem == null || !keepItem.AtlasFolderHints.Contains(atlasPath)))
                    {
                        atlasLost.Add(atlasPath);
                    }
                }
            }

            List<string> lines = new List<string>();
            if (materials.Count > 0)
            {
                lines.Add("被材质球引用，合并后材质会改指向保留图：\n" + JoinNames(materials));
            }

            if (atlasKeep.Count > 0 && atlasLost.Count > 0)
            {
                lines.Add(
                    "两张图分别在不同图集的打包文件夹里，图集只认文件夹，不会把另一张补进去。\n"
                    + "保留后：保留图仍在 "
                    + JoinNames(atlasKeep)
                    + "\n被删图会从这些图集里消失："
                    + JoinNames(atlasLost)
                    + "\n预制体/材质上的引用会改到保留图（走保留图所在图集）。无法同时留在两个图集，除非把文件拷到两个文件夹。");
            }
            else if (atlasLost.Count > 0)
            {
                lines.Add(
                    "图集按文件夹打包，不会改 packable。保留图不在这些图集的文件夹里，删掉后图集下次打包会少这张图：\n"
                    + JoinNames(atlasLost)
                    + "\n建议改留图集文件夹里的那张。");
            }
            else if (atlasKeep.Count > 0)
            {
                lines.Add("保留图在图集打包文件夹内，删掉其它目录的同名图即可，图集会按文件夹重新收集。");
            }

            if (anims.Count > 0)
            {
                lines.Add("被动画/控制器引用：\n" + JoinNames(anims));
            }

            return string.Join("\n\n", lines.ToArray());
        }

        private static void AppendUnique(List<string> target, List<string> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (!target.Contains(source[i]))
                {
                    target.Add(source[i]);
                }
            }
        }

        private void ApplyFolderAsset()
        {
            if (mSearchFolder == null)
            {
                return;
            }

            string path = AssetDatabase.GetAssetPath(mSearchFolder);
            if (!AssetDatabase.IsValidFolder(path))
            {
                EditorUtility.DisplayDialog("同名图片合并", "请选择 Assets 下的文件夹。", "确定");
                mSearchFolder = null;
                return;
            }

            mSearchPath = path;
            EditorPrefs.SetString(PrefsSearchPath, mSearchPath);
        }

        private void PickFolder()
        {
            string startDir = Path.Combine(Directory.GetCurrentDirectory(), mSearchPath);
            string selected = EditorUtility.OpenFolderPanel("选择扫描目录", startDir, "");
            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            string projectRoot = Directory.GetCurrentDirectory().Replace("\\", "/");
            selected = selected.Replace("\\", "/");
            if (!selected.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("同名图片合并", "请选择当前工程内的目录。", "确定");
                return;
            }

            string relative = selected.Substring(projectRoot.Length).TrimStart('/');
            if (!relative.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog("同名图片合并", "Find Reference 2 只能处理 Assets 下的资源。", "确定");
                return;
            }

            mSearchPath = relative;
            mSearchFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(mSearchPath);
            EditorPrefs.SetString(PrefsSearchPath, mSearchPath);
        }

        private void ScanSameNameImages()
        {
            ApplyFolderAsset();
            if (string.IsNullOrEmpty(mSearchPath) || !AssetDatabase.IsValidFolder(mSearchPath))
            {
                EditorUtility.DisplayDialog("同名图片合并", "请先指定有效的 Assets 目录。", "确定");
                return;
            }

            mGroups.Clear();
            mSelectedGroupIndex = -1;
            // 先只按文件名归组，选中某组时再加载预览和 FR2 引用
            Dictionary<string, List<ImageItem>> map = new Dictionary<string, List<ImageItem>>(StringComparer.OrdinalIgnoreCase);
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { mSearchPath });
            for (int i = 0; i < guids.Length; i++)
            {
                string guid = guids[i];
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsImagePath(path))
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(path);
                if (!map.TryGetValue(name, out List<ImageItem> items))
                {
                    items = new List<ImageItem>();
                    map.Add(name, items);
                }

                items.Add(new ImageItem(guid, path));
            }

            foreach (KeyValuePair<string, List<ImageItem>> pair in map)
            {
                if (pair.Value.Count > 1)
                {
                    SameNameGroup group = new SameNameGroup(pair.Key);
                    pair.Value.Sort((a, b) => string.Compare(a.AssetPath, b.AssetPath, StringComparison.OrdinalIgnoreCase));
                    group.Items.AddRange(pair.Value);
                    mGroups.Add(group);
                }
            }

            mGroups.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            RebuildAtlasFolderCache();
            for (int i = 0; i < mGroups.Count; i++)
            {
                BindAtlasFolders(mGroups[i]);
            }

            PrefetchAllGroupHints();
            mStatus = $"在 {mSearchPath} 下找到 {mGroups.Count} 组同名图片。";
        }

        /// <summary>
        /// 工程图集按文件夹打包，这里记下每个图集的 packable 文件夹，用来提示删图后会不会掉出图集。
        /// </summary>
        private void RebuildAtlasFolderCache()
        {
            mAtlasFolders.Clear();
            string[] atlasGuids = AssetDatabase.FindAssets("t:SpriteAtlas");
            for (int i = 0; i < atlasGuids.Length; i++)
            {
                string atlasPath = AssetDatabase.GUIDToAssetPath(atlasGuids[i]);
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

                for (int j = 0; j < packables.Length; j++)
                {
                    if (packables[j] == null)
                    {
                        continue;
                    }

                    string packablePath = AssetDatabase.GetAssetPath(packables[j]).Replace("\\", "/");
                    if (string.IsNullOrEmpty(packablePath) || !AssetDatabase.IsValidFolder(packablePath))
                    {
                        continue;
                    }

                    if (!packablePath.EndsWith("/"))
                    {
                        packablePath += "/";
                    }

                    mAtlasFolders.Add(new AtlasFolderPack
                    {
                        AtlasPath = atlasPath,
                        FolderPath = packablePath
                    });
                }
            }
        }

        private void BindAtlasFolders(SameNameGroup group)
        {
            for (int i = 0; i < group.Items.Count; i++)
            {
                ImageItem item = group.Items[i];
                item.AtlasFolderHints.Clear();
                string imagePath = item.AssetPath.Replace("\\", "/");
                for (int j = 0; j < mAtlasFolders.Count; j++)
                {
                    AtlasFolderPack pack = mAtlasFolders[j];
                    if (imagePath.StartsWith(pack.FolderPath, StringComparison.OrdinalIgnoreCase)
                        && !item.AtlasFolderHints.Contains(pack.AtlasPath))
                    {
                        item.AtlasFolderHints.Add(pack.AtlasPath);
                    }
                }
            }
        }

        private void SelectGroup(int index)
        {
            mSelectedGroupIndex = index;
            mPreviewScroll = Vector2.zero;
            EnsureGroupReady(mGroups[index]);
        }

        private static void EnsureGroupReady(SameNameGroup group)
        {
            bool fr2Ready = Fr2CacheUtil.IsReady();
            foreach (ImageItem item in group.Items)
            {
                item.LoadPreview();
                if (fr2Ready)
                {
                    item.LoadReferences();
                }
                else
                {
                    item.ClearReferences();
                }
            }

            group.RefsLoaded = true;
        }

        /// <summary>
        /// 查找后立刻查出每组的材质/图集提示，避免要点进组才出现。
        /// </summary>
        private void PrefetchAllGroupHints()
        {
            bool fr2Ready = Fr2CacheUtil.IsReady();
            try
            {
                for (int i = 0; i < mGroups.Count; i++)
                {
                    SameNameGroup group = mGroups[i];
                    if (fr2Ready)
                    {
                        if (mGroups.Count > 20)
                        {
                            EditorUtility.DisplayProgressBar(
                                "同名图片合并",
                                "读取 FR2 引用 " + (i + 1) + "/" + mGroups.Count,
                                (i + 1) / (float)mGroups.Count);
                        }

                        for (int j = 0; j < group.Items.Count; j++)
                        {
                            group.Items[j].LoadReferences();
                        }

                        group.RefsLoaded = true;
                    }
                    else
                    {
                        for (int j = 0; j < group.Items.Count; j++)
                        {
                            group.Items[j].ClearReferences();
                        }

                        group.RefsLoaded = false;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void SetVisibleGroupsChecked(bool isChecked)
        {
            for (int i = 0; i < mGroups.Count; i++)
            {
                if (!IsGroupVisible(mGroups[i]))
                {
                    continue;
                }

                mGroups[i].Checked = isChecked;
                if (isChecked)
                {
                    EnsureGroupReady(mGroups[i]);
                }
            }
        }

        private int CountCheckedGroups()
        {
            int count = 0;
            for (int i = 0; i < mGroups.Count; i++)
            {
                if (mGroups[i].Checked)
                {
                    count++;
                }
            }

            return count;
        }

        private void TryBatchMerge()
        {
            List<MergeJob> jobs = new List<MergeJob>();
            List<string> missingKeep = new List<string>();
            for (int i = 0; i < mGroups.Count; i++)
            {
                SameNameGroup group = mGroups[i];
                if (!group.Checked)
                {
                    continue;
                }

                EnsureGroupReady(group);
                ImageItem keepItem = group.FindItem(group.KeepGuid);
                if (keepItem == null)
                {
                    missingKeep.Add(group.Name);
                    continue;
                }

                jobs.Add(new MergeJob(group, keepItem));
            }

            if (missingKeep.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    "同名图片合并",
                    "这些已勾选组还没设保留图，请先点开并点「设为保留」：\n" + string.Join("\n", missingKeep.ToArray()),
                    "确定");
                return;
            }

            if (jobs.Count == 0)
            {
                EditorUtility.DisplayDialog("同名图片合并", "请先勾选要合并的组，并为每组设一张保留图。", "确定");
                return;
            }

            TryMergeJobs(jobs);
        }

        private void TryKeepImage(SameNameGroup group, ImageItem keepItem)
        {
            group.KeepGuid = keepItem.Guid;
            group.Checked = true;
            TryMergeJobs(new List<MergeJob> { new MergeJob(group, keepItem) });
        }

        private void TryMergeJobs(List<MergeJob> jobs)
        {
            if (mMergeRunning)
            {
                EditorUtility.DisplayDialog("同名图片合并", "上一次合并还在进行中，请稍后再试。", "确定");
                return;
            }

            bool fr2Ready = Fr2CacheUtil.IsReady();
            string message = BuildConfirmMessage(jobs, fr2Ready);
            if (!EditorUtility.DisplayDialog("确认保留并合并", message, "确定", "取消"))
            {
                return;
            }

            mMergeRunning = true;
            try
            {
                SavePrefabStageIfOpen();
                EditorSceneManager.SaveOpenScenes();

                int totalScene = 0;
                int totalAsset = 0;
                int totalYaml = 0;
                int totalDelete = 0;
                HashSet<string> patchedYaml = new HashSet<string>();
                List<string> allDeletePaths = new List<string>();
                List<SameNameGroup> doneGroups = new List<SameNameGroup>();

                for (int i = 0; i < jobs.Count; i++)
                {
                    MergeJob job = jobs[i];
                    job.KeepItem.LoadPreview();
                    KeepImageAssets keepAssets = LoadKeepAssets(job.KeepItem);
                    if (keepAssets.Texture == null)
                    {
                        Debug.LogWarning("保留图加载失败，已跳过：" + job.KeepItem.AssetPath);
                        continue;
                    }

                    List<ImageItem> deleteItems = job.Group.Items.Where(item => item.Guid != job.KeepItem.Guid).ToList();
                    HashSet<string> deleteGuids = new HashSet<string>();
                    for (int j = 0; j < deleteItems.Count; j++)
                    {
                        deleteGuids.Add(deleteItems[j].Guid);
                    }

                    List<ImageFileIds> deleteFileIds = LoadDeleteFileIds(deleteItems);
                    totalScene += RetargetLoadedSceneObjects(deleteGuids, keepAssets);
                    if (fr2Ready)
                    {
                        totalAsset += RetargetFr2UsedByAssets(deleteItems, deleteGuids, keepAssets, deleteFileIds);
                    }

                    HashSet<string> yamlPaths = CollectYamlPatchPaths(deleteItems, fr2Ready);
                    totalYaml += PatchYamlFiles(yamlPaths, deleteFileIds, keepAssets, patchedYaml);
                    for (int j = 0; j < deleteItems.Count; j++)
                    {
                        allDeletePaths.Add(deleteItems[j].AssetPath);
                    }

                    totalDelete += deleteItems.Count;
                    doneGroups.Add(job.Group);
                }

                SavePrefabStageIfOpen();
                EditorSceneManager.SaveOpenScenes();
                ReloadPatchedOpenScenes(patchedYaml);
                AssetDatabase.SaveAssets();
                DeleteAssetPaths(allDeletePaths);

                for (int i = 0; i < doneGroups.Count; i++)
                {
                    mGroups.Remove(doneGroups[i]);
                }

                mSelectedGroupIndex = -1;
                mStatus = jobs.Count > 1
                    ? $"已批量合并 {doneGroups.Count} 组，场景 {totalScene} 处，磁盘 {totalAsset} 处，YAML {totalYaml} 个文件，删除 {totalDelete} 张。"
                    : $"已保留 {jobs[0].KeepItem.AssetPath}，场景 {totalScene} 处，磁盘 {totalAsset} 处，YAML {totalYaml} 个文件，删除 {totalDelete} 张。";
            }
            finally
            {
                mMergeRunning = false;
            }
        }

        private static string BuildConfirmMessage(List<MergeJob> jobs, bool fr2Ready)
        {
            StringBuilder sb = new StringBuilder();
            if (jobs.Count == 1)
            {
                MergeJob job = jobs[0];
                List<ImageItem> deleteItems = job.Group.Items.Where(item => item.Guid != job.KeepItem.Guid).ToList();
                sb.Append("保留：").Append(job.KeepItem.AssetPath).Append('\n');
                sb.Append("删除：").Append(deleteItems.Count).Append(" 张同名图\n");
                sb.Append("FR2 记录的引用数：").Append(deleteItems.Sum(item => item.RefCount));
                string special = BuildSpecialWarning(job.Group, job.KeepItem);
                if (!string.IsNullOrEmpty(special))
                {
                    sb.Append("\n\n").Append(special);
                }
            }
            else
            {
                sb.Append("将一次合并 ").Append(jobs.Count).Append(" 组，删除后只刷新一次，避免反复编译。\n");
                int show = Mathf.Min(jobs.Count, 12);
                for (int i = 0; i < show; i++)
                {
                    sb.Append("\n- ").Append(jobs[i].Group.Name).Append(" → ").Append(Path.GetFileName(jobs[i].KeepItem.AssetPath));
                    if (jobs[i].Group.HasMaterialRef)
                    {
                        sb.Append("（含材质球引用）");
                    }

                    if (jobs[i].Group.HasAtlasRef)
                    {
                        sb.Append("（含图集文件夹）");
                    }
                }

                if (jobs.Count > show)
                {
                    sb.Append("\n- ... 还有 ").Append(jobs.Count - show).Append(" 组");
                }

                int materialGroupCount = 0;
                for (int i = 0; i < jobs.Count; i++)
                {
                    if (jobs[i].Group.HasMaterialRef)
                    {
                        materialGroupCount++;
                    }
                }

                if (materialGroupCount > 0)
                {
                    sb.Append("\n\n其中 ").Append(materialGroupCount).Append(" 组被材质球引用，合并后材质会改指向各组的保留图。");
                }

                int atlasGroupCount = 0;
                for (int i = 0; i < jobs.Count; i++)
                {
                    if (jobs[i].Group.HasAtlasRef)
                    {
                        atlasGroupCount++;
                    }
                }

                if (atlasGroupCount > 0)
                {
                    sb.Append("\n其中 ").Append(atlasGroupCount).Append(" 组在图集文件夹内：保留图留在自己图集，被删图会从另一图集消失。");
                }
            }

            if (!fr2Ready)
            {
                sb.Append("\n\nFR2 缓存未就绪：会改当前打开场景里的引用，预制体等磁盘引用可能漏掉。");
            }

            sb.Append("\n\n操作不可撤销，请确认资源已提交或可回滚。");
            return sb.ToString();
        }

        private static void DeleteAssetPaths(List<string> deletePaths)
        {
            if (deletePaths.Count == 0)
            {
                return;
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < deletePaths.Count; i++)
                {
                    string path = deletePaths[i];
                    if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                    {
                        AssetDatabase.DeleteAsset(path);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// 遍历当前打开场景和 Prefab Stage 里的组件，把指向待删图的 Sprite/Texture 改到保留图。
        /// 必须用对象赋值，不能只换 YAML 里的 guid，否则 Sprite 的 fileID 对不上会变白。
        /// </summary>
        private static int RetargetLoadedSceneObjects(HashSet<string> deleteGuids, KeepImageAssets keepAssets)
        {
            int count = 0;
            GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allObjects.Length; i++)
            {
                GameObject go = allObjects[i];
                if (go == null || !go.scene.IsValid() || EditorUtility.IsPersistent(go))
                {
                    continue;
                }

                Component[] components = go.GetComponents<Component>();
                for (int j = 0; j < components.Length; j++)
                {
                    int changed = RetargetObject(components[j], deleteGuids, keepAssets);
                    if (changed > 0)
                    {
                        EditorSceneManager.MarkSceneDirty(go.scene);
                    }

                    count += changed;
                }
            }

            return count;
        }

        /// <summary>
        /// 用 FR2 的 UsedBy 找到磁盘上的引用资源，再用 SerializedObject 改指向。
        /// </summary>
        private static int RetargetFr2UsedByAssets(
            List<ImageItem> deleteItems,
            HashSet<string> deleteGuids,
            KeepImageAssets keepAssets,
            List<ImageFileIds> deleteFileIds)
        {
            int count = 0;
            HashSet<string> visited = new HashSet<string>();
            for (int i = 0; i < deleteItems.Count; i++)
            {
                Dictionary<string, FR2_Ref> refs = FR2_Ref.FindUsedBy(new[] { deleteItems[i].Guid });
                if (refs == null)
                {
                    continue;
                }

                foreach (KeyValuePair<string, FR2_Ref> pair in refs)
                {
                    if (pair.Value == null || pair.Value.asset == null || pair.Value.depth != 1)
                    {
                        continue;
                    }

                    string path = pair.Value.asset.assetPath;
                    if (string.IsNullOrEmpty(path) || !visited.Add(path))
                    {
                        continue;
                    }

                    if (AssetDatabase.IsValidFolder(path) || deleteGuids.Contains(pair.Key))
                    {
                        continue;
                    }

                    count += RetargetAssetAtPath(path, deleteGuids, keepAssets, deleteFileIds);
                }
            }

            return count;
        }

        private static int RetargetAssetAtPath(
            string path,
            HashSet<string> deleteGuids,
            KeepImageAssets keepAssets,
            List<ImageFileIds> deleteFileIds)
        {
            // 场景资源不能当普通资产加载改引用，直接改 YAML。
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return ReplaceGuidsInYamlFile(path, deleteFileIds, keepAssets) ? 1 : 0;
            }

            // 图集 packable 是文件夹，不能把单张图 GUID 写进图集。
            if (path.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".spriteatlasv2", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int count = 0;
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (assets == null || assets.Length == 0)
            {
                Object main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main != null)
                {
                    count += RetargetObject(main, deleteGuids, keepAssets);
                }

                return count;
            }

            for (int i = 0; i < assets.Length; i++)
            {
                Object asset = assets[i];
                if (asset is Component || asset is Material || asset is ScriptableObject)
                {
                    count += RetargetObject(asset, deleteGuids, keepAssets);
                }
            }

            return count;
        }

        private static int RetargetObject(Object target, HashSet<string> deleteGuids, KeepImageAssets keepAssets)
        {
            if (target == null)
            {
                return 0;
            }

            SerializedObject so = new SerializedObject(target);
            int count = 0;
            count += RetargetObjectReferenceProperty(so.FindProperty("m_Sprite"), deleteGuids, keepAssets);
            count += RetargetObjectReferenceProperty(so.FindProperty("m_Texture"), deleteGuids, keepAssets);

            SerializedProperty prop = so.GetIterator();
            bool enterChildren = true;
            while (prop.Next(enterChildren))
            {
                enterChildren = true;
                if (prop.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                count += RetargetObjectReferenceProperty(prop, deleteGuids, keepAssets);
            }

            if (count > 0)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(target);
                Component component = target as Component;
                if (component != null)
                {
                    EditorUtility.SetDirty(component.gameObject);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }
            }

            return count;
        }

        private static int RetargetObjectReferenceProperty(
            SerializedProperty prop,
            HashSet<string> deleteGuids,
            KeepImageAssets keepAssets)
        {
            if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference || prop.objectReferenceValue == null)
            {
                return 0;
            }

            Object value = prop.objectReferenceValue;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long _))
            {
                return 0;
            }

            if (!deleteGuids.Contains(guid))
            {
                return 0;
            }

            Object replacement = null;
            Sprite oldSprite = value as Sprite;
            if (oldSprite != null)
            {
                replacement = PickKeepSprite(keepAssets, oldSprite);
            }
            else if (value is Texture)
            {
                replacement = keepAssets.Texture;
            }

            if (replacement == null || replacement == value)
            {
                return 0;
            }

            prop.objectReferenceValue = replacement;
            return 1;
        }

        private static KeepImageAssets LoadKeepAssets(ImageItem keepItem)
        {
            KeepImageAssets result = new KeepImageAssets
            {
                Guid = keepItem.Guid,
                Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(keepItem.AssetPath),
                Sprites = new List<Sprite>()
            };
            if (result.Texture != null)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(result.Texture, out _, out result.TextureFileId);
            }

            Object[] all = AssetDatabase.LoadAllAssetsAtPath(keepItem.AssetPath);
            for (int i = 0; i < all.Length; i++)
            {
                Sprite sprite = all[i] as Sprite;
                if (sprite != null)
                {
                    result.Sprites.Add(sprite);
                }
            }

            if (result.Sprites.Count > 0)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(result.Sprites[0], out _, out result.SpriteFileId);
            }

            return result;
        }

        private static List<ImageFileIds> LoadDeleteFileIds(List<ImageItem> deleteItems)
        {
            List<ImageFileIds> result = new List<ImageFileIds>();
            for (int i = 0; i < deleteItems.Count; i++)
            {
                ImageItem item = deleteItems[i];
                ImageFileIds ids = new ImageFileIds { Guid = item.Guid };
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(item.AssetPath);
                if (texture != null)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out _, out ids.TextureFileId);
                }

                Object[] all = AssetDatabase.LoadAllAssetsAtPath(item.AssetPath);
                for (int j = 0; j < all.Length; j++)
                {
                    Sprite sprite = all[j] as Sprite;
                    if (sprite == null)
                    {
                        continue;
                    }

                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out _, out ids.SpriteFileId);
                    break;
                }

                result.Add(ids);
            }

            return result;
        }

        private static void AddScenesContainingGuids(HashSet<string> paths, List<ImageItem> deleteItems)
        {
            string[] sceneGuids = AssetDatabase.FindAssets("t:Scene");
            for (int i = 0; i < sceneGuids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(sceneGuids[i]);
                if (string.IsNullOrEmpty(path)
                    || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(path))
                {
                    continue;
                }

                string text = File.ReadAllText(path);
                for (int j = 0; j < deleteItems.Count; j++)
                {
                    if (text.IndexOf(deleteItems[j].Guid, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    paths.Add(path);
                    break;
                }
            }
        }

        private static HashSet<string> CollectYamlPatchPaths(List<ImageItem> deleteItems, bool fr2Ready)
        {
            HashSet<string> paths = new HashSet<string>();
            AddScenesContainingGuids(paths, deleteItems);
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                string path = EditorSceneManager.GetSceneAt(i).path;
                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }

            if (!fr2Ready)
            {
                return paths;
            }

            for (int i = 0; i < deleteItems.Count; i++)
            {
                Dictionary<string, FR2_Ref> refs = FR2_Ref.FindUsedBy(new[] { deleteItems[i].Guid });
                if (refs == null)
                {
                    continue;
                }

                foreach (KeyValuePair<string, FR2_Ref> pair in refs)
                {
                    if (pair.Value == null || pair.Value.asset == null || pair.Value.depth != 1)
                    {
                        continue;
                    }

                    string path = pair.Value.asset.assetPath;
                    if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                    {
                        paths.Add(path);
                    }
                }
            }

            return paths;
        }

        private static int PatchYamlFiles(
            HashSet<string> paths,
            List<ImageFileIds> deleteFileIds,
            KeepImageAssets keepAssets,
            HashSet<string> patched)
        {
            int count = 0;
            foreach (string path in paths)
            {
                if (ReplaceGuidsInYamlFile(path, deleteFileIds, keepAssets))
                {
                    patched.Add(path);
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 直接替换 YAML 里的 guid 和 Sprite/Texture fileID，保证切场景后磁盘引用仍有效。
        /// </summary>
        private static bool ReplaceGuidsInYamlFile(
            string path,
            List<ImageFileIds> deleteFileIds,
            KeepImageAssets keepAssets)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !IsYamlAssetPath(path))
            {
                return false;
            }

            string text = File.ReadAllText(path);
            string original = text;
            for (int i = 0; i < deleteFileIds.Count; i++)
            {
                ImageFileIds from = deleteFileIds[i];
                if (from.SpriteFileId != 0 && keepAssets.SpriteFileId != 0)
                {
                    text = text.Replace(
                        "fileID: " + from.SpriteFileId + ", guid: " + from.Guid,
                        "fileID: " + keepAssets.SpriteFileId + ", guid: " + keepAssets.Guid);
                }

                if (from.TextureFileId != 0 && keepAssets.TextureFileId != 0)
                {
                    text = text.Replace(
                        "fileID: " + from.TextureFileId + ", guid: " + from.Guid,
                        "fileID: " + keepAssets.TextureFileId + ", guid: " + keepAssets.Guid);
                }

                text = text.Replace(from.Guid, keepAssets.Guid);
            }

            if (text == original)
            {
                return false;
            }

            File.WriteAllText(path, text);
            return true;
        }

        private static void ReloadPatchedOpenScenes(HashSet<string> patched)
        {
            if (patched == null || patched.Count == 0)
            {
                return;
            }

            List<string> openPaths = new List<string>();
            string activePath = EditorSceneManager.GetActiveScene().path;
            bool needReload = false;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                string path = EditorSceneManager.GetSceneAt(i).path;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                openPaths.Add(path);
                if (patched.Contains(path))
                {
                    needReload = true;
                }
            }

            if (!needReload)
            {
                return;
            }

            for (int i = 0; i < openPaths.Count; i++)
            {
                OpenSceneMode mode = i == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive;
                EditorSceneManager.OpenScene(openPaths[i], mode);
            }

            if (!string.IsNullOrEmpty(activePath))
            {
                UnityEngine.SceneManagement.Scene active = EditorSceneManager.GetSceneByPath(activePath);
                if (active.IsValid())
                {
                    EditorSceneManager.SetActiveScene(active);
                }
            }
        }

        private static void SavePrefabStageIfOpen()
        {
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || string.IsNullOrEmpty(stage.assetPath))
            {
                return;
            }

            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath);
        }

        private static bool IsYamlAssetPath(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".unity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".prefab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".asset", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".controller", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".overrideController", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".anim", StringComparison.OrdinalIgnoreCase);
        }

        private static Sprite PickKeepSprite(KeepImageAssets keepAssets, Sprite oldSprite)
        {
            if (keepAssets.Sprites.Count == 0)
            {
                return null;
            }

            if (oldSprite != null)
            {
                for (int i = 0; i < keepAssets.Sprites.Count; i++)
                {
                    if (keepAssets.Sprites[i].name == oldSprite.name)
                    {
                        return keepAssets.Sprites[i];
                    }
                }
            }

            return keepAssets.Sprites[0];
        }

        private sealed class KeepImageAssets
        {
            public string Guid;
            public Texture2D Texture;
            public List<Sprite> Sprites;
            public long TextureFileId;
            public long SpriteFileId;
        }

        private sealed class ImageFileIds
        {
            public string Guid;
            public long TextureFileId;
            public long SpriteFileId;
        }

        private IEnumerable<SameNameGroup> GetVisibleGroups()
        {
            return mGroups.Where(IsGroupVisible);
        }

        private bool IsGroupVisible(SameNameGroup group)
        {
            if (string.IsNullOrEmpty(mFilterText))
            {
                return true;
            }

            return group.Name.IndexOf(mFilterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsImagePath(string path)
        {
            string ext = Path.GetExtension(path);
            for (int i = 0; i < ImageExtensions.Length; i++)
            {
                if (string.Equals(ext, ImageExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void PingItem(ImageItem item)
        {
            if (item.MainAsset == null)
            {
                return;
            }

            EditorGUIUtility.PingObject(item.MainAsset);
            Selection.activeObject = item.MainAsset;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024f).ToString("0.0") + " KB";
            }

            return (bytes / (1024f * 1024f)).ToString("0.00") + " MB";
        }

        private sealed class AtlasFolderPack
        {
            public string AtlasPath;
            public string FolderPath;
        }

        private sealed class MergeJob
        {
            public MergeJob(SameNameGroup group, ImageItem keepItem)
            {
                Group = group;
                KeepItem = keepItem;
            }

            public SameNameGroup Group { get; }
            public ImageItem KeepItem { get; }
        }

        private sealed class SameNameGroup
        {
            public SameNameGroup(string name)
            {
                Name = name;
                Items = new List<ImageItem>();
            }

            public string Name { get; }
            public List<ImageItem> Items { get; }
            public bool Checked;
            public string KeepGuid = "";
            public bool RefsLoaded;

            public bool HasMaterialRef
            {
                get
                {
                    for (int i = 0; i < Items.Count; i++)
                    {
                        if (Items[i].MaterialPaths.Count > 0)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public bool HasAtlasRef
            {
                get
                {
                    for (int i = 0; i < Items.Count; i++)
                    {
                        if (Items[i].AtlasFolderHints.Count > 0)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public ImageItem FindItem(string guid)
            {
                if (string.IsNullOrEmpty(guid))
                {
                    return null;
                }

                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i].Guid == guid)
                    {
                        return Items[i];
                    }
                }

                return null;
            }
        }

        private sealed class ImageItem
        {
            public ImageItem(string guid, string assetPath)
            {
                Guid = guid;
                AssetPath = assetPath;
                RefPaths = new List<string>();
                MaterialPaths = new List<string>();
                AtlasPaths = new List<string>();
                AtlasFolderHints = new List<string>();
                AnimPaths = new List<string>();
            }

            public string Guid { get; }
            public string AssetPath { get; }
            public Object MainAsset { get; private set; }
            public Texture2D Texture { get; private set; }
            public int Width { get; private set; }
            public int Height { get; private set; }
            public long FileSize { get; private set; }
            public int RefCount { get; private set; }
            public List<string> RefPaths { get; }
            public List<string> MaterialPaths { get; }
            public List<string> AtlasPaths { get; }
            public List<string> AtlasFolderHints { get; }
            public List<string> AnimPaths { get; }

            public void LoadPreview()
            {
                if (MainAsset == null)
                {
                    MainAsset = AssetDatabase.LoadMainAssetAtPath(AssetPath);
                }

                if (Texture == null)
                {
                    Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetPath);
                }

                if (FileSize == 0)
                {
                    string fullPath = Path.Combine(Directory.GetCurrentDirectory(), AssetPath);
                    if (File.Exists(fullPath))
                    {
                        FileSize = new FileInfo(fullPath).Length;
                    }
                }

                if (Texture != null)
                {
                    Width = Texture.width;
                    Height = Texture.height;
                }

                if (MainAsset != null)
                {
                    AssetPreview.GetAssetPreview(MainAsset);
                }
            }

            public void ClearReferences()
            {
                RefCount = 0;
                RefPaths.Clear();
                MaterialPaths.Clear();
                AtlasPaths.Clear();
                AnimPaths.Clear();
            }

            public void LoadReferences()
            {
                ClearReferences();
                Dictionary<string, FR2_Ref> refs = FR2_Ref.FindUsedBy(new[] { Guid });
                if (refs == null)
                {
                    return;
                }

                if (refs.TryGetValue(Guid, out FR2_Ref self) && self != null && self.asset != null)
                {
                    RefCount = self.asset.UsageCount();
                }

                foreach (KeyValuePair<string, FR2_Ref> pair in refs)
                {
                    if (pair.Key == Guid || pair.Value == null || pair.Value.asset == null || pair.Value.depth != 1)
                    {
                        continue;
                    }

                    string path = pair.Value.asset.assetPath;
                    if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
                    {
                        continue;
                    }

                    RefPaths.Add(path);
                    ClassifyRefPath(path);
                }

                RefPaths.Sort(StringComparer.OrdinalIgnoreCase);
            }

            private void ClassifyRefPath(string path)
            {
                string ext = Path.GetExtension(path);
                if (string.Equals(ext, ".mat", StringComparison.OrdinalIgnoreCase))
                {
                    MaterialPaths.Add(path);
                    return;
                }

                if (string.Equals(ext, ".spriteatlas", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".spriteatlasv2", StringComparison.OrdinalIgnoreCase))
                {
                    AtlasPaths.Add(path);
                    return;
                }

                if (string.Equals(ext, ".controller", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".overrideController", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".anim", StringComparison.OrdinalIgnoreCase))
                {
                    AnimPaths.Add(path);
                }
            }
        }

        /// <summary>
        /// FR2 的 isReady 是内部属性，这里用反射只读检查缓存状态。
        /// </summary>
        private static class Fr2CacheUtil
        {
            private static readonly PropertyInfo ReadyProperty =
                typeof(FR2_Cache).GetProperty("isReady", BindingFlags.NonPublic | BindingFlags.Static);

            public static bool IsReady()
            {
                if (ReadyProperty == null)
                {
                    return false;
                }

                object value = ReadyProperty.GetValue(null, null);
                return value is bool ready && ready;
            }
        }
    }
}
