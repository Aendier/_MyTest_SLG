using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public sealed class PrefabImageReplacerIgnoreListWindow : EditorWindow
{
    [SerializeField] private Vector2 scrollPosition;

    [NonSerialized] private PrefabImageReplacerIgnoreListConfig ignoreListConfig;
    [NonSerialized] private string configError = string.Empty;
    [NonSerialized] private string editMessage = string.Empty;
    [NonSerialized] private GameObject pendingPrefab;
    [NonSerialized] private DefaultAsset pendingFolder;

    [MenuItem("UIR/Prefab Image Replacer Ignore List")]
    public static void Open()
    {
        var window = GetWindow<PrefabImageReplacerIgnoreListWindow>("Prefab 忽略列表");
        window.minSize = new Vector2(520f, 300f);
    }

    private void OnEnable()
    {
        minSize = new Vector2(520f, 300f);
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
        TryLoadConfig();
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        SaveConfigIfDirty();
    }

    private void OnUndoRedoPerformed()
    {
        SaveConfigIfDirty();
        Repaint();
    }

    private void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Prefab 图片替换忽略列表", EditorStyles.boldLabel);
            if (ignoreListConfig != null)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    string.Format(
                        "Prefab {0}    文件夹 {1}",
                        ignoreListConfig.IgnoredPrefabs.Count,
                        ignoreListConfig.IgnoredFolders.Count),
                    EditorStyles.miniBoldLabel);
            }
        }

        EditorGUILayout.HelpBox(
            "扫描时会跳过命中的嵌套 Prefab 实例及其完整子树。修改后请在 Prefab Image Replacer 中重新扫描。",
            MessageType.Info);

        var editingDisabled = EditorApplication.isPlayingOrWillChangePlaymode;
        if (editingDisabled)
        {
            EditorGUILayout.HelpBox("忽略列表仅支持在编辑模式下修改。", MessageType.Warning);
        }

        if (ignoreListConfig == null)
        {
            EditorGUILayout.HelpBox(configError, MessageType.Error);
            using (new EditorGUI.DisabledScope(editingDisabled))
            {
                if (GUILayout.Button("重新加载忽略列表配置", GUILayout.Width(170f)))
                {
                    TryLoadConfig();
                }
            }

            return;
        }

        if (!string.IsNullOrEmpty(editMessage))
        {
            EditorGUILayout.HelpBox(editMessage, MessageType.Warning);
        }

        EditorGUILayout.Space(4f);
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        using (new EditorGUI.DisabledScope(editingDisabled))
        {
            DrawIgnoreListSection(
                "忽略 Prefab",
                "精确忽略选中的 Prefab，扫描时跳过对应实例的完整子树。",
                "暂无忽略的 Prefab。",
                ignoreListConfig.IgnoredPrefabs,
                false);
            EditorGUILayout.Space(10f);
            DrawIgnoreListSection(
                "忽略文件夹",
                "忽略文件夹及其子文件夹中的所有 Prefab。",
                "暂无忽略的文件夹。",
                ignoreListConfig.IgnoredFolders,
                true);
        }

        EditorGUILayout.EndScrollView();
    }

    private void TryLoadConfig()
    {
        try
        {
            ignoreListConfig = PrefabImageReplacerIgnoreListConfig.LoadOrCreate();
            configError = string.Empty;
        }
        catch (Exception exception)
        {
            ignoreListConfig = null;
            configError = "忽略列表配置不可用：" + exception.Message;
        }

        Repaint();
    }

    private void DrawIgnoreListSection(
        string label,
        string description,
        string emptyMessage,
        IList<PrefabImageReplacerIgnoreEntry> entries,
        bool isFolder)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            DrawSectionHeader(label, entries == null ? 0 : entries.Count, isFolder);
            EditorGUILayout.LabelField(description, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4f);

            if (entries == null)
            {
                EditorGUILayout.HelpBox("配置列表不可用，请重新加载配置。", MessageType.Warning);
                return;
            }

            if (entries.Count == 0)
            {
                GUILayout.Label(
                    emptyMessage,
                    EditorStyles.centeredGreyMiniLabel,
                    GUILayout.Height(32f));
            }
            else
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    if (index > 0)
                    {
                        DrawSeparator();
                    }

                    if (DrawIgnoreListRow(entries, index, isFolder))
                    {
                        return;
                    }
                }
            }

            EditorGUILayout.Space(4f);
            DrawSeparator();
            EditorGUILayout.Space(4f);
            DrawAddControls(isFolder, entries);
        }
    }

    private static void DrawSectionHeader(string label, int count, bool isFolder)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            var icon = EditorGUIUtility.IconContent(isFolder ? "Folder Icon" : "Prefab Icon");
            GUILayout.Label(icon, GUILayout.Width(20f), GUILayout.Height(18f));
            GUILayout.Label(label, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(count + " 项", EditorStyles.miniLabel, GUILayout.Width(42f));
        }
    }

    private bool DrawIgnoreListRow(
        IList<PrefabImageReplacerIgnoreEntry> entries,
        int index,
        bool isFolder)
    {
        var entry = entries[index];
        string currentPath;
        string warning;
        var currentObject = ResolveIgnoreListObject(entry, isFolder, out currentPath, out warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.ObjectField(
                currentObject,
                isFolder ? typeof(DefaultAsset) : typeof(GameObject),
                false);
            var changed = EditorGUI.EndChangeCheck();

            var removeContent = CreateIconContent("Toolbar Minus", "-", "从忽略列表移除此条目");
            if (GUILayout.Button(
                    removeContent,
                    GUILayout.Width(28f),
                    GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            {
                ModifyConfig("删除 Prefab 忽略列表条目", () => entries.RemoveAt(index));
                return true;
            }

            if (changed)
            {
                ChangeIgnoreListEntry(entries, index, entry, picked, isFolder);
                return picked == null;
            }
        }

        if (!string.IsNullOrEmpty(warning))
        {
            EditorGUILayout.HelpBox(warning, MessageType.Warning);
        }
        else if (!string.IsNullOrEmpty(currentPath))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(4f);
                EditorGUILayout.LabelField(
                    new GUIContent("路径  " + currentPath, currentPath),
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        return false;
    }

    private void ChangeIgnoreListEntry(
        IList<PrefabImageReplacerIgnoreEntry> entries,
        int index,
        PrefabImageReplacerIgnoreEntry entry,
        UnityEngine.Object picked,
        bool isFolder)
    {
        if (picked == null)
        {
            ModifyConfig("删除 Prefab 忽略列表条目", () => entries.RemoveAt(index));
            return;
        }

        var pickedPath = AssetDatabase.GetAssetPath(picked);
        string validationError;
        if (!ValidateIgnoreListAsset(pickedPath, isFolder, out validationError))
        {
            editMessage = validationError;
            return;
        }

        var pickedGuid = AssetDatabase.AssetPathToGUID(pickedPath);
        if (entries.Any(item => item != null
            && item != entry
            && string.Equals(item.Guid, pickedGuid, StringComparison.OrdinalIgnoreCase)))
        {
            editMessage = "该资源已经在忽略列表中。";
            return;
        }

        ModifyConfig("修改 Prefab 忽略列表条目", () =>
        {
            if (entry == null)
            {
                entries[index] = PrefabImageReplacerIgnoreEntry.Create(pickedPath);
            }
            else
            {
                entry.SetValues(pickedGuid, pickedPath);
            }
        });
    }

    private void DrawAddControls(bool isFolder, IList<PrefabImageReplacerIgnoreEntry> entries)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (isFolder)
            {
                pendingFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                    "添加文件夹",
                    pendingFolder,
                    typeof(DefaultAsset),
                    false);
                using (new EditorGUI.DisabledScope(pendingFolder == null))
                {
                    var addContent = CreateIconContent("Toolbar Plus", "+", "添加选中的文件夹");
                    if (GUILayout.Button(
                            addContent,
                            GUILayout.Width(28f),
                            GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                    {
                        AddIgnoreListAsset(pendingFolder, true, entries);
                        pendingFolder = null;
                    }
                }
            }
            else
            {
                pendingPrefab = (GameObject)EditorGUILayout.ObjectField(
                    "添加 Prefab",
                    pendingPrefab,
                    typeof(GameObject),
                    false);
                using (new EditorGUI.DisabledScope(pendingPrefab == null))
                {
                    var addContent = CreateIconContent("Toolbar Plus", "+", "添加选中的 Prefab");
                    if (GUILayout.Button(
                            addContent,
                            GUILayout.Width(28f),
                            GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                    {
                        AddIgnoreListAsset(pendingPrefab, false, entries);
                        pendingPrefab = null;
                    }
                }
            }
        }
    }

    private static void DrawSeparator()
    {
        var rect = EditorGUILayout.GetControlRect(false, 1f);
        rect.xMin += 4f;
        rect.xMax -= 4f;
        var color = EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.08f)
            : new Color(0f, 0f, 0f, 0.12f);
        EditorGUI.DrawRect(rect, color);
    }

    private static GUIContent CreateIconContent(string iconName, string fallbackText, string tooltip)
    {
        var icon = EditorGUIUtility.IconContent(iconName);
        return icon.image == null
            ? new GUIContent(fallbackText, tooltip)
            : new GUIContent(icon.image, tooltip);
    }

    private void AddIgnoreListAsset(
        UnityEngine.Object asset,
        bool isFolder,
        IList<PrefabImageReplacerIgnoreEntry> entries)
    {
        if (asset == null)
        {
            editMessage = "请选择要加入忽略列表的资源。";
            return;
        }

        var path = AssetDatabase.GetAssetPath(asset);
        string validationError;
        if (!ValidateIgnoreListAsset(path, isFolder, out validationError))
        {
            editMessage = validationError;
            return;
        }

        var guid = AssetDatabase.AssetPathToGUID(path);
        if (entries.Any(item => item != null
            && string.Equals(item.Guid, guid, StringComparison.OrdinalIgnoreCase)))
        {
            editMessage = "该资源已经在忽略列表中。";
            return;
        }

        ModifyConfig(
            "添加 Prefab 忽略列表条目",
            () => entries.Add(PrefabImageReplacerIgnoreEntry.Create(path)));
    }

    private static bool ValidateIgnoreListAsset(string path, bool isFolder, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            error = "忽略列表资源路径无效。";
            return false;
        }

        if (isFolder)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                error = "忽略文件夹条目必须是项目文件夹。";
                return false;
            }
        }
        else if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            || AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
        {
            error = "忽略 Prefab 条目必须是项目中的 Prefab 资源。";
            return false;
        }

        if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
        {
            error = "忽略列表资源没有可用 GUID。";
            return false;
        }

        return true;
    }

    private static UnityEngine.Object ResolveIgnoreListObject(
        PrefabImageReplacerIgnoreEntry entry,
        bool isFolder,
        out string currentPath,
        out string warning)
    {
        currentPath = string.Empty;
        warning = string.Empty;
        if (entry == null || string.IsNullOrEmpty(entry.Guid))
        {
            warning = "忽略列表条目为空或缺少 GUID。";
            return null;
        }

        currentPath = AssetDatabase.GUIDToAssetPath(entry.Guid);
        if (string.IsNullOrEmpty(currentPath))
        {
            warning = string.Format(
                "忽略列表资源已丢失（GUID {0}）。最后路径：{1}",
                entry.Guid,
                string.IsNullOrEmpty(entry.LastKnownPath) ? "未知" : entry.LastKnownPath);
            return null;
        }

        if (isFolder)
        {
            if (!AssetDatabase.IsValidFolder(currentPath))
            {
                warning = "忽略列表文件夹无效：" + currentPath;
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<DefaultAsset>(currentPath);
        }

        if (!currentPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            warning = "忽略列表 Prefab 条目不是 .prefab 资源：" + currentPath;
            return null;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(currentPath);
        if (prefab == null)
        {
            warning = "忽略列表 Prefab 资源无效：" + currentPath;
            return null;
        }

        return prefab;
    }

    private void ModifyConfig(string actionName, Action mutation)
    {
        if (ignoreListConfig == null || mutation == null)
        {
            return;
        }

        Undo.RecordObject(ignoreListConfig, actionName);
        mutation();
        EditorUtility.SetDirty(ignoreListConfig);
        AssetDatabase.SaveAssetIfDirty(ignoreListConfig);
        editMessage = string.Empty;
        Repaint();
    }

    private void SaveConfigIfDirty()
    {
        if (ignoreListConfig != null)
        {
            AssetDatabase.SaveAssetIfDirty(ignoreListConfig);
        }
    }
}
