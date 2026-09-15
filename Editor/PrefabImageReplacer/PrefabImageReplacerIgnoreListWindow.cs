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
    [NonSerialized] private DefaultAsset pendingPrefabFolder;
    [NonSerialized] private Sprite pendingSprite;
    [NonSerialized] private DefaultAsset pendingSpriteFolder;

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
                        "Prefab 规则 {0}    Sprite 规则 {1}",
                        ignoreListConfig.IgnoredPrefabs.Count + ignoreListConfig.IgnoredFolders.Count,
                        ignoreListConfig.IgnoredSprites.Count + ignoreListConfig.IgnoredSpriteFolders.Count),
                    EditorStyles.miniBoldLabel);
            }
        }

        EditorGUILayout.HelpBox(
            "Prefab 规则会跳过命中的嵌套实例及其完整子树；Sprite 规则会过滤 Image 使用的原始 Sprite，"
            + "不影响 RawImage。修改后请在 Prefab Image Replacer 中重新扫描。",
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
            EditorGUILayout.LabelField("Prefab 实例", EditorStyles.boldLabel);
            DrawIgnoreListSection(
                "忽略 Prefab",
                "精确忽略选中的 Prefab，扫描时跳过对应实例的完整子树。",
                "暂无忽略的 Prefab。",
                ignoreListConfig.IgnoredPrefabs,
                IgnoreAssetKind.Prefab);
            EditorGUILayout.Space(10f);
            DrawIgnoreListSection(
                "忽略 Prefab 文件夹",
                "忽略文件夹及其子文件夹中的所有 Prefab。",
                "暂无忽略的 Prefab 文件夹。",
                ignoreListConfig.IgnoredFolders,
                IgnoreAssetKind.PrefabFolder);

            EditorGUILayout.Space(16f);
            EditorGUILayout.LabelField("Sprite 资源", EditorStyles.boldLabel);
            DrawIgnoreListSection(
                "忽略 Sprite",
                "精确忽略选中的 Sprite；同一纹理中的其他 Sprite 仍会参与扫描。",
                "暂无忽略的 Sprite。",
                ignoreListConfig.IgnoredSprites,
                IgnoreAssetKind.Sprite);
            EditorGUILayout.Space(10f);
            DrawIgnoreListSection(
                "忽略 Sprite 文件夹",
                "忽略文件夹及其子文件夹中的所有 Sprite。",
                "暂无忽略的 Sprite 文件夹。",
                ignoreListConfig.IgnoredSpriteFolders,
                IgnoreAssetKind.SpriteFolder);
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
        IgnoreAssetKind kind)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            DrawSectionHeader(label, entries == null ? 0 : entries.Count, kind);
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

                    if (DrawIgnoreListRow(entries, index, kind))
                    {
                        return;
                    }
                }
            }

            EditorGUILayout.Space(4f);
            DrawSeparator();
            EditorGUILayout.Space(4f);
            DrawAddControls(kind, entries);
        }
    }

    private static void DrawSectionHeader(string label, int count, IgnoreAssetKind kind)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            var iconName = IsFolderKind(kind)
                ? "Folder Icon"
                : kind == IgnoreAssetKind.Sprite ? "Sprite Icon" : "Prefab Icon";
            var icon = EditorGUIUtility.IconContent(iconName);
            GUILayout.Label(icon, GUILayout.Width(20f), GUILayout.Height(18f));
            GUILayout.Label(label, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label(count + " 项", EditorStyles.miniLabel, GUILayout.Width(42f));
        }
    }

    private bool DrawIgnoreListRow(
        IList<PrefabImageReplacerIgnoreEntry> entries,
        int index,
        IgnoreAssetKind kind)
    {
        var entry = entries[index];
        string currentPath;
        string warning;
        var currentObject = ResolveIgnoreListObject(entry, kind, out currentPath, out warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.ObjectField(
                currentObject,
                GetObjectType(kind),
                false);
            var changed = EditorGUI.EndChangeCheck();

            var removeContent = CreateIconContent("Toolbar Minus", "-", "从忽略列表移除此条目");
            if (GUILayout.Button(
                    removeContent,
                    GUILayout.Width(28f),
                    GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            {
                ModifyConfig("删除 " + GetKindLabel(kind) + " 忽略列表条目", () => entries.RemoveAt(index));
                return true;
            }

            if (changed)
            {
                ChangeIgnoreListEntry(entries, index, entry, picked, kind);
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
                    new GUIContent(
                        "路径  " + currentPath + GetSubAssetLabel(currentObject, kind),
                        currentPath),
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
        IgnoreAssetKind kind)
    {
        if (picked == null)
        {
            ModifyConfig("删除 " + GetKindLabel(kind) + " 忽略列表条目", () => entries.RemoveAt(index));
            return;
        }

        string pickedPath;
        string pickedGuid;
        long pickedLocalFileId;
        string validationError;
        if (!TryGetIgnoreListIdentity(
                picked,
                kind,
                out pickedPath,
                out pickedGuid,
                out pickedLocalFileId,
                out validationError))
        {
            editMessage = validationError;
            return;
        }

        if (entries.Any(item => item != null
            && item != entry
            && EntryMatches(item, kind, pickedGuid, pickedLocalFileId)))
        {
            editMessage = "该资源已经在忽略列表中。";
            return;
        }

        ModifyConfig("修改 " + GetKindLabel(kind) + " 忽略列表条目", () =>
        {
            if (entry == null)
            {
                entries[index] = CreateEntry(picked, kind, pickedPath);
            }
            else if (kind == IgnoreAssetKind.Sprite)
            {
                entry.SetValues(pickedGuid, pickedLocalFileId, pickedPath, picked.name);
            }
            else
            {
                entry.SetValues(pickedGuid, pickedPath);
            }
        });
    }

    private void DrawAddControls(
        IgnoreAssetKind kind,
        IList<PrefabImageReplacerIgnoreEntry> entries)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            var pendingAsset = GetPendingAsset(kind);
            pendingAsset = EditorGUILayout.ObjectField(
                "添加 " + GetKindLabel(kind),
                pendingAsset,
                GetObjectType(kind),
                false);
            SetPendingAsset(kind, pendingAsset);
            using (new EditorGUI.DisabledScope(pendingAsset == null))
            {
                var addContent = CreateIconContent(
                    "Toolbar Plus",
                    "+",
                    "添加选中的 " + GetKindLabel(kind));
                if (GUILayout.Button(
                        addContent,
                        GUILayout.Width(28f),
                        GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                {
                    AddIgnoreListAsset(pendingAsset, kind, entries);
                    SetPendingAsset(kind, null);
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
        IgnoreAssetKind kind,
        IList<PrefabImageReplacerIgnoreEntry> entries)
    {
        if (asset == null)
        {
            editMessage = "请选择要加入忽略列表的资源。";
            return;
        }

        string path;
        string guid;
        long localFileId;
        string validationError;
        if (!TryGetIgnoreListIdentity(
                asset,
                kind,
                out path,
                out guid,
                out localFileId,
                out validationError))
        {
            editMessage = validationError;
            return;
        }

        if (entries.Any(item => item != null
            && EntryMatches(item, kind, guid, localFileId)))
        {
            editMessage = "该资源已经在忽略列表中。";
            return;
        }

        ModifyConfig(
            "添加 " + GetKindLabel(kind) + " 忽略列表条目",
            () => entries.Add(CreateEntry(asset, kind, path)));
    }

    private static bool TryGetIgnoreListIdentity(
        UnityEngine.Object asset,
        IgnoreAssetKind kind,
        out string path,
        out string guid,
        out long localFileId,
        out string error)
    {
        path = asset == null ? string.Empty : AssetDatabase.GetAssetPath(asset);
        guid = string.Empty;
        localFileId = 0;
        error = string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            error = "忽略列表资源路径无效。";
            return false;
        }

        if (IsFolderKind(kind))
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                error = "忽略文件夹条目必须是项目文件夹。";
                return false;
            }
        }
        else if (kind == IgnoreAssetKind.Prefab
            && (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                || asset as GameObject == null
                || AssetDatabase.LoadAssetAtPath<GameObject>(path) == null))
        {
            error = "忽略 Prefab 条目必须是项目中的 Prefab 资源。";
            return false;
        }
        else if (kind == IgnoreAssetKind.Sprite && asset as Sprite == null)
        {
            error = "忽略 Sprite 条目必须是项目中的 Sprite 资源。";
            return false;
        }

        if (kind == IgnoreAssetKind.Sprite)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out localFileId)
                || localFileId == 0)
            {
                error = "忽略 Sprite 资源没有可用 GUID 或 local file ID。";
                return false;
            }
        }
        else
        {
            guid = AssetDatabase.AssetPathToGUID(path);
        }

        if (string.IsNullOrEmpty(guid))
        {
            error = "忽略列表资源没有可用 GUID。";
            return false;
        }

        return true;
    }

    private static UnityEngine.Object ResolveIgnoreListObject(
        PrefabImageReplacerIgnoreEntry entry,
        IgnoreAssetKind kind,
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

        if (IsFolderKind(kind))
        {
            if (!AssetDatabase.IsValidFolder(currentPath))
            {
                warning = "忽略列表文件夹无效：" + currentPath;
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<DefaultAsset>(currentPath);
        }

        if (kind == IgnoreAssetKind.Sprite)
        {
            var sprite = entry.ResolveSprite(out currentPath);
            if (sprite == null)
            {
                warning = string.Format(
                    "忽略列表 Sprite 已丢失或切片标识已变化（local file ID {0}）。最后记录：{1} @ {2}",
                    entry.LocalFileId,
                    string.IsNullOrEmpty(entry.LastKnownName) ? "未知名称" : entry.LastKnownName,
                    string.IsNullOrEmpty(entry.LastKnownPath) ? currentPath : entry.LastKnownPath);
            }

            return sprite;
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

    private UnityEngine.Object GetPendingAsset(IgnoreAssetKind kind)
    {
        switch (kind)
        {
            case IgnoreAssetKind.Prefab:
                return pendingPrefab;
            case IgnoreAssetKind.PrefabFolder:
                return pendingPrefabFolder;
            case IgnoreAssetKind.Sprite:
                return pendingSprite;
            default:
                return pendingSpriteFolder;
        }
    }

    private void SetPendingAsset(IgnoreAssetKind kind, UnityEngine.Object asset)
    {
        switch (kind)
        {
            case IgnoreAssetKind.Prefab:
                pendingPrefab = asset as GameObject;
                break;
            case IgnoreAssetKind.PrefabFolder:
                pendingPrefabFolder = asset as DefaultAsset;
                break;
            case IgnoreAssetKind.Sprite:
                pendingSprite = asset as Sprite;
                break;
            default:
                pendingSpriteFolder = asset as DefaultAsset;
                break;
        }
    }

    private static PrefabImageReplacerIgnoreEntry CreateEntry(
        UnityEngine.Object asset,
        IgnoreAssetKind kind,
        string path)
    {
        return kind == IgnoreAssetKind.Sprite
            ? PrefabImageReplacerIgnoreEntry.Create((Sprite)asset)
            : PrefabImageReplacerIgnoreEntry.Create(path);
    }

    private static bool EntryMatches(
        PrefabImageReplacerIgnoreEntry entry,
        IgnoreAssetKind kind,
        string guid,
        long localFileId)
    {
        return string.Equals(entry.Guid, guid, StringComparison.OrdinalIgnoreCase)
            && (kind != IgnoreAssetKind.Sprite || entry.LocalFileId == localFileId);
    }

    private static bool IsFolderKind(IgnoreAssetKind kind)
    {
        return kind == IgnoreAssetKind.PrefabFolder || kind == IgnoreAssetKind.SpriteFolder;
    }

    private static Type GetObjectType(IgnoreAssetKind kind)
    {
        if (IsFolderKind(kind))
            return typeof(DefaultAsset);

        return kind == IgnoreAssetKind.Sprite ? typeof(Sprite) : typeof(GameObject);
    }

    private static string GetKindLabel(IgnoreAssetKind kind)
    {
        switch (kind)
        {
            case IgnoreAssetKind.Prefab:
                return "Prefab";
            case IgnoreAssetKind.PrefabFolder:
                return "Prefab 文件夹";
            case IgnoreAssetKind.Sprite:
                return "Sprite";
            default:
                return "Sprite 文件夹";
        }
    }

    private static string GetSubAssetLabel(UnityEngine.Object asset, IgnoreAssetKind kind)
    {
        return kind == IgnoreAssetKind.Sprite && asset != null
            ? "  [" + asset.name + "]"
            : string.Empty;
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

    private enum IgnoreAssetKind
    {
        Prefab,
        PrefabFolder,
        Sprite,
        SpriteFolder
    }
}
