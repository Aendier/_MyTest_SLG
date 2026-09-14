using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Finds TMP text components in Prefab assets that use an exact font/material pair,
/// then replaces the pair one item, one Prefab, or one scan at a time.
/// </summary>
public sealed class PrefabFontMaterialReplacerWindow : EditorWindow
{
    private const string MenuPath = "UIR/Prefab Font Material Replacer";
    private const int ScanItemsPerUpdate = 1;

    [SerializeField] private List<DefaultAsset> scanFolders = new List<DefaultAsset>();
    [SerializeField] private TMP_FontAsset sourceFont;
    [SerializeField] private TMP_FontAsset targetFont;
    [SerializeField] private Material sourceMaterial;
    [SerializeField] private Material targetMaterial;
    [SerializeField] private bool includeVariants;
    [SerializeField] private string searchText = string.Empty;

    [NonSerialized] private List<MaterialOption> sourceMaterialOptions = new List<MaterialOption>();
    [NonSerialized] private List<MaterialOption> targetMaterialOptions = new List<MaterialOption>();
    [NonSerialized] private List<PrefabGroup> groups = new List<PrefabGroup>();
    [NonSerialized] private List<string> scanPaths = new List<string>();
    [NonSerialized] private int scanIndex;
    [NonSerialized] private bool isScanning;
    [NonSerialized] private bool scanWasCancelled;
    [NonSerialized] private bool needsReview;
    [NonSerialized] private string scanMessage = string.Empty;

    [MenuItem(MenuPath)]
    public static void Open()
    {
        var window = GetWindow<PrefabFontMaterialReplacerWindow>("Prefab Font Replacer");
        window.minSize = new Vector2(760f, 520f);
    }

    private void OnEnable()
    {
        if (scanFolders == null)
        {
            scanFolders = new List<DefaultAsset>();
        }

        if (groups == null)
        {
            groups = new List<PrefabGroup>();
        }

        RefreshMaterialOptions(true);
        RefreshMaterialOptions(false);
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
    }

    private void OnDisable()
    {
        StopScan(false);
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
    }

    private void OnUndoRedoPerformed()
    {
        var changed = false;
        foreach (var group in groups)
        {
            foreach (var occurrence in group.Occurrences)
            {
                if (occurrence.Status == ReplacementStatus.Applied)
                {
                    occurrence.Status = ReplacementStatus.NeedsReview;
                    occurrence.Message = "Undo/Redo was used; scan again to verify this item.";
                    changed = true;
                }
            }
        }

        if (changed)
        {
            needsReview = true;
            Repaint();
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Prefab Font and Material Replacer", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Scans Prefab assets for TMP components using an exact source font and shared material. "
            + "The tool changes Prefab assets only; scene instances are not modified.",
            MessageType.Info);

        DrawConfiguration();
        DrawScanToolbar();
        DrawStatus();
        DrawResults();
    }

    private void DrawConfiguration()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Scan folders", EditorStyles.boldLabel);
            for (var index = 0; index < scanFolders.Count; index++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var selected = (DefaultAsset)EditorGUILayout.ObjectField(
                        scanFolders[index], typeof(DefaultAsset), false);
                    if (selected != scanFolders[index])
                    {
                        scanFolders[index] = selected;
                    }

                    if (GUILayout.Button("Remove", GUILayout.Width(64f)))
                    {
                        scanFolders.RemoveAt(index);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add folder", GUILayout.Width(90f)))
                {
                    scanFolders.Add(null);
                }

                EditorGUILayout.LabelField(
                    "Add one or more folders from the Project window.",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Source pair", EditorStyles.boldLabel);
            var newSourceFont = (TMP_FontAsset)EditorGUILayout.ObjectField(
                "Source font", sourceFont, typeof(TMP_FontAsset), false);
            if (newSourceFont != sourceFont)
            {
                sourceFont = newSourceFont;
                RefreshMaterialOptions(true);
            }

            sourceMaterial = DrawMaterialPopup("Source material", sourceMaterial, sourceMaterialOptions);

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField("Target pair", EditorStyles.boldLabel);
            var newTargetFont = (TMP_FontAsset)EditorGUILayout.ObjectField(
                "Target font", targetFont, typeof(TMP_FontAsset), false);
            if (newTargetFont != targetFont)
            {
                targetFont = newTargetFont;
                RefreshMaterialOptions(false);
            }

            targetMaterial = DrawMaterialPopup("Target material", targetMaterial, targetMaterialOptions);

            includeVariants = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Scan Prefab Variants",
                    "When enabled, only direct Variant objects and font/material overrides are considered."),
                includeVariants);
        }
    }

    private void DrawScanToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUILayout.LabelField("Filter", GUILayout.Width(36f));
            var newSearch = EditorGUILayout.TextField(
                searchText,
                GUI.skin.FindStyle("ToolbarSearchTextField") ?? GUI.skin.textField);
            if (!string.Equals(newSearch, searchText, StringComparison.Ordinal))
            {
                searchText = newSearch;
            }

            GUILayout.FlexibleSpace();
            if (isScanning)
            {
                if (GUILayout.Button("Cancel scan", EditorStyles.toolbarButton, GUILayout.Width(86f)))
                {
                    StopScan(true);
                }
            }
            else
            {
                using (new EditorGUI.DisabledScope(!CanScan()))
                {
                    if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(54f)))
                    {
                        StartScan();
                    }
                }

                using (new EditorGUI.DisabledScope(groups.Count == 0))
                {
                    if (GUILayout.Button("Replace all", EditorStyles.toolbarButton, GUILayout.Width(84f)))
                    {
                        RequestReplace(groups.SelectMany(item => item.Occurrences), "Replace all matching Prefab text");
                    }
                }
            }
        }
    }

    private void DrawStatus()
    {
        if (!string.IsNullOrEmpty(scanMessage))
        {
            EditorGUILayout.HelpBox(scanMessage, scanWasCancelled ? MessageType.Warning : MessageType.Info);
        }

        if (needsReview)
        {
            EditorGUILayout.HelpBox(
                "Undo/Redo changed one or more applied items. Scan again before relying on their status.",
                MessageType.Warning);
        }
    }

    private void DrawResults()
    {
        var visibleGroups = GetVisibleGroups();
        var occurrenceCount = groups.Sum(group => group.Occurrences.Count);
        var appliedCount = groups.Sum(group => group.Occurrences.Count(item => item.Status == ReplacementStatus.Applied));
        var failedCount = groups.Sum(group => group.Occurrences.Count(item => item.Status == ReplacementStatus.Failed));
        EditorGUILayout.LabelField(
            string.Format(
                "Prefabs: {0}    Matches: {1}    Applied: {2}    Failed: {3}",
                groups.Count,
                occurrenceCount,
                appliedCount,
                failedCount),
            EditorStyles.miniBoldLabel);

        using (var scroll = new EditorGUILayout.ScrollViewScope(GetScrollPosition()))
        {
            SetScrollPosition(scroll.scrollPosition);
            foreach (var group in visibleGroups)
            {
                DrawGroup(group);
            }

            if (groups.Count == 0 && !isScanning)
            {
                EditorGUILayout.HelpBox("No matching TMP components have been scanned yet.", MessageType.Info);
            }
            else if (groups.Count > 0 && visibleGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("No result matches the current filter.", MessageType.Info);
            }
        }
    }

    [NonSerialized] private Vector2 scrollPosition;

    private Vector2 GetScrollPosition()
    {
        return scrollPosition;
    }

    private void SetScrollPosition(Vector2 value)
    {
        scrollPosition = value;
    }

    private void DrawGroup(PrefabGroup group)
    {
        var visibleOccurrences = group.Occurrences
            .Where(occurrence => MatchesFilter(group, occurrence))
            .ToList();
        if (visibleOccurrences.Count == 0)
        {
            return;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                group.Expanded = EditorGUILayout.Foldout(
                    group.Expanded,
                    string.Format("{0} ({1})", group.Path, visibleOccurrences.Count),
                    true);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Replace Prefab", GUILayout.Width(104f)))
                {
                    RequestReplace(group.Occurrences, "Replace matching text in " + group.Path);
                }
            }

            if (!group.Expanded)
            {
                return;
            }

            foreach (var occurrence in visibleOccurrences)
            {
                DrawOccurrence(occurrence);
            }
        }
    }

    private void DrawOccurrence(PrefabOccurrence occurrence)
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            var label = string.Format(
                "{0} [{1}]\n{2}",
                occurrence.HierarchyPath,
                occurrence.ComponentTypeName,
                occurrence.Message);
            var rect = GUILayoutUtility.GetRect(
                new GUIContent(label),
                GUI.skin.button,
                GUILayout.ExpandWidth(true),
                GUILayout.MinHeight(38f));
            if (GUI.Button(rect, label, GUI.skin.button))
            {
                LocateOccurrence(occurrence, Event.current.clickCount >= 2);
            }

            var status = occurrence.Status.ToString();
            var previousColor = GUI.backgroundColor;
            GUI.backgroundColor = GetStatusColor(occurrence.Status);
            GUILayout.Label(status, EditorStyles.miniButton, GUILayout.Width(86f));
            GUI.backgroundColor = previousColor;

            using (new EditorGUI.DisabledScope(isScanning || occurrence.Status == ReplacementStatus.Applied))
            {
                if (GUILayout.Button("Replace", GUILayout.Width(62f)))
                {
                    RequestReplace(new[] { occurrence }, "Replace " + occurrence.HierarchyPath);
                }
            }
        }
    }

    private static Color GetStatusColor(ReplacementStatus status)
    {
        switch (status)
        {
            case ReplacementStatus.Applied:
                return new Color(0.55f, 0.85f, 0.55f);
            case ReplacementStatus.Failed:
                return new Color(1f, 0.62f, 0.52f);
            case ReplacementStatus.Skipped:
            case ReplacementStatus.NeedsReview:
                return new Color(1f, 0.82f, 0.42f);
            default:
                return Color.white;
        }
    }

    private Material DrawMaterialPopup(
        string label,
        Material selected,
        List<MaterialOption> options)
    {
        if (options == null || options.Count == 0)
        {
            EditorGUILayout.HelpBox(label + ": no associated materials found.", MessageType.Warning);
            return null;
        }

        var labels = options.Select(option => option.Label).ToArray();
        var selectedIndex = options.FindIndex(option => option.Material == selected);
        selectedIndex = Mathf.Clamp(selectedIndex, 0, options.Count - 1);
        var newIndex = EditorGUILayout.Popup(label, selectedIndex, labels);
        return options[newIndex].Material;
    }

    private void RefreshMaterialOptions(bool source)
    {
        var font = source ? sourceFont : targetFont;
        var options = BuildMaterialOptions(font);
        if (source)
        {
            sourceMaterialOptions = options;
            sourceMaterial = KeepOrChooseMaterial(sourceMaterial, options);
        }
        else
        {
            targetMaterialOptions = options;
            targetMaterial = KeepOrChooseMaterial(targetMaterial, options);
        }
    }

    private static Material KeepOrChooseMaterial(Material selected, List<MaterialOption> options)
    {
        if (options.Any(option => option.Material == selected))
        {
            return selected;
        }

        return options.Count == 0 ? null : options[0].Material;
    }

    private static List<MaterialOption> BuildMaterialOptions(TMP_FontAsset font)
    {
        var options = new List<MaterialOption>();
        if (font == null)
        {
            return options;
        }

        var atlasTextures = new HashSet<Texture>(
            (font.atlasTextures ?? new Texture2D[0]).Where(texture => texture != null));
        AddMaterialOption(options, font.material);

        var materialGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
        foreach (var guid in materialGuids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null || material == font.material || !IsTextMeshProMaterial(material))
            {
                continue;
            }

            if (material.mainTexture != null && atlasTextures.Contains(material.mainTexture))
            {
                AddMaterialOption(options, material);
            }
        }

        return options
            .OrderBy(option => option.Material == font.material ? 0 : 1)
            .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddMaterialOption(List<MaterialOption> options, Material material)
    {
        if (material == null || options.Any(option => option.Material == material))
        {
            return;
        }

        options.Add(new MaterialOption
        {
            Material = material,
            Label = material.name + " (" + AssetDatabase.GetAssetPath(material) + ")"
        });
    }

    private static bool IsTextMeshProMaterial(Material material)
    {
        return material.shader != null
            && material.shader.name.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool CanScan()
    {
        return !isScanning
            && sourceFont != null
            && targetFont != null
            && sourceMaterial != null
            && targetMaterial != null
            && GetValidFolderPaths().Count > 0;
    }

    private void StartScan()
    {
        if (!CanScan())
        {
            scanMessage = "Choose valid folders, fonts, and associated materials before scanning.";
            return;
        }

        StopScan(false);
        groups.Clear();
        needsReview = false;
        scanMessage = string.Empty;
        scanWasCancelled = false;
        scanPaths = FindPrefabPaths();
        scanIndex = 0;
        isScanning = true;
        EditorApplication.update -= ScanStep;
        EditorApplication.update += ScanStep;
        Repaint();
    }

    private void ScanStep()
    {
        if (!isScanning)
        {
            return;
        }

        for (var item = 0; item < ScanItemsPerUpdate && scanIndex < scanPaths.Count; item++)
        {
            var path = scanPaths[scanIndex++];
            var progress = scanPaths.Count == 0 ? 1f : (float)scanIndex / scanPaths.Count;
            if (EditorUtility.DisplayCancelableProgressBar(
                    "Scanning Prefabs",
                    path,
                    progress))
            {
                StopScan(true);
                return;
            }

            ScanPrefab(path);
        }

        if (scanIndex >= scanPaths.Count)
        {
            StopScan(false);
            scanMessage = string.Format(
                "Scan complete. Found {0} matching TMP component(s) in {1} Prefab(s).",
                groups.Sum(group => group.Occurrences.Count),
                groups.Count);
        }

        Repaint();
    }

    private void StopScan(bool cancelled)
    {
        if (isScanning)
        {
            EditorApplication.update -= ScanStep;
        }

        isScanning = false;
        scanWasCancelled = cancelled;
        EditorUtility.ClearProgressBar();
    }

    private List<string> FindPrefabPaths()
    {
        var paths = GetValidFolderPaths();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", paths.ToArray()))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
            {
                result.Add(path);
            }
        }

        return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> GetValidFolderPaths()
    {
        return scanFolders
            .Where(folder => folder != null)
            .Select(AssetDatabase.GetAssetPath)
            .Where(path => !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ScanPrefab(string path)
    {
        if (!includeVariants && PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(path)) == PrefabAssetType.Variant)
        {
            return;
        }

        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                return;
            }

            var isVariant = PrefabUtility.GetPrefabAssetType(root) == PrefabAssetType.Variant;
            var directVariantTargets = isVariant && includeVariants
                ? BuildDirectVariantTargets(root)
                : null;

            foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (!IsDirectPrefabContent(text.gameObject, root, path, isVariant && includeVariants)
                    || (isVariant && includeVariants && !IsDirectVariantText(text, directVariantTargets))
                    || text.font != sourceFont
                    || text.fontSharedMaterial != sourceMaterial)
                {
                    continue;
                }

                AddOccurrence(path, root.transform, text);
            }
        }
        catch (Exception exception)
        {
            scanMessage = "Could not scan " + path + ": " + exception.Message;
        }
        finally
        {
            if (root != null)
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }

    private void AddOccurrence(string prefabPath, Transform root, TMP_Text text)
    {
        var group = groups.FirstOrDefault(item => string.Equals(item.Path, prefabPath, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            group = new PrefabGroup { Path = prefabPath, Expanded = true };
            groups.Add(group);
        }

        var components = text.transform.GetComponents<TMP_Text>();
        var componentIndex = Array.IndexOf(components, text);
        group.Occurrences.Add(new PrefabOccurrence
        {
            PrefabPath = prefabPath,
            HierarchyPath = BuildHierarchyPath(text.transform, root),
            SiblingIndices = BuildSiblingIndices(text.transform, root),
            ComponentIndex = componentIndex,
            ComponentTypeName = text.GetType().Name,
            Status = ReplacementStatus.Pending,
            Message = "Ready to replace."
        });
    }

    private static bool IsDirectPrefabContent(
        GameObject gameObject,
        GameObject hostRoot,
        string hostPrefabPath,
        bool isVariant)
    {
        if (isVariant)
        {
            var nearestVariantRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            return nearestVariantRoot == null || nearestVariantRoot == hostRoot;
        }

        if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
        {
            return true;
        }

        var nestedPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
        return string.IsNullOrEmpty(nestedPath)
            || string.Equals(nestedPath, hostPrefabPath, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<TMP_Text> BuildDirectVariantTargets(GameObject root)
    {
        var result = new HashSet<TMP_Text>();
        foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
        {
            if (PrefabUtility.GetCorrespondingObjectFromSource(text) == null)
            {
                result.Add(text);
            }
        }

        var modifications = PrefabUtility.GetPropertyModifications(root) ?? new PropertyModification[0];
        foreach (var modification in modifications)
        {
            if (modification.target is TMP_Text text
                && (string.Equals(modification.propertyPath, "m_fontAsset", StringComparison.Ordinal)
                    || string.Equals(modification.propertyPath, "m_sharedMaterial", StringComparison.Ordinal)
                    || string.Equals(modification.propertyPath, "m_fontSharedMaterial", StringComparison.Ordinal)))
            {
                result.Add(text);
            }
        }

        return result;
    }

    private static bool IsDirectVariantText(TMP_Text text, HashSet<TMP_Text> directTargets)
    {
        return directTargets != null && directTargets.Contains(text);
    }

    private void RequestReplace(IEnumerable<PrefabOccurrence> requested, string actionName)
    {
        var entries = requested
            .Where(item => item != null && item.Status != ReplacementStatus.Applied)
            .Distinct()
            .ToList();
        if (entries.Count == 0)
        {
            return;
        }

        if (sourceFont == null || sourceMaterial == null || targetFont == null || targetMaterial == null)
        {
            EditorUtility.DisplayDialog("Cannot replace", "Choose complete source and target font/material pairs first.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "Confirm replacement",
                string.Format("Replace {0} matching TMP component(s)?\n\nThis can be undone as one operation.", entries.Count),
                "Replace",
                "Cancel"))
        {
            return;
        }

        ApplyReplacements(entries, actionName);
    }

    private void ApplyReplacements(List<PrefabOccurrence> entries, string actionName)
    {
        Undo.IncrementCurrentGroup();
        var undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(actionName);

        foreach (var prefabGroup in entries.GroupBy(item => item.PrefabPath, StringComparer.OrdinalIgnoreCase))
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabGroup.Key);
            var changed = new List<PrefabOccurrence>();
            try
            {
                if (root == null)
                {
                    MarkFailed(prefabGroup, "Could not load Prefab contents.");
                    continue;
                }

                foreach (var occurrence in prefabGroup)
                {
                    var text = FindText(root.transform, occurrence);
                    if (text == null)
                    {
                        occurrence.Status = ReplacementStatus.Failed;
                        occurrence.Message = "The component could not be located; scan again.";
                        continue;
                    }

                    if (text.font != sourceFont || text.fontSharedMaterial != sourceMaterial)
                    {
                        occurrence.Status = ReplacementStatus.Skipped;
                        occurrence.Message = "The source font/material pair changed since scanning.";
                        continue;
                    }

                    Undo.RecordObject(text, actionName);
                    text.font = targetFont;
                    text.fontSharedMaterial = targetMaterial;
                    EditorUtility.SetDirty(text);
                    EditorUtility.SetDirty(root);
                    changed.Add(occurrence);
                }

                if (changed.Count > 0)
                {
                    if (PrefabUtility.SavePrefabAsset(root))
                    {
                        foreach (var occurrence in changed)
                        {
                            occurrence.Status = ReplacementStatus.Applied;
                            occurrence.Message = "Replacement saved. Double-click to verify in Prefab Mode.";
                        }
                    }
                    else
                    {
                        MarkFailed(changed, "Unity could not save the Prefab asset.");
                    }
                }
            }
            catch (Exception exception)
            {
                MarkFailed(prefabGroup, exception.Message);
            }
        }

        Undo.CollapseUndoOperations(undoGroup);
        needsReview = false;
        scanMessage = "Replacement finished. Review each row, then scan again if the assets changed externally.";
        Repaint();
    }

    private static void MarkFailed(IEnumerable<PrefabOccurrence> entries, string message)
    {
        foreach (var occurrence in entries)
        {
            occurrence.Status = ReplacementStatus.Failed;
            occurrence.Message = message;
        }
    }

    private static TMP_Text FindText(Transform root, PrefabOccurrence occurrence)
    {
        var transform = occurrence.SiblingIndices == null
            ? FindTransform(root, occurrence.HierarchyPath)
            : FindTransform(root, occurrence.SiblingIndices);
        if (transform == null)
        {
            return null;
        }

        var components = transform.GetComponents<TMP_Text>();
        return occurrence.ComponentIndex >= 0 && occurrence.ComponentIndex < components.Length
            ? components[occurrence.ComponentIndex]
            : null;
    }

    private void LocateOccurrence(PrefabOccurrence occurrence, bool openPrefab)
    {
        if (openPrefab)
        {
            var stage = PrefabStageUtility.OpenPrefab(occurrence.PrefabPath);
            if (stage != null)
            {
                EditorApplication.delayCall += () => SelectStageObject(stage, occurrence);
            }

            return;
        }

        var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
        if (currentStage != null
            && string.Equals(currentStage.assetPath, occurrence.PrefabPath, StringComparison.OrdinalIgnoreCase))
        {
            var text = FindText(currentStage.prefabContentsRoot.transform, occurrence);
            if (text != null)
            {
                Selection.activeObject = text.gameObject;
                EditorGUIUtility.PingObject(text.gameObject);
                return;
            }
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(occurrence.PrefabPath);
        EditorUtility.FocusProjectWindow();
        Selection.activeObject = prefab;
        EditorGUIUtility.PingObject(prefab);
    }

    private static void SelectStageObject(PrefabStage stage, PrefabOccurrence occurrence)
    {
        if (stage == null || stage.prefabContentsRoot == null)
        {
            return;
        }

        var text = FindText(stage.prefabContentsRoot.transform, occurrence);
        if (text != null)
        {
            Selection.activeObject = text.gameObject;
            EditorGUIUtility.PingObject(text.gameObject);
        }
    }

    private List<PrefabGroup> GetVisibleGroups()
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return groups;
        }

        var query = searchText.Trim();
        return groups.Where(group => group.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || group.Occurrences.Any(occurrence => MatchesFilter(group, occurrence)))
            .ToList();
    }

    private bool MatchesFilter(PrefabGroup group, PrefabOccurrence occurrence)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var query = searchText.Trim();
        return group.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || occurrence.HierarchyPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || occurrence.ComponentTypeName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || occurrence.Status.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildHierarchyPath(Transform transform, Transform root)
    {
        var names = new Stack<string>();
        var current = transform;
        while (current != null && current != root)
        {
            names.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", names.ToArray());
    }

    private static List<int> BuildSiblingIndices(Transform transform, Transform root)
    {
        var reversed = new List<int>();
        var current = transform;
        while (current != null && current != root)
        {
            reversed.Add(current.GetSiblingIndex());
            current = current.parent;
        }

        reversed.Reverse();
        return reversed;
    }

    private static Transform FindTransform(Transform root, string relativePath)
    {
        if (root == null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(relativePath))
        {
            return root;
        }

        var current = root;
        foreach (var name in relativePath.Split('/'))
        {
            Transform next = null;
            for (var index = 0; index < current.childCount; index++)
            {
                var child = current.GetChild(index);
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                {
                    next = child;
                    break;
                }
            }

            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static Transform FindTransform(Transform root, List<int> siblingIndices)
    {
        if (root == null || siblingIndices == null)
        {
            return root;
        }

        var current = root;
        foreach (var siblingIndex in siblingIndices)
        {
            if (siblingIndex < 0 || siblingIndex >= current.childCount)
            {
                return null;
            }

            current = current.GetChild(siblingIndex);
        }

        return current;
    }

    private sealed class MaterialOption
    {
        public Material Material;
        public string Label;
    }

    private sealed class PrefabGroup
    {
        public string Path;
        public bool Expanded;
        public readonly List<PrefabOccurrence> Occurrences = new List<PrefabOccurrence>();
    }

    private sealed class PrefabOccurrence
    {
        public string PrefabPath;
        public string HierarchyPath;
        public List<int> SiblingIndices;
        public int ComponentIndex;
        public string ComponentTypeName;
        public ReplacementStatus Status;
        public string Message;
    }

    private enum ReplacementStatus
    {
        Pending,
        Applied,
        Skipped,
        Failed,
        NeedsReview
    }
}
