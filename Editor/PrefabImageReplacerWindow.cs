using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Previews Image and RawImage Sprite replacements on a scene object, then optionally saves them to a Prefab.
/// </summary>
public sealed class PrefabImageReplacerWindow : EditorWindow
{
    private const string SessionKey = "PrefabImageReplacerWindow.Session";
    private const string OutputSuffix = "_Replaced";
    private const int AppliedStateVersion = 2;
    private const int SessionVersion = 2;
    private const int PageSize = 30;
    private const float PreviewSize = 44f;
    private const float NativeSizeWarningThreshold = 0.5f;

    [SerializeField] private GameObject targetObject;
    [SerializeField] private string targetDisplayName = string.Empty;
    [SerializeField] private string targetError = string.Empty;
    [SerializeField] private string searchText = string.Empty;
    [SerializeField] private Vector2 scrollPosition;
    [SerializeField] private List<ImageSlot> slots = new List<ImageSlot>();
    [SerializeField] private int currentPage;
    [SerializeField] private bool hasSession;
    [SerializeField] private bool isSaveQueued;
    [SerializeField] private int serializedVersion;

    [NonSerialized] private GUIStyle pathButtonStyle;
    [NonSerialized] private bool isRestoreQueued;

    private GUIStyle PathButtonStyle
    {
        get
        {
            if (pathButtonStyle == null)
            {
                pathButtonStyle = new GUIStyle(EditorStyles.miniButton)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true
                };
            }

            return pathButtonStyle;
        }
    }

    [MenuItem("Tools/Prefab Image Replacer")]
    public static void Open()
    {
        var window = GetWindow<PrefabImageReplacerWindow>("Prefab Image Replacer");
        window.minSize = new Vector2(640f, 420f);
    }

    private void OnEnable()
    {
        minSize = new Vector2(640f, 420f);
        isSaveQueued = false;
        isRestoreQueued = false;
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
        Selection.selectionChanged -= OnSelectionChanged;
        Selection.selectionChanged += OnSelectionChanged;

        if (slots == null)
        {
            slots = new List<ImageSlot>();
        }
        else
        {
            slots = slots.Where(slot => slot != null).ToList();
        }

        if (serializedVersion < AppliedStateVersion)
        {
            foreach (var slot in slots)
            {
                slot.IsReplacementApplied = slot.Replacement != null;
                if (!slot.IsReplacementApplied)
                {
                    slot.UseNativeSize = false;
                }
            }
        }
        else
        {
            foreach (var slot in slots)
            {
                if (slot.Replacement == null || !slot.IsReplacementApplied)
                {
                    slot.UseNativeSize = false;
                }
            }
        }

        serializedVersion = SessionVersion;

        if (!hasSession)
        {
            RestoreSession();
        }
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        Selection.selectionChanged -= OnSelectionChanged;
        PersistSession();
    }

    private void OnUndoRedoPerformed()
    {
        PersistSession();
        Repaint();
    }

    private void OnSelectionChanged()
    {
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("场景图片引用实时替换", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "拖入场景对象后，会按原始图片资源聚合 Image/RawImage 引用。组内换图会立即作用到全部引用；原尺寸和还原仍按物体独立控制。工具不会自动保存场景或 Prefab。",
            MessageType.Info);

        var editingDisabled = EditorApplication.isPlayingOrWillChangePlaymode;
        if (editingDisabled)
        {
            EditorGUILayout.HelpBox("实时替换仅支持编辑模式。退出播放模式后再操作。", MessageType.Warning);
        }

        using (new EditorGUI.DisabledScope(editingDisabled))
        {
            DrawTargetField();
            DrawToolbar();
        }

        if (!string.IsNullOrEmpty(targetError))
        {
            EditorGUILayout.HelpBox(targetError, MessageType.Error);
        }

        if (!hasSession)
        {
            EditorGUILayout.HelpBox("请拖入当前已加载场景中的一个 GameObject。", MessageType.Warning);
            return;
        }

        if (targetObject == null)
        {
            EditorGUILayout.HelpBox(
                "原场景对象已删除或所在场景已卸载。失效槽位会保留并跳过；拖入新对象可开始新的扫描。",
                MessageType.Warning);
        }
        else
        {
            EditorGUILayout.LabelField("扫描对象", targetDisplayName, EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(
                "预览修改会进入 Undo 并使场景变为未保存状态。关闭窗口不会自动还原；若保存场景，当前预览结果也会保存。",
                MessageType.Warning);
        }

        DrawSlotList(editingDisabled);
        DrawSaveControls(editingDisabled);
    }

    private void DrawTargetField()
    {
        EditorGUI.BeginChangeCheck();
        var selected = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("场景对象", "扫描这个场景对象及其全部子节点"),
            targetObject,
            typeof(GameObject),
            true);
        if (!EditorGUI.EndChangeCheck())
        {
            return;
        }

        if (selected == null)
        {
            ClearSession();
            return;
        }

        string error;
        if (!IsValidSceneTarget(selected, out error))
        {
            targetError = error;
            return;
        }

        targetError = string.Empty;
        targetObject = selected;
        targetDisplayName = selected.scene.name + ": " + BuildTransformPath(selected.transform, null);
        hasSession = true;
        RebuildBaseline(false);
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUILayout.LabelField(
                new GUIContent("筛选", "按节点路径、组件类型、图片名或资源路径筛选"),
                EditorStyles.miniLabel,
                GUILayout.Width(32f));

            var newSearch = EditorGUILayout.TextField(
                searchText,
                GUI.skin.FindStyle("ToolbarSeachTextField") ?? GUI.skin.textField,
                GUILayout.ExpandWidth(true));
            if (!string.Equals(newSearch, searchText, StringComparison.Ordinal))
            {
                searchText = newSearch;
                currentPage = 0;
                scrollPosition = Vector2.zero;
            }

            using (new EditorGUI.DisabledScope(targetObject == null))
            {
                if (GUILayout.Button(
                    new GUIContent("重扫并建立基线", "把对象当前状态记录为新的还原基线"),
                    EditorStyles.toolbarButton,
                    GUILayout.Width(104f)))
                {
                    RebuildBaseline(true);
                }
            }

            using (new EditorGUI.DisabledScope(slots.Count == 0 || isRestoreQueued))
            {
                if (GUILayout.Button("还原全部", EditorStyles.toolbarButton, GUILayout.Width(68f)))
                {
                    RequestRestoreAll();
                }
            }
        }
    }

    private void DrawSlotList(bool editingDisabled)
    {
        var groups = BuildImageGroups();
        var filteredGroups = BuildFilteredGroupViews(groups);
        var invalidCount = slots.Count(slot => !slot.IsValid);
        var configuredCount = slots.Count(slot => slot.HasAppliedReplacement);
        EditorGUILayout.LabelField(
            string.Format(
                "图片组：{0}    引用：{1}    已应用：{2}    失效：{3}    当前显示组：{4}",
                groups.Count,
                slots.Count,
                configuredCount,
                invalidCount,
                filteredGroups.Count),
            EditorStyles.miniBoldLabel);

        var pageCount = Mathf.Max(1, Mathf.CeilToInt(filteredGroups.Count / (float)PageSize));
        currentPage = Mathf.Clamp(currentPage, 0, pageCount - 1);
        DrawPagination(pageCount, filteredGroups.Count);

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        var firstIndex = currentPage * PageSize;
        var lastIndex = Mathf.Min(firstIndex + PageSize, filteredGroups.Count);
        using (new EditorGUI.DisabledScope(editingDisabled))
        {
            for (var index = firstIndex; index < lastIndex; index++)
            {
                DrawImageGroup(filteredGroups[index]);
            }
        }

        if (slots.Count == 0 && targetObject != null)
        {
            EditorGUILayout.HelpBox("这个对象的子树中没有找到已设置图片的 Image 或 RawImage。", MessageType.Info);
        }
        else if (filteredGroups.Count == 0)
        {
            EditorGUILayout.HelpBox("没有符合筛选条件的图片组或引用路径。", MessageType.Info);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawPagination(int pageCount, int filteredCount)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(currentPage <= 0))
            {
                if (GUILayout.Button("上一页", GUILayout.Width(58f)))
                {
                    currentPage--;
                    scrollPosition = Vector2.zero;
                }
            }

            EditorGUILayout.LabelField(
                string.Format("第 {0}/{1} 页，每页 {2} 组，共 {3} 组", currentPage + 1, pageCount, PageSize, filteredCount),
                EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(currentPage >= pageCount - 1))
            {
                if (GUILayout.Button("下一页", GUILayout.Width(58f)))
                {
                    currentPage++;
                    scrollPosition = Vector2.zero;
                }
            }
        }
    }

    private void DrawImageGroup(ImageGroupView view)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            var group = view.Group;
            var nativeWarningCount = CountNativeSizeWarnings(group.Slots);
            var title = string.Format(
                "{0}    引用 {1}    已应用 {2}    尺寸警告 {3}",
                group.OriginalName,
                group.Slots.Count,
                group.AppliedCount,
                nativeWarningCount);
            EditorGUILayout.LabelField(new GUIContent(title, title), EditorStyles.boldLabel);

            EditorGUILayout.LabelField(
                new GUIContent("原图资源：" + group.SourcePath, group.SourcePath),
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPreview(group.OriginalSource, "点击定位原图资源");
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField("原图分辨率", group.OriginalResolutionText, EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("替换图分辨率", group.ReplacementResolutionText, EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                EditorGUI.BeginChangeCheck();
                var replacement = (Sprite)EditorGUILayout.ObjectField(
                    new GUIContent("替换 Sprite"),
                    group.Replacement,
                    typeof(Sprite),
                    false,
                    GUILayout.Width(230f));
                if (EditorGUI.EndChangeCheck())
                {
                    ChangeGroupReplacement(group, replacement);
                }

                if (group.Replacement != null)
                {
                    DrawPreview(group.Replacement, "点击定位替换图资源");
                }
            }

            foreach (var warning in group.Warnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            var isExpanded = view.ForceExpanded || group.IsExpanded;
            using (new EditorGUILayout.HorizontalScope())
            {
                var usageLabel = view.ForceExpanded
                    ? string.Format("匹配 {0} / 共 {1}", view.VisibleSlots.Count, group.Slots.Count)
                    : string.Format("使用位置（{0}）", group.Slots.Count);
                using (new EditorGUI.DisabledScope(view.ForceExpanded))
                {
                    var newExpanded = EditorGUILayout.Foldout(
                        isExpanded,
                        usageLabel,
                        true,
                        EditorStyles.foldout);
                    if (!view.ForceExpanded && newExpanded != group.IsExpanded)
                    {
                        SetGroupExpanded(group, newExpanded);
                        isExpanded = newExpanded;
                    }
                }

                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(group.Replacement == null || isRestoreQueued))
                {
                    if (GUILayout.Button("还原本组", GUILayout.Width(80f)))
                    {
                        RequestGroupRestore(group);
                    }
                }
            }

            if (!isExpanded)
            {
                return;
            }

            EditorGUILayout.Space(2f);
            foreach (var slot in view.VisibleSlots)
            {
                DrawUsageSlot(slot);
            }
        }
    }

    private void DrawUsageSlot(ImageSlot slot)
    {
        var isSelected = slot.IsValid && Selection.activeGameObject == slot.Component.gameObject;
        var previousBackgroundColor = GUI.backgroundColor;
        try
        {
            if (isSelected)
            {
                GUI.backgroundColor = new Color(0.45f, 0.72f, 1f);
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (!slot.IsValid)
                {
                    EditorGUILayout.LabelField(slot.Path + "  [" + slot.ComponentLabel + "]", EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.HelpBox("该组件或节点已经失效。重新扫描后会从列表中清理。", MessageType.Warning);
                }
                else
                {
                    var title = slot.Path + "  [" + slot.ComponentLabel + "]";
                    if (GUILayout.Button(new GUIContent(title, "点击选中物体并在 Scene 视图中定位"), PathButtonStyle))
                    {
                        SelectAndFrame(slot);
                    }

                    Vector2 currentSize;
                    Vector2 nativeSize;
                    if (TryGetNativeSizeMismatch(slot, out currentSize, out nativeSize))
                    {
                        EditorGUILayout.HelpBox(
                            string.Format(
                                "警告：当前尺寸 {0}，Unity Native Size {1}，宽或高的差值超过 {2:0.##}。",
                                FormatSize(currentSize),
                                FormatSize(nativeSize),
                                NativeSizeWarningThreshold),
                            MessageType.Warning);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(
                            slot.IsReplacementApplied ? "状态：已应用组替换" : "状态：原图 / 已还原",
                            EditorStyles.miniLabel);

                        GUILayout.FlexibleSpace();
                        using (new EditorGUI.DisabledScope(!slot.HasAppliedReplacement))
                        {
                            EditorGUI.BeginChangeCheck();
                            var useNativeSize = EditorGUILayout.ToggleLeft(
                                new GUIContent("Set Native Size", "勾选后立即按替换图片设置原尺寸；取消后恢复该物体的基线布局"),
                                slot.UseNativeSize,
                                GUILayout.Width(130f));
                            if (EditorGUI.EndChangeCheck())
                            {
                                ChangeNativeSize(slot, useNativeSize);
                            }
                        }

                        if (GUILayout.Button("还原", GUILayout.Width(58f)))
                        {
                            RestoreSlot(slot);
                        }
                    }
                }
            }
        }
        finally
        {
            GUI.backgroundColor = previousBackgroundColor;
        }
    }

    private void DrawSaveControls(bool editingDisabled)
    {
        string sourcePath;
        var hasSourcePrefab = TryGetModifiableSourcePrefabPath(out sourcePath);
        var hasAppliedReplacement = slots.Any(slot => slot.HasAppliedReplacement);
        if (hasSourcePrefab)
        {
            EditorGUILayout.LabelField("源 Prefab", sourcePath, EditorStyles.miniLabel);
            if (!hasAppliedReplacement)
            {
                EditorGUILayout.HelpBox("修改源 Prefab 不可用：当前没有已应用的替换。", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "仅写入当前仍应用组替换的图片引用和原尺寸结果。",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }
        else if (targetObject == null)
        {
            EditorGUILayout.HelpBox("修改源 Prefab 不可用：请先选择有效的场景对象。", MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "修改源 Prefab 不可用：扫描对象必须是 Prefab 实例根节点。",
                MessageType.Warning);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            var canCreate = targetObject != null && !isSaveQueued;
            using (new EditorGUI.DisabledScope(editingDisabled || !canCreate))
            {
                if (GUILayout.Button("生成新 Prefab", GUILayout.Width(150f), GUILayout.Height(28f)))
                {
                    QueueSave(SaveMode.CreateNew);
                }
            }

            GUILayout.Space(4f);
            var canModify = targetObject != null
                && !isSaveQueued
                && hasSourcePrefab
                && hasAppliedReplacement;
            using (new EditorGUI.DisabledScope(editingDisabled || !canModify))
            {
                if (GUILayout.Button("修改源 Prefab", GUILayout.Width(150f), GUILayout.Height(28f)))
                {
                    QueueSave(SaveMode.ModifySource);
                }
            }
        }
    }

    private List<ImageGroup> BuildImageGroups()
    {
        var groups = new List<ImageGroup>();
        var groupsBySource = new Dictionary<UnityEngine.Object, ImageGroup>();
        foreach (var slot in slots.Where(slot => slot.OriginalSource != null))
        {
            ImageGroup group;
            if (!groupsBySource.TryGetValue(slot.OriginalSource, out group))
            {
                group = new ImageGroup(slot.OriginalSource);
                groupsBySource.Add(slot.OriginalSource, group);
                groups.Add(group);
            }

            group.Slots.Add(slot);
        }

        return groups
            .OrderBy(group => group.OriginalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<ImageGroupView> BuildFilteredGroupViews(List<ImageGroup> groups)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return groups.Select(group => new ImageGroupView(group, group.Slots, false)).ToList();
        }

        var query = searchText.Trim();
        var views = new List<ImageGroupView>();
        foreach (var group in groups)
        {
            if (group.OriginalName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || group.SourcePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                views.Add(new ImageGroupView(group, group.Slots, false));
                continue;
            }

            var matchingSlots = group.Slots
                .Where(slot => slot.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || slot.ComponentLabel.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (matchingSlots.Count > 0)
            {
                views.Add(new ImageGroupView(group, matchingSlots, true));
            }
        }

        return views;
    }

    private void SetGroupExpanded(ImageGroup group, bool isExpanded)
    {
        foreach (var slot in group.Slots)
        {
            slot.GroupExpanded = isExpanded;
        }

        PersistSession();
    }

    private void ChangeGroupReplacement(ImageGroup group, Sprite replacement)
    {
        if (group.Slots.Any(slot => slot.Kind == SlotKind.RawImage)
            && replacement != null
            && !IsWholeTextureSprite(replacement))
        {
            foreach (var slot in group.Slots)
            {
                slot.Warning = "RawImage 只接受未打包且覆盖完整纹理的 Sprite；图集子 Sprite 不会被应用。";
            }

            PersistSession();
            Repaint();
            return;
        }

        var actionName = replacement == null ? "还原图片组" : "预览替换图片组";
        Undo.IncrementCurrentGroup();
        var undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(actionName);
        Undo.RecordObject(this, actionName);
        var objects = group.Slots
            .Where(slot => slot.IsValid)
            .SelectMany(slot => new UnityEngine.Object[] { slot.Component, slot.RectTransform })
            .Where(item => item != null)
            .Distinct()
            .ToArray();
        if (objects.Length > 0)
        {
            Undo.RecordObjects(objects, actionName);
        }

        foreach (var slot in group.Slots)
        {
            slot.Warning = string.Empty;
            slot.Replacement = replacement;
            slot.IsReplacementApplied = replacement != null && slot.IsValid;
            if (replacement == null)
            {
                slot.UseNativeSize = false;
                if (slot.IsValid)
                {
                    RestoreSlotState(slot);
                }
            }
            else if (slot.IsValid)
            {
                ApplyPreviewState(slot);
            }

            if (slot.IsValid)
            {
                MarkSceneObjectsChanged(slot);
            }
        }

        EditorUtility.SetDirty(this);
        Undo.CollapseUndoOperations(undoGroup);
        PersistSession();
        Repaint();
    }

    private void ChangeNativeSize(ImageSlot slot, bool useNativeSize)
    {
        if (!slot.HasAppliedReplacement)
        {
            return;
        }

        var undoGroup = BeginSlotUndo(slot, useNativeSize ? "设置图片原尺寸" : "恢复图片布局");
        slot.UseNativeSize = useNativeSize;
        ApplyPreviewState(slot);
        FinishSlotChange(slot, undoGroup);
    }

    private void RestoreSlot(ImageSlot slot)
    {
        if (!slot.IsValid)
        {
            return;
        }

        var undoGroup = BeginSlotUndo(slot, "还原图片槽位");
        slot.IsReplacementApplied = false;
        slot.UseNativeSize = false;
        slot.Warning = string.Empty;
        RestoreSlotState(slot);
        FinishSlotChange(slot, undoGroup);
    }

    private void RequestGroupRestore(ImageGroup group)
    {
        if (isRestoreQueued || group == null || group.Replacement == null)
        {
            return;
        }

        var slotsAtRequest = slots;
        var sourceAtRequest = group.OriginalSource;
        isRestoreQueued = true;
        EditorApplication.delayCall += () =>
        {
            if (this == null)
            {
                return;
            }

            isRestoreQueued = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode
                || !hasSession
                || !ReferenceEquals(slots, slotsAtRequest))
            {
                Repaint();
                return;
            }

            var currentGroup = BuildImageGroups()
                .FirstOrDefault(item => item.OriginalSource == sourceAtRequest);
            if (currentGroup == null || currentGroup.Replacement == null)
            {
                Repaint();
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "还原图片组",
                    string.Format("确定还原“{0}”的全部 {1} 个使用位置吗？", currentGroup.OriginalName, currentGroup.Slots.Count),
                    "还原",
                    "取消"))
            {
                Repaint();
                return;
            }

            ChangeGroupReplacement(currentGroup, null);
        };
        GUIUtility.ExitGUI();
    }

    private void RequestRestoreAll()
    {
        if (isRestoreQueued || slots.Count == 0)
        {
            return;
        }

        var slotsAtRequest = slots;
        isRestoreQueued = true;
        EditorApplication.delayCall += () =>
        {
            if (this == null)
            {
                return;
            }

            isRestoreQueued = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode
                || !hasSession
                || !ReferenceEquals(slots, slotsAtRequest)
                || slots.Count == 0)
            {
                Repaint();
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "还原全部图片",
                    string.Format("确定还原当前会话中的全部 {0} 个使用位置吗？", slots.Count),
                    "还原全部",
                    "取消"))
            {
                Repaint();
                return;
            }

            RestoreAllSlots();
        };
        GUIUtility.ExitGUI();
    }

    private void RestoreAllSlots()
    {
        Undo.IncrementCurrentGroup();
        var undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("还原全部图片槽位");
        Undo.RecordObject(this, "还原全部图片槽位");

        var objects = slots
            .Where(slot => slot.IsValid)
            .SelectMany(slot => new UnityEngine.Object[] { slot.Component, slot.RectTransform })
            .Where(item => item != null)
            .Distinct()
            .ToArray();
        if (objects.Length > 0)
        {
            Undo.RecordObjects(objects, "还原全部图片槽位");
        }

        foreach (var slot in slots)
        {
            slot.Replacement = null;
            slot.IsReplacementApplied = false;
            slot.UseNativeSize = false;
            slot.Warning = string.Empty;
            if (slot.IsValid)
            {
                RestoreSlotState(slot);
                MarkSceneObjectsChanged(slot);
            }
        }

        EditorUtility.SetDirty(this);
        Undo.CollapseUndoOperations(undoGroup);
        PersistSession();
        Repaint();
    }

    private int BeginSlotUndo(ImageSlot slot, string actionName)
    {
        Undo.IncrementCurrentGroup();
        var undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(actionName);
        Undo.RecordObject(this, actionName);
        Undo.RecordObject(slot.Component, actionName);
        Undo.RecordObject(slot.RectTransform, actionName);
        return undoGroup;
    }

    private void FinishSlotChange(ImageSlot slot, int undoGroup)
    {
        MarkSceneObjectsChanged(slot);
        EditorUtility.SetDirty(this);
        Undo.CollapseUndoOperations(undoGroup);
        PersistSession();
        Repaint();
    }

    private static void ApplyPreviewState(ImageSlot slot)
    {
        slot.OriginalRect.Apply(slot.RectTransform);
        if (slot.Kind == SlotKind.Image)
        {
            var image = (Image)slot.Component;
            image.sprite = slot.Replacement;
            if (slot.UseNativeSize)
            {
                image.SetNativeSize();
            }
        }
        else
        {
            var rawImage = (RawImage)slot.Component;
            rawImage.texture = slot.Replacement == null ? null : slot.Replacement.texture;
            if (slot.UseNativeSize)
            {
                rawImage.SetNativeSize();
            }
        }
    }

    private static void RestoreSlotState(ImageSlot slot)
    {
        slot.OriginalRect.Apply(slot.RectTransform);
        if (slot.Kind == SlotKind.Image)
        {
            ((Image)slot.Component).sprite = slot.OriginalSource as Sprite;
        }
        else
        {
            ((RawImage)slot.Component).texture = slot.OriginalSource as Texture;
        }
    }

    private static void MarkSceneObjectsChanged(ImageSlot slot)
    {
        EditorUtility.SetDirty(slot.Component);
        EditorUtility.SetDirty(slot.RectTransform);
        PrefabUtility.RecordPrefabInstancePropertyModifications(slot.Component);
        PrefabUtility.RecordPrefabInstancePropertyModifications(slot.RectTransform);
        var scene = slot.Component.gameObject.scene;
        if (scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    private void RebuildBaseline(bool preserveGroupExpansion)
    {
        if (targetObject == null)
        {
            return;
        }

        string error;
        if (!IsValidSceneTarget(targetObject, out error))
        {
            targetError = error;
            return;
        }

        targetError = string.Empty;
        targetDisplayName = targetObject.scene.name + ": " + BuildTransformPath(targetObject.transform, null);
        var expandedBySource = preserveGroupExpansion
            ? slots.Where(slot => slot.OriginalSource != null)
                .GroupBy(slot => slot.OriginalSource)
                .ToDictionary(group => group.Key, group => group.Any(slot => slot.GroupExpanded))
            : new Dictionary<UnityEngine.Object, bool>();
        var newSlots = new List<ImageSlot>();

        foreach (var image in targetObject.GetComponentsInChildren<Image>(true))
        {
            if (image.sprite != null)
            {
                newSlots.Add(CreateSlot(image, SlotKind.Image, expandedBySource));
            }
        }

        foreach (var rawImage in targetObject.GetComponentsInChildren<RawImage>(true))
        {
            if (rawImage.texture != null)
            {
                newSlots.Add(CreateSlot(rawImage, SlotKind.RawImage, expandedBySource));
            }
        }

        slots = newSlots
            .OrderBy(slot => slot.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(slot => slot.Kind)
            .ThenBy(slot => slot.ComponentIndex)
            .ToList();
        currentPage = 0;
        scrollPosition = Vector2.zero;
        hasSession = true;
        PersistSession();
        Repaint();
    }

    private ImageSlot CreateSlot(
        Component component,
        SlotKind kind,
        Dictionary<UnityEngine.Object, bool> expandedBySource)
    {
        var originalSource = kind == SlotKind.Image
            ? (UnityEngine.Object)((Image)component).sprite
            : ((RawImage)component).texture;
        var components = kind == SlotKind.Image
            ? component.gameObject.GetComponents<Image>().Cast<Component>().ToArray()
            : component.gameObject.GetComponents<RawImage>().Cast<Component>().ToArray();

        bool groupExpanded;
        expandedBySource.TryGetValue(originalSource, out groupExpanded);
        return new ImageSlot
        {
            Component = component,
            Kind = kind,
            Path = BuildTransformPath(component.transform, targetObject.transform),
            OriginalSource = originalSource,
            OriginalRect = RectTransformSnapshot.Capture((RectTransform)component.transform),
            Locator = BuildTransformLocator(component.transform, targetObject.transform),
            ComponentIndex = Array.IndexOf(components, component),
            Replacement = null,
            IsReplacementApplied = false,
            UseNativeSize = false,
            GroupExpanded = groupExpanded,
            Warning = string.Empty
        };
    }

    private void QueueSave(SaveMode mode)
    {
        if (isSaveQueued)
        {
            return;
        }

        var targetAtRequest = targetObject;
        var slotsAtRequest = slots;
        isSaveQueued = true;
        EditorApplication.delayCall += () =>
        {
            if (this == null)
            {
                return;
            }

            isSaveQueued = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode
                || !hasSession
                || targetObject != targetAtRequest
                || !ReferenceEquals(slots, slotsAtRequest))
            {
                Repaint();
                return;
            }

            if (mode == SaveMode.ModifySource)
            {
                SaveConfiguredChangesToSourcePrefab();
            }
            else
            {
                SaveCurrentSubtreeAsNewPrefab();
            }

            Repaint();
        };
        GUIUtility.ExitGUI();
    }

    private void SaveCurrentSubtreeAsNewPrefab()
    {
        if (targetObject == null)
        {
            return;
        }

        var outputPath = EditorUtility.SaveFilePanelInProject(
            "保存新 Prefab",
            targetObject.name + OutputSuffix,
            "prefab",
            "选择新 Prefab 的保存位置");
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        bool succeeded;
        var savedPrefab = PrefabUtility.SaveAsPrefabAsset(targetObject, outputPath, out succeeded);
        if (!succeeded || savedPrefab == null)
        {
            EditorUtility.DisplayDialog("保存失败", "Unity 未能保存新 Prefab。请检查路径和 Console。", "确定");
            return;
        }

        Selection.activeObject = savedPrefab;
        EditorGUIUtility.PingObject(savedPrefab);
        EditorUtility.DisplayDialog("保存完成", "已生成：" + outputPath, "确定");
    }

    private void SaveConfiguredChangesToSourcePrefab()
    {
        string sourcePath;
        if (!TryGetModifiableSourcePrefabPath(out sourcePath))
        {
            EditorUtility.DisplayDialog("无法修改", "拖入对象不是 Prefab 实例根节点。", "确定");
            return;
        }

        var configuredSlots = slots
            .Where(slot => slot.HasAppliedReplacement)
            .ToList();
        if (configuredSlots.Count == 0)
        {
            EditorUtility.DisplayDialog("没有修改", "当前没有仍在应用的有效替换 Sprite。", "确定");
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "确认修改源 Prefab",
                "将只写入当前仍应用组替换的图片引用与原尺寸结果：\n" + sourcePath,
                "修改",
                "取消"))
        {
            return;
        }

        GameObject prefabRoot = null;
        try
        {
            prefabRoot = PrefabUtility.LoadPrefabContents(sourcePath);
            var appliedCount = 0;
            var skippedCount = 0;
            var wasCanceled = false;
            for (var slotIndex = 0; slotIndex < configuredSlots.Count; slotIndex++)
            {
                var slot = configuredSlots[slotIndex];
                if (EditorUtility.DisplayCancelableProgressBar(
                        "修改源 Prefab",
                        slot.Path,
                        slotIndex / (float)configuredSlots.Count))
                {
                    wasCanceled = true;
                    break;
                }

                var targetTransform = FindTransform(prefabRoot.transform, slot.Locator);
                var targetComponent = FindComponent(targetTransform, slot.Kind, slot.ComponentIndex);
                if (targetComponent == null)
                {
                    skippedCount++;
                    continue;
                }

                if (slot.Kind == SlotKind.Image)
                {
                    var image = (Image)targetComponent;
                    image.sprite = slot.Replacement;
                    if (slot.UseNativeSize)
                    {
                        image.SetNativeSize();
                        EditorUtility.SetDirty(image.rectTransform);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(image.rectTransform);
                    }
                }
                else
                {
                    var rawImage = (RawImage)targetComponent;
                    rawImage.texture = slot.Replacement.texture;
                    if (slot.UseNativeSize)
                    {
                        rawImage.SetNativeSize();
                        EditorUtility.SetDirty(rawImage.rectTransform);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(rawImage.rectTransform);
                    }
                }

                EditorUtility.SetDirty(targetComponent);
                PrefabUtility.RecordPrefabInstancePropertyModifications(targetComponent);
                appliedCount++;
            }

            if (wasCanceled)
            {
                EditorUtility.DisplayDialog("操作已取消", "源 Prefab 未保存。", "确定");
                return;
            }

            bool succeeded;
            var savedPrefab = PrefabUtility.SaveAsPrefabAsset(prefabRoot, sourcePath, out succeeded);
            if (!succeeded || savedPrefab == null)
            {
                throw new InvalidOperationException("Unity 未能保存源 Prefab。");
            }

            Selection.activeObject = savedPrefab;
            EditorGUIUtility.PingObject(savedPrefab);
            EditorUtility.DisplayDialog(
                "修改完成",
                string.Format("源 Prefab：{0}\n已写入槽位：{1}\n定位失败并跳过：{2}", sourcePath, appliedCount, skippedCount),
                "确定");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("修改失败", exception.Message, "确定");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (prefabRoot != null)
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }
    }

    private bool TryGetModifiableSourcePrefabPath(out string sourcePath)
    {
        sourcePath = string.Empty;
        if (targetObject == null || !PrefabUtility.IsPartOfPrefabInstance(targetObject))
        {
            return false;
        }

        var instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(targetObject);
        if (instanceRoot != targetObject)
        {
            return false;
        }

        sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(targetObject);
        return !string.IsNullOrEmpty(sourcePath);
    }

    private static Component FindComponent(Transform transform, SlotKind kind, int componentIndex)
    {
        if (transform == null || componentIndex < 0)
        {
            return null;
        }

        if (kind == SlotKind.Image)
        {
            var images = transform.GetComponents<Image>();
            return componentIndex < images.Length ? images[componentIndex] : null;
        }

        var rawImages = transform.GetComponents<RawImage>();
        return componentIndex < rawImages.Length ? rawImages[componentIndex] : null;
    }

    private static Transform FindTransform(Transform root, List<TransformLocatorStep> locator)
    {
        var current = root;
        foreach (var step in locator)
        {
            var sameNameIndex = 0;
            Transform match = null;
            for (var childIndex = 0; childIndex < current.childCount; childIndex++)
            {
                var child = current.GetChild(childIndex);
                if (!string.Equals(child.name, step.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (sameNameIndex == step.SameNameIndex)
                {
                    match = child;
                    break;
                }

                sameNameIndex++;
            }

            if (match == null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }

    private static List<TransformLocatorStep> BuildTransformLocator(Transform transform, Transform root)
    {
        var reversed = new List<TransformLocatorStep>();
        var current = transform;
        while (current != null && current != root)
        {
            var sameNameIndex = 0;
            var parent = current.parent;
            if (parent != null)
            {
                for (var index = 0; index < current.GetSiblingIndex(); index++)
                {
                    if (string.Equals(parent.GetChild(index).name, current.name, StringComparison.Ordinal))
                    {
                        sameNameIndex++;
                    }
                }
            }

            reversed.Add(new TransformLocatorStep { Name = current.name, SameNameIndex = sameNameIndex });
            current = parent;
        }

        reversed.Reverse();
        return reversed;
    }

    private static bool IsValidSceneTarget(GameObject candidate, out string error)
    {
        if (candidate == null)
        {
            error = "场景对象为空。";
            return false;
        }

        if (EditorUtility.IsPersistent(candidate))
        {
            error = "这里只接受场景中的对象实例，不能拖入 Project 里的 Prefab 资产。";
            return false;
        }

        if (!candidate.scene.IsValid() || !candidate.scene.isLoaded)
        {
            error = "对象不属于当前已加载场景。";
            return false;
        }

        if (EditorSceneManager.IsPreviewSceneObject(candidate))
        {
            error = "不支持 Preview Scene 或 Prefab Stage 中的对象，请拖入普通场景对象。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsWholeTextureSprite(Sprite sprite)
    {
        if (sprite == null || sprite.texture == null || sprite.packed)
        {
            return false;
        }

        var rect = sprite.rect;
        return Mathf.Approximately(rect.x, 0f)
            && Mathf.Approximately(rect.y, 0f)
            && Mathf.Approximately(rect.width, sprite.texture.width)
            && Mathf.Approximately(rect.height, sprite.texture.height);
    }

    private static int CountNativeSizeWarnings(IEnumerable<ImageSlot> groupSlots)
    {
        var warningCount = 0;
        foreach (var slot in groupSlots)
        {
            Vector2 currentSize;
            Vector2 nativeSize;
            if (TryGetNativeSizeMismatch(slot, out currentSize, out nativeSize))
            {
                warningCount++;
            }
        }

        return warningCount;
    }

    private static bool TryGetNativeSizeMismatch(ImageSlot slot, out Vector2 currentSize, out Vector2 nativeSize)
    {
        currentSize = Vector2.zero;
        nativeSize = Vector2.zero;
        if (!slot.IsValid)
        {
            return false;
        }

        currentSize = slot.RectTransform.rect.size;
        if (slot.Kind == SlotKind.Image)
        {
            var image = (Image)slot.Component;
            var activeSprite = image.overrideSprite != null ? image.overrideSprite : image.sprite;
            if (activeSprite == null || image.pixelsPerUnit <= 0f)
            {
                return false;
            }

            nativeSize = activeSprite.rect.size / image.pixelsPerUnit;
        }
        else
        {
            var rawImage = (RawImage)slot.Component;
            var texture = rawImage.mainTexture;
            if (texture == null)
            {
                return false;
            }

            nativeSize = new Vector2(
                Mathf.RoundToInt(texture.width * rawImage.uvRect.width),
                Mathf.RoundToInt(texture.height * rawImage.uvRect.height));
        }

        return Mathf.Abs(currentSize.x - nativeSize.x) > NativeSizeWarningThreshold
            || Mathf.Abs(currentSize.y - nativeSize.y) > NativeSizeWarningThreshold;
    }

    private static string FormatSize(Vector2 size)
    {
        return string.Format("{0:0.##} x {1:0.##}", size.x, size.y);
    }

    private static void SelectAndFrame(ImageSlot slot)
    {
        if (!slot.IsValid)
        {
            return;
        }

        Selection.activeGameObject = slot.Component.gameObject;
        if (SceneView.lastActiveSceneView != null)
        {
            SceneView.lastActiveSceneView.FrameSelected();
        }
    }

    private static void DrawPreview(UnityEngine.Object source, string tooltip)
    {
        var previewRect = GUILayoutUtility.GetRect(
            PreviewSize,
            PreviewSize,
            GUILayout.Width(PreviewSize),
            GUILayout.Height(PreviewSize));
        EditorGUI.DrawRect(previewRect, new Color(0.16f, 0.16f, 0.16f));

        var sprite = source as Sprite;
        if (sprite != null && sprite.texture != null && sprite.rect.height > 0f)
        {
            var uv = GetSpriteUvRect(sprite, sprite.texture);
            var fittedRect = GetAspectFittedRect(previewRect, sprite.rect.width / sprite.rect.height);
            GUI.DrawTextureWithTexCoords(fittedRect, sprite.texture, uv, true);
        }
        else
        {
            var texture = source as Texture;
            if (texture != null && texture.width > 0 && texture.height > 0)
            {
                var fittedRect = GetAspectFittedRect(previewRect, (float)texture.width / texture.height);
                EditorGUI.DrawPreviewTexture(fittedRect, texture, null, ScaleMode.ScaleToFit);
            }
        }

        if (source != null && GUI.Button(previewRect, new GUIContent(string.Empty, tooltip), GUIStyle.none))
        {
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = source;
            EditorGUIUtility.PingObject(source);
        }
    }

    private static Rect GetSpriteUvRect(Sprite sprite, Texture texture)
    {
        if (!sprite.packed)
        {
            return new Rect(
                sprite.rect.x / texture.width,
                sprite.rect.y / texture.height,
                sprite.rect.width / texture.width,
                sprite.rect.height / texture.height);
        }

        var uvs = sprite.uv;
        if (uvs == null || uvs.Length == 0)
        {
            return new Rect(0f, 0f, 1f, 1f);
        }

        var minX = uvs[0].x;
        var maxX = uvs[0].x;
        var minY = uvs[0].y;
        var maxY = uvs[0].y;
        for (var index = 1; index < uvs.Length; index++)
        {
            minX = Mathf.Min(minX, uvs[index].x);
            maxX = Mathf.Max(maxX, uvs[index].x);
            minY = Mathf.Min(minY, uvs[index].y);
            maxY = Mathf.Max(maxY, uvs[index].y);
        }

        var uv = Rect.MinMaxRect(minX, minY, maxX, maxY);
        switch (sprite.packingRotation)
        {
            case SpritePackingRotation.FlipHorizontal:
                uv.x += uv.width;
                uv.width = -uv.width;
                break;
            case SpritePackingRotation.FlipVertical:
                uv.y += uv.height;
                uv.height = -uv.height;
                break;
            case SpritePackingRotation.Rotate180:
                uv.x += uv.width;
                uv.y += uv.height;
                uv.width = -uv.width;
                uv.height = -uv.height;
                break;
        }

        return uv;
    }

    private static Rect GetAspectFittedRect(Rect container, float aspect)
    {
        if (aspect <= 0f || float.IsNaN(aspect) || float.IsInfinity(aspect))
        {
            return container;
        }

        var containerAspect = container.width / container.height;
        if (aspect > containerAspect)
        {
            var height = container.width / aspect;
            return new Rect(container.x, container.y + (container.height - height) * 0.5f, container.width, height);
        }

        var width = container.height * aspect;
        return new Rect(container.x + (container.width - width) * 0.5f, container.y, width, container.height);
    }

    private void ClearSession()
    {
        targetObject = null;
        targetDisplayName = string.Empty;
        targetError = string.Empty;
        slots = new List<ImageSlot>();
        currentPage = 0;
        scrollPosition = Vector2.zero;
        hasSession = false;
        SessionState.EraseString(SessionKey);
        Repaint();
    }

    private void PersistSession()
    {
        if (!hasSession)
        {
            SessionState.EraseString(SessionKey);
            return;
        }

        var data = new SessionData
        {
            Version = SessionVersion,
            HasSession = true,
            Target = ObjectReferenceData.Capture(targetObject),
            TargetDisplayName = targetDisplayName,
            SearchText = searchText,
            CurrentPage = currentPage,
            Slots = slots.Select(PersistedSlotData.Capture).ToList()
        };
        serializedVersion = SessionVersion;
        SessionState.SetString(SessionKey, JsonUtility.ToJson(data));
    }

    private void RestoreSession()
    {
        var json = SessionState.GetString(SessionKey, string.Empty);
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            var data = JsonUtility.FromJson<SessionData>(json);
            if (data == null || !data.HasSession)
            {
                return;
            }

            hasSession = true;
            serializedVersion = SessionVersion;
            targetObject = data.Target == null ? null : data.Target.Resolve() as GameObject;
            targetDisplayName = data.TargetDisplayName ?? string.Empty;
            searchText = data.SearchText ?? string.Empty;
            currentPage = data.CurrentPage;
            slots = data.Slots == null
                ? new List<ImageSlot>()
                : data.Slots.Where(item => item != null)
                    .Select(item => item.Restore(data.Version))
                    .Where(slot => slot.OriginalSource != null)
                    .ToList();
            foreach (var slot in slots)
            {
                var componentTransform = slot.Component == null ? null : slot.Component.transform;
                if (targetObject == null
                    || componentTransform == null
                    || (componentTransform != targetObject.transform && !componentTransform.IsChildOf(targetObject.transform)))
                {
                    slot.Component = null;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Prefab Image Replacer 无法恢复上次会话：" + exception.Message);
            ClearSession();
        }
    }

    private static string BuildTransformPath(Transform transform, Transform root)
    {
        var names = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            names.Push(current.name);
            if (current == root)
            {
                break;
            }

            current = current.parent;
        }

        return string.Join("/", names.ToArray());
    }

    private static Vector2Int GetResolution(UnityEngine.Object source, SlotKind kind)
    {
        if (source == null)
        {
            return Vector2Int.zero;
        }

        var sprite = source as Sprite;
        if (sprite != null)
        {
            if (kind == SlotKind.Image)
            {
                return new Vector2Int(Mathf.RoundToInt(sprite.rect.width), Mathf.RoundToInt(sprite.rect.height));
            }

            return sprite.texture == null
                ? Vector2Int.zero
                : new Vector2Int(sprite.texture.width, sprite.texture.height);
        }

        var texture = source as Texture;
        return texture == null ? Vector2Int.zero : new Vector2Int(texture.width, texture.height);
    }

    private static string FormatResolution(Vector2Int resolution)
    {
        return resolution == Vector2Int.zero
            ? "未知"
            : string.Format("{0} x {1}", resolution.x, resolution.y);
    }

    private enum SaveMode
    {
        CreateNew,
        ModifySource
    }

    private enum SlotKind
    {
        Image,
        RawImage
    }

    [Serializable]
    private sealed class ImageSlot
    {
        public Component Component;
        public SlotKind Kind;
        public string Path;
        public UnityEngine.Object OriginalSource;
        public RectTransformSnapshot OriginalRect;
        public List<TransformLocatorStep> Locator = new List<TransformLocatorStep>();
        public int ComponentIndex;
        public Sprite Replacement;
        public bool IsReplacementApplied;
        public bool UseNativeSize;
        public bool GroupExpanded;
        public string Warning;

        public bool IsValid
        {
            get
            {
                return Component != null
                    && RectTransform != null
                    && ((Kind == SlotKind.Image && Component is Image)
                        || (Kind == SlotKind.RawImage && Component is RawImage));
            }
        }

        public RectTransform RectTransform
        {
            get { return Component == null ? null : Component.transform as RectTransform; }
        }

        public bool HasAppliedReplacement
        {
            get { return IsValid && IsReplacementApplied && Replacement != null; }
        }

        public string ComponentLabel
        {
            get { return Kind == SlotKind.Image ? "Image" : "RawImage"; }
        }

    }

    private sealed class ImageGroup
    {
        public readonly UnityEngine.Object OriginalSource;
        public readonly List<ImageSlot> Slots = new List<ImageSlot>();

        public ImageGroup(UnityEngine.Object originalSource)
        {
            OriginalSource = originalSource;
        }

        public string OriginalName
        {
            get { return OriginalSource == null ? "<未设置>" : OriginalSource.name; }
        }

        public string SourcePath
        {
            get
            {
                var path = AssetDatabase.GetAssetPath(OriginalSource);
                return string.IsNullOrEmpty(path) ? "（非项目资源）" : path;
            }
        }

        public Sprite Replacement
        {
            get { return Slots.Select(slot => slot.Replacement).FirstOrDefault(item => item != null); }
        }

        public int AppliedCount
        {
            get { return Slots.Count(slot => slot.HasAppliedReplacement); }
        }

        public bool IsExpanded
        {
            get { return Slots.Any(slot => slot.GroupExpanded); }
        }

        public string OriginalResolutionText
        {
            get
            {
                var kind = Slots.Count == 0 ? SlotKind.Image : Slots[0].Kind;
                return FormatResolution(GetResolution(OriginalSource, kind));
            }
        }

        public string ReplacementResolutionText
        {
            get
            {
                var replacement = Replacement;
                var kind = Slots.Count == 0 ? SlotKind.Image : Slots[0].Kind;
                return replacement == null ? "未配置" : FormatResolution(GetResolution(replacement, kind));
            }
        }

        public IEnumerable<string> Warnings
        {
            get
            {
                return Slots.Select(slot => slot.Warning)
                    .Where(warning => !string.IsNullOrEmpty(warning))
                    .Distinct();
            }
        }
    }

    private sealed class ImageGroupView
    {
        public readonly ImageGroup Group;
        public readonly List<ImageSlot> VisibleSlots;
        public readonly bool ForceExpanded;

        public ImageGroupView(ImageGroup group, IEnumerable<ImageSlot> visibleSlots, bool forceExpanded)
        {
            Group = group;
            VisibleSlots = visibleSlots.ToList();
            ForceExpanded = forceExpanded;
        }
    }

    [Serializable]
    private sealed class RectTransformSnapshot
    {
        public Vector2 AnchorMin;
        public Vector2 AnchorMax;
        public Vector2 Pivot;
        public Vector3 AnchoredPosition3D;
        public Vector2 SizeDelta;
        public Quaternion LocalRotation;
        public Vector3 LocalScale;

        public static RectTransformSnapshot Capture(RectTransform rectTransform)
        {
            return new RectTransformSnapshot
            {
                AnchorMin = rectTransform.anchorMin,
                AnchorMax = rectTransform.anchorMax,
                Pivot = rectTransform.pivot,
                AnchoredPosition3D = rectTransform.anchoredPosition3D,
                SizeDelta = rectTransform.sizeDelta,
                LocalRotation = rectTransform.localRotation,
                LocalScale = rectTransform.localScale
            };
        }

        public void Apply(RectTransform rectTransform)
        {
            if (rectTransform == null)
            {
                return;
            }

            rectTransform.anchorMin = AnchorMin;
            rectTransform.anchorMax = AnchorMax;
            rectTransform.pivot = Pivot;
            rectTransform.anchoredPosition3D = AnchoredPosition3D;
            rectTransform.sizeDelta = SizeDelta;
            rectTransform.localRotation = LocalRotation;
            rectTransform.localScale = LocalScale;
        }
    }

    [Serializable]
    private sealed class TransformLocatorStep
    {
        public string Name;
        public int SameNameIndex;
    }

    [Serializable]
    private sealed class ObjectReferenceData
    {
        public string GlobalId;
        public int InstanceId;

        public static ObjectReferenceData Capture(UnityEngine.Object source)
        {
            var data = new ObjectReferenceData();
            if (source == null)
            {
                return data;
            }

            data.InstanceId = source.GetInstanceID();
            try
            {
                var globalId = GlobalObjectId.GetGlobalObjectIdSlow(source);
                if (globalId.identifierType != 0)
                {
                    data.GlobalId = globalId.ToString();
                }
            }
            catch (Exception)
            {
                // Some temporary scene objects only have an Editor instance ID.
            }

            return data;
        }

        public UnityEngine.Object Resolve()
        {
            GlobalObjectId globalId;
            if (!string.IsNullOrEmpty(GlobalId) && GlobalObjectId.TryParse(GlobalId, out globalId))
            {
                var globalObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                if (globalObject != null)
                {
                    return globalObject;
                }
            }

            if (InstanceId != 0)
            {
                return EditorUtility.InstanceIDToObject(InstanceId);
            }

            return null;
        }
    }

    [Serializable]
    private sealed class SessionData
    {
        public int Version;
        public bool HasSession;
        public ObjectReferenceData Target;
        public string TargetDisplayName;
        public string SearchText;
        public int CurrentPage;
        public List<PersistedSlotData> Slots;
    }

    [Serializable]
    private sealed class PersistedSlotData
    {
        public ObjectReferenceData Component;
        public SlotKind Kind;
        public string Path;
        public ObjectReferenceData OriginalSource;
        public RectTransformSnapshot OriginalRect;
        public List<TransformLocatorStep> Locator;
        public int ComponentIndex;
        public ObjectReferenceData Replacement;
        public bool IsReplacementApplied;
        public bool UseNativeSize;
        public bool GroupExpanded;
        public string Warning;

        public static PersistedSlotData Capture(ImageSlot slot)
        {
            return new PersistedSlotData
            {
                Component = ObjectReferenceData.Capture(slot.Component),
                Kind = slot.Kind,
                Path = slot.Path,
                OriginalSource = ObjectReferenceData.Capture(slot.OriginalSource),
                OriginalRect = slot.OriginalRect,
                Locator = slot.Locator,
                ComponentIndex = slot.ComponentIndex,
                Replacement = ObjectReferenceData.Capture(slot.Replacement),
                IsReplacementApplied = slot.IsReplacementApplied,
                UseNativeSize = slot.UseNativeSize,
                GroupExpanded = slot.GroupExpanded,
                Warning = slot.Warning
            };
        }

        public ImageSlot Restore(int sessionVersion)
        {
            var replacement = Replacement == null ? null : Replacement.Resolve() as Sprite;
            var isReplacementApplied = replacement != null
                && (sessionVersion >= AppliedStateVersion ? IsReplacementApplied : true);
            return new ImageSlot
            {
                Component = Component == null ? null : Component.Resolve() as UnityEngine.Component,
                Kind = Kind,
                Path = Path,
                OriginalSource = OriginalSource == null ? null : OriginalSource.Resolve(),
                OriginalRect = OriginalRect ?? new RectTransformSnapshot(),
                Locator = Locator ?? new List<TransformLocatorStep>(),
                ComponentIndex = ComponentIndex,
                Replacement = replacement,
                IsReplacementApplied = isReplacementApplied,
                UseNativeSize = isReplacementApplied && UseNativeSize,
                GroupExpanded = GroupExpanded,
                Warning = Warning ?? string.Empty
            };
        }
    }
}
