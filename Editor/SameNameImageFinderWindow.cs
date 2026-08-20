using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
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
        private const float CardWidth = 220f;

        private static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".exr"
        };

        [SerializeField] private DefaultAsset mSearchFolder;
        [SerializeField] private string mSearchPath = "Assets";
        [SerializeField] private string mFilterText = "";

        private readonly List<SameNameGroup> mGroups = new List<SameNameGroup>();
        private Vector2 mGroupScroll;
        private Vector2 mPreviewScroll;
        private int mSelectedGroupIndex = -1;
        private string mStatus = "选择目录后点击查找。";
        private bool mMergeRunning;
        private bool mWasFr2Ready;
        private SameNameGroup mPendingDeleteGroup;
        private string mPendingKeepGuid;

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

        private void OnDisable()
        {
            EditorApplication.update -= WaitMergeThenDelete;
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
            if (fr2Ready && !mWasFr2Ready && mSelectedGroupIndex >= 0 && mSelectedGroupIndex < mGroups.Count)
            {
                SelectGroup(mSelectedGroupIndex);
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
            EditorGUILayout.EndVertical();
        }

        private void DrawFr2Status()
        {
            if (Fr2CacheUtil.IsReady())
            {
                EditorGUILayout.HelpBox(
                    "Find Reference 2 缓存已就绪。选中一组同名图后会列出引用，保留某张时会改指向并删除其余图片。",
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
            EditorGUILayout.BeginVertical("box", GUILayout.Width(280f));
            EditorGUILayout.LabelField($"同名组（{GetVisibleGroups().Count()}）", EditorStyles.boldLabel);
            mGroupScroll = EditorGUILayout.BeginScrollView(mGroupScroll);
            int visibleIndex = 0;
            for (int i = 0; i < mGroups.Count; i++)
            {
                SameNameGroup group = mGroups[i];
                if (!IsGroupVisible(group))
                {
                    continue;
                }

                bool selected = i == mSelectedGroupIndex;
                GUI.backgroundColor = selected ? new Color(0.6f, 0.8f, 1f) : Color.white;
                if (GUILayout.Button($"{group.Name}  ({group.Items.Count})", selected ? EditorStyles.miniButtonMid : EditorStyles.miniButton))
                {
                    SelectGroup(i);
                }

                GUI.backgroundColor = Color.white;
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
            using (new EditorGUI.DisabledScope(mMergeRunning))
            {
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
            EditorGUILayout.BeginVertical("box", GUILayout.Width(CardWidth));
            Rect previewRect = GUILayoutUtility.GetRect(PreviewSize, PreviewSize, GUILayout.Width(CardWidth - 12f));
            DrawPreview(previewRect, item);

            if (GUI.Button(previewRect, GUIContent.none, GUIStyle.none))
            {
                PingItem(item);
            }

            EditorGUILayout.LabelField($"{item.Width} x {item.Height}    {FormatSize(item.FileSize)}");
            EditorGUILayout.LabelField(item.AssetPath, EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"FR2 引用：{item.RefCount}", EditorStyles.boldLabel);

            DrawRefList(item);

            using (new EditorGUI.DisabledScope(mMergeRunning || !Fr2CacheUtil.IsReady()))
            {
                GUI.backgroundColor = new Color(0.55f, 0.9f, 0.55f);
                if (GUILayout.Button("保留这张，删除其它", GUILayout.Height(26f)))
                {
                    TryKeepImage(group, item);
                }

                GUI.backgroundColor = Color.white;
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
            mStatus = $"在 {mSearchPath} 下找到 {mGroups.Count} 组同名图片。";
        }

        private void SelectGroup(int index)
        {
            mSelectedGroupIndex = index;
            mPreviewScroll = Vector2.zero;
            SameNameGroup group = mGroups[index];
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
        }

        private void TryKeepImage(SameNameGroup group, ImageItem keepItem)
        {
            if (!Fr2CacheUtil.IsReady())
            {
                EditorUtility.DisplayDialog("同名图片合并", "请先打开 Find Reference 2 并等缓存扫描完成。", "确定");
                return;
            }

            if (FR2_Export.IsMergeProcessing || mMergeRunning)
            {
                EditorUtility.DisplayDialog("同名图片合并", "上一次合并还在进行中，请稍后再试。", "确定");
                return;
            }

            List<ImageItem> deleteItems = group.Items.Where(item => item.Guid != keepItem.Guid).ToList();
            int moveRefCount = deleteItems.Sum(item => item.RefCount);
            string message =
                $"保留：{keepItem.AssetPath}\n删除：{deleteItems.Count} 张同名图\n将改写的引用数：{moveRefCount}\n\n操作不可撤销，请确认资源已提交或可回滚。";
            if (!EditorUtility.DisplayDialog("确认保留并合并", message, "确定", "取消"))
            {
                return;
            }

            Object[] selection = group.Items
                .Select(item =>
                {
                    item.LoadPreview();
                    return item.MainAsset;
                })
                .Where(asset => asset != null)
                .ToArray();
            if (selection.Length != group.Items.Count)
            {
                EditorUtility.DisplayDialog("同名图片合并", "有图片加载失败，已取消。", "确定");
                return;
            }

            Selection.objects = selection;
            FR2_Export.MergeDuplicate(keepItem.Guid);
            if (!FR2_Export.IsMergeProcessing)
            {
                EditorUtility.DisplayDialog("同名图片合并", "FR2 未能启动合并。请确认缓存已就绪，且选中的都是 Assets 下的图片。", "确定");
                return;
            }

            mMergeRunning = true;
            mPendingDeleteGroup = group;
            mPendingKeepGuid = keepItem.Guid;
            mStatus = "正在用 FR2 改写引用...";
            EditorApplication.update -= WaitMergeThenDelete;
            EditorApplication.update += WaitMergeThenDelete;
        }

        private void WaitMergeThenDelete()
        {
            if (FR2_Export.IsMergeProcessing)
            {
                return;
            }

            EditorApplication.update -= WaitMergeThenDelete;
            try
            {
                DeleteUnusedImages();
            }
            finally
            {
                mMergeRunning = false;
                mPendingDeleteGroup = null;
                mPendingKeepGuid = null;
                Repaint();
            }
        }

        private void DeleteUnusedImages()
        {
            SameNameGroup group = mPendingDeleteGroup;
            if (group == null)
            {
                return;
            }

            List<string> deletePaths = group.Items
                .Where(item => item.Guid != mPendingKeepGuid)
                .Select(item => item.AssetPath)
                .ToList();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in deletePaths)
                {
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

            mGroups.Remove(group);
            mSelectedGroupIndex = -1;
            mStatus = $"已保留一张同名图，删除 {deletePaths.Count} 张未使用图片。";
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

        private sealed class SameNameGroup
        {
            public SameNameGroup(string name)
            {
                Name = name;
                Items = new List<ImageItem>();
            }

            public string Name { get; }
            public List<ImageItem> Items { get; }
        }

        private sealed class ImageItem
        {
            public ImageItem(string guid, string assetPath)
            {
                Guid = guid;
                AssetPath = assetPath;
                RefPaths = new List<string>();
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
                }

                RefPaths.Sort(StringComparer.OrdinalIgnoreCase);
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
