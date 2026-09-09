using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Lists Image/RawImage references in a prefab and creates a copy with Sprite replacements.
/// </summary>
public sealed class PrefabImageReplacerWindow : EditorWindow
{
    private const string OutputSuffix = "_Replaced";
    private const float PreviewSize = 44f;

    private GameObject prefab;
    private string searchText = string.Empty;
    private Vector2 scrollPosition;
    private Vector2 problemScrollPosition;
    private List<ImageSlot> slots = new List<ImageSlot>();
    private List<ScanProblem> scanProblems = new List<ScanProblem>();
    private Hash128 scannedDependencyHash;
    private SaveMode saveMode = SaveMode.CreateCopy;
    private int lastReviewIssueCount;
    private bool problemDetailsExpanded;
    private bool hasScanned;
    private bool isBusy;
    private bool isApplyQueued;

    [MenuItem("Tools/Prefab Image Replacer")]
    public static void Open()
    {
        GetWindow<PrefabImageReplacerWindow>("Prefab Image Replacer");
    }

    private void OnInspectorUpdate()
    {
        if (prefab == null || isBusy)
        {
            return;
        }

        var path = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dependencyHash = AssetDatabase.GetAssetDependencyHash(path);
        if (hasScanned && dependencyHash != scannedDependencyHash)
        {
            ScanPrefab();
        }

        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Prefab 图片引用替换", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "按原始 Sprite/Texture 聚合列出 Image 和 RawImage 引用。配置替换 Sprite 后，可生成新 Prefab 或直接修改当前 Prefab。",
            MessageType.Info);
        EditorGUILayout.LabelField("提示：点击原图或槽位图缩略图可在 Project 窗口中定位资源。", EditorStyles.miniLabel);

        EditorGUI.BeginChangeCheck();
        var selectedPrefab = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Prefab", "要扫描的 Prefab 资源"), prefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck())
        {
            prefab = selectedPrefab;
            ScanPrefab();
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUILayout.LabelField(
                new GUIContent("筛选", "按图片名、资源路径或节点路径筛选图片槽位"),
                EditorStyles.miniLabel,
                GUILayout.Width(32f));
            var newSearch = EditorGUILayout.TextField(
                searchText,
                GUI.skin.FindStyle("ToolbarSeachTextField") ?? GUI.skin.textField,
                GUILayout.ExpandWidth(true));
            if (!string.Equals(newSearch, searchText, StringComparison.Ordinal))
            {
                searchText = newSearch;
                Repaint();
            }

            if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(52f)))
            {
                ScanPrefab();
            }
        }

        if (prefab == null)
        {
            EditorGUILayout.HelpBox("请拖入一个 Prefab 资源。", MessageType.Warning);
            return;
        }

        var prefabPath = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            EditorGUILayout.HelpBox("当前对象不是项目中的 Prefab 资源。", MessageType.Error);
            return;
        }

        var selectedSaveMode = (SaveMode)EditorGUILayout.Popup(
            "保存方式",
            (int)saveMode,
            new[] { "新增预制体（推荐）", "直接修改当前预制体" });
        if (selectedSaveMode != saveMode)
        {
            saveMode = selectedSaveMode;
        }

        if (saveMode == SaveMode.ModifyOriginal)
        {
            EditorGUILayout.HelpBox(
                "此模式会覆盖当前 Prefab 文件，未配置替换图的槽位仍会保持不变。执行前还会再次确认。",
                MessageType.Warning);

            if (PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant)
            {
                EditorGUILayout.HelpBox(
                    "当前是 Prefab Variant，覆盖时会写回当前变体并保留其继承关系。",
                    MessageType.Info);
            }
        }

        var outputPath = GetOutputPath(prefabPath);
        EditorGUILayout.LabelField(saveMode == SaveMode.ModifyOriginal ? "目标" : "输出", outputPath, EditorStyles.miniLabel);

        var currentIssues = BuildReviewIssues();
        if (currentIssues.Count > 0 || GetUnconfiguredSlotCount() > 0)
        {
            EditorGUILayout.HelpBox(
                BuildIssueSummary(currentIssues)
                + "\n未配置替换图的槽位，以及 Image/RawImage 空或丢失引用会自动跳过；存在风险的槽位可在确认窗口中多选。",
                MessageType.Warning);
            DrawProblemDetails(currentIssues);
        }

        EditorGUILayout.LabelField(
            string.Format("图片槽位：{0}    引用节点：{1}", slots.Count, slots.Sum(s => s.Usages.Count)),
            EditorStyles.miniBoldLabel);

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        foreach (var slot in slots)
        {
            if (!MatchesSearch(slot))
            {
                continue;
            }

            DrawSlot(slot);
        }

        if (slots.Count == 0 && scanProblems.Count == 0)
        {
            EditorGUILayout.HelpBox("没有找到 Image 或 RawImage 图片引用。", MessageType.Info);
        }

        EditorGUILayout.EndScrollView();

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(isBusy || isApplyQueued || slots.Count == 0))
            {
                var applyButtonLabel = saveMode == SaveMode.ModifyOriginal
                    ? "直接修改当前 Prefab"
                    : "生成替换 Prefab";
                if (GUILayout.Button(applyButtonLabel, GUILayout.Width(150f), GUILayout.Height(28f)))
                {
                    QueuePrepareApply();
                }
            }
        }
    }

    private void QueuePrepareApply()
    {
        if (isBusy || isApplyQueued)
        {
            return;
        }

        isApplyQueued = true;
        EditorApplication.delayCall += () =>
        {
            isApplyQueued = false;
            if (this != null)
            {
                PrepareApply();
            }
        };
        GUIUtility.ExitGUI();
    }

    private void DrawSlot(ImageSlot slot)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(
                string.Format("{0}  ({1} 个引用)", slot.DisplayName, slot.Usages.Count),
                EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(
                "资源路径：" + slot.SourcePath,
                EditorStyles.wordWrappedMiniLabel,
                GUILayout.MinHeight(EditorGUIUtility.singleLineHeight));

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPreview(slot.Source, "点击定位原图资源");
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(
                        slot.SourceKind == SourceKind.Sprite ? "Image.sprite" : "RawImage.texture",
                        EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("原图分辨率", slot.OriginalResolutionText, EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                EditorGUI.BeginChangeCheck();
                var replacement = (Sprite)EditorGUILayout.ObjectField(
                    new GUIContent("替换 Sprite"), slot.Replacement, typeof(Sprite), false, GUILayout.Width(230f));
                if (EditorGUI.EndChangeCheck())
                {
                    slot.Replacement = replacement;
                }
                if (replacement != null)
                {
                    DrawPreview(replacement, "点击定位槽位图资源");
                }
            }

            if (slot.Replacement != null)
            {
                EditorGUILayout.LabelField("槽位图分辨率", slot.ReplacementResolutionText, EditorStyles.miniLabel);
                if (slot.HasResolutionMismatch)
                {
                    EditorGUILayout.HelpBox(
                        string.Format(
                            "分辨率不一致：原图 {0}，槽位图 {1}。请确认缩放、裁剪和显示效果符合预期。",
                            slot.OriginalResolutionText,
                            slot.ReplacementResolutionText),
                        MessageType.Warning);
                }
            }

            slot.UsagesExpanded = EditorGUILayout.Foldout(
                slot.UsagesExpanded,
                string.Format("使用位置（{0} 个）", slot.Usages.Count),
                true);
            if (!slot.UsagesExpanded)
            {
                return;
            }

            foreach (var usage in slot.Usages)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8f);
                    var state = usage.Writable ? string.Empty : "（嵌套 Prefab，只读）";
                    EditorGUILayout.LabelField(
                        usage.Path + "  [" + usage.ComponentProperty + "] " + state,
                        EditorStyles.miniLabel);
                }
            }
        }
    }

    private void DrawPreview(UnityEngine.Object source, string tooltip)
    {
        var previewRect = GUILayoutUtility.GetRect(
            PreviewSize,
            PreviewSize,
            GUILayout.Width(PreviewSize),
            GUILayout.Height(PreviewSize));
        EditorGUI.DrawRect(previewRect, new Color(0.16f, 0.16f, 0.16f));

        var sprite = source as Sprite;
        if (sprite != null && sprite.texture != null
            && sprite.texture.width > 0 && sprite.texture.height > 0)
        {
            var texture = sprite.texture;
            var uv = GetSpriteUvRect(sprite, texture);
            var fittedRect = GetAspectFittedRect(previewRect, sprite.rect.width / sprite.rect.height);
            GUI.DrawTextureWithTexCoords(fittedRect, texture, uv, true);
        }
        else if (source is Texture && ((Texture)source).width > 0 && ((Texture)source).height > 0)
        {
            var texture = (Texture)source;
            var fittedRect = GetAspectFittedRect(previewRect, (float)texture.width / texture.height);
            EditorGUI.DrawPreviewTexture(fittedRect, texture, null, ScaleMode.ScaleToFit);
        }
        else if (source != null)
        {
            var preview = AssetPreview.GetAssetPreview(source);
            if (preview != null)
            {
                EditorGUI.DrawPreviewTexture(previewRect, preview, null, ScaleMode.ScaleToFit);
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

        // Packed and tightly packed Sprites can throw when textureRect is read.
        // UV bounds always identify the actual region inside the atlas texture.
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

    private bool MatchesSearch(ImageSlot slot)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var query = searchText.Trim();
        return slot.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || slot.SourcePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || slot.Usages.Any(u => u.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void ScanPrefab()
    {
        slots = new List<ImageSlot>();
        scanProblems = new List<ScanProblem>();
        hasScanned = false;

        if (prefab == null)
        {
            return;
        }

        var path = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        isBusy = true;
        try
        {
            // Read the imported prefab asset directly. Opening an isolated prefab contents
            // scene can deserialize stale cross-prefab PPtrs into Library/Unused and emit
            // noisy Unity errors before the image references are even inspected.
            var root = prefab;
            var byKey = new Dictionary<string, ImageSlot>(StringComparer.Ordinal);
            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite == null)
                {
                    scanProblems.Add(new ScanProblem(
                        IssueKind.ImageMissingReference,
                        BuildTransformPath(image.transform, root.transform),
                        "Image.sprite 为空或丢失引用"));
                    continue;
                }

                AddUsage(byKey, image.sprite, SourceKind.Sprite, image.gameObject,
                    BuildTransformPath(image.transform, root.transform), "Image.sprite", root);
            }

            foreach (var rawImage in root.GetComponentsInChildren<RawImage>(true))
            {
                if (rawImage.texture == null)
                {
                    scanProblems.Add(new ScanProblem(
                        IssueKind.RawImageMissingReference,
                        BuildTransformPath(rawImage.transform, root.transform),
                        "RawImage.texture 为空或丢失引用"));
                    continue;
                }

                AddUsage(byKey, rawImage.texture, SourceKind.Texture, rawImage.gameObject,
                    BuildTransformPath(rawImage.transform, root.transform), "RawImage.texture", root);
            }

            slots = byKey.Values.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            scannedDependencyHash = AssetDatabase.GetAssetDependencyHash(path);
            hasScanned = true;
        }
        catch (Exception exception)
        {
            scanProblems.Add(new ScanProblem(
                IssueKind.ScanFailure,
                path,
                "扫描失败：" + exception.Message));
            Debug.LogException(exception);
        }
        finally
        {
            isBusy = false;
            Repaint();
        }
    }

    private static void AddUsage(
        Dictionary<string, ImageSlot> byKey,
        UnityEngine.Object source,
        SourceKind sourceKind,
        GameObject gameObject,
        string path,
        string componentProperty,
        GameObject prefabRoot)
    {
        var key = GetAssetKey(source);
        ImageSlot slot;
        if (!byKey.TryGetValue(key, out slot))
        {
            slot = new ImageSlot(key, source, sourceKind);
            byKey.Add(key, slot);
        }

        slot.Usages.Add(new ImageUsage(
            path,
            componentProperty,
            IsDirectlyWritable(gameObject, prefabRoot)));
    }

    private void PrepareApply()
    {
        if (prefab == null || slots.Count == 0)
        {
            return;
        }

        if (scanProblems.Any(problem => problem.Kind == IssueKind.ScanFailure))
        {
            EditorUtility.DisplayDialog(
                "无法生成替换 Prefab",
                "本次扫描失败，不能使用不完整的扫描结果执行替换。请重新扫描并确认没有扫描错误后再试。",
                "确定");
            return;
        }

        var issues = BuildReviewIssues();
        lastReviewIssueCount = issues.Count;
        var actionableIssues = issues
            .Where(issue => !string.IsNullOrEmpty(issue.SlotKey))
            .ToList();

        if (actionableIssues.Count > 0)
        {
            IssueReviewWindow.Show(actionableIssues, (acknowledged, selectedIssueSlots) =>
            {
                if (acknowledged)
                {
                    ConfirmAndApply(selectedIssueSlots);
                }
            });
        }
        else
        {
            ConfirmAndApply(new HashSet<string>(StringComparer.Ordinal));
        }
    }

    private void ConfirmAndApply(HashSet<string> selectedIssueSlots)
    {
        if (saveMode == SaveMode.ModifyOriginal
            && !EditorUtility.DisplayDialog(
                "确认修改当前 Prefab",
                "这会直接覆盖当前 Prefab 文件，原文件不会保留为独立副本。确定继续吗？",
                "覆盖并执行",
                "取消"))
        {
            return;
        }

        PerformApply(selectedIssueSlots);
    }

    private List<ReviewIssue> BuildReviewIssues()
    {
        var issues = new List<ReviewIssue>();
        foreach (var problem in scanProblems)
        {
            issues.Add(new ReviewIssue(problem.Kind, problem.Path, problem.Message));
        }

        foreach (var slot in slots.Where(s => s.Replacement != null))
        {
            if (slot.HasResolutionMismatch)
            {
                issues.Add(new ReviewIssue(
                    IssueKind.ResolutionMismatch,
                    slot.DisplayName,
                    string.Format(
                        "原图分辨率 {0}，槽位图分辨率 {1}（分辨率不一致，请确认后继续）",
                        slot.OriginalResolutionText,
                        slot.ReplacementResolutionText),
                    slot.Key));
            }

            var readOnlyUsages = slot.Usages.Where(u => !u.Writable).ToList();
            if (readOnlyUsages.Count > 0)
            {
                issues.Add(new ReviewIssue(
                    IssueKind.NestedPrefabReadOnly,
                    slot.DisplayName,
                    string.Format(
                        "有 {0} 个引用位于嵌套 Prefab，无法直接写回（这些引用将跳过）：{1}",
                        readOnlyUsages.Count,
                        string.Join("、", readOnlyUsages.Select(u => u.Path).ToArray())),
                    slot.Key));
            }
        }

        return issues;
    }

    private void DrawProblemDetails(List<ReviewIssue> issues)
    {
        problemDetailsExpanded = EditorGUILayout.Foldout(
            problemDetailsExpanded,
            string.Format("问题明细（{0}）", issues.Count),
            true);
        if (!problemDetailsExpanded)
        {
            return;
        }

        var height = Mathf.Min(220f, Mathf.Max(64f, issues.Count * 34f));
        problemScrollPosition = EditorGUILayout.BeginScrollView(
            problemScrollPosition,
            EditorStyles.helpBox,
            GUILayout.Height(height));
        foreach (var issue in issues)
        {
            EditorGUILayout.LabelField(
                string.Format("[{0}] {1}\n{2}", GetIssueKindLabel(issue.Kind), issue.Path, issue.Message),
                EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(4f);
        }

        EditorGUILayout.EndScrollView();
    }

    private string BuildIssueSummary(List<ReviewIssue> issues)
    {
        return string.Format(
            "当前共 {0} 个问题项（已配置替换图槽位）：Image 空/丢失 {1}，RawImage 空/丢失 {2}，分辨率不一致 {3}，嵌套只读槽位 {4}，扫描失败 {5}。未配置替换图 {6} 个，将自动跳过。",
            issues.Count,
            CountIssues(issues, IssueKind.ImageMissingReference),
            CountIssues(issues, IssueKind.RawImageMissingReference),
            CountIssues(issues, IssueKind.ResolutionMismatch),
            CountIssues(issues, IssueKind.NestedPrefabReadOnly),
            CountIssues(issues, IssueKind.ScanFailure),
            GetUnconfiguredSlotCount());
    }

    private int GetUnconfiguredSlotCount()
    {
        return slots.Count(slot => slot.Replacement == null);
    }

    private static int CountIssues(List<ReviewIssue> issues, IssueKind kind)
    {
        return issues.Count(issue => issue.Kind == kind);
    }

    private static string GetIssueKindLabel(IssueKind kind)
    {
        switch (kind)
        {
            case IssueKind.ImageMissingReference:
                return "Image 空/丢失";
            case IssueKind.RawImageMissingReference:
                return "RawImage 空/丢失";
            case IssueKind.MissingReplacement:
                return "未配置槽位";
            case IssueKind.ResolutionMismatch:
                return "分辨率不一致";
            case IssueKind.NestedPrefabReadOnly:
                return "嵌套只读";
            case IssueKind.ScanFailure:
                return "扫描失败";
            default:
                return "其它";
        }
    }

    private void PerformApply(HashSet<string> selectedIssueSlots)
    {
        if (isBusy)
        {
            return;
        }

        var prefabPath = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(prefabPath))
        {
            return;
        }

        var skippedIssueSlots = new HashSet<string>(
            BuildReviewIssues()
                .Where(issue => !string.IsNullOrEmpty(issue.SlotKey))
                .Select(issue => issue.SlotKey)
                .Where(slotKey => selectedIssueSlots == null || !selectedIssueSlots.Contains(slotKey)),
            StringComparer.Ordinal);
        var replacementByKey = slots
            .Where(s => s.Replacement != null && !skippedIssueSlots.Contains(s.Key))
            .ToDictionary(s => s.Key, s => s.Replacement, StringComparer.Ordinal);

        isBusy = true;
        PrefabOperationContext context = null;
        try
        {
            context = PrefabOperationContext.Open(prefab, prefabPath);
            var root = context.Root;
            var replacedSlots = new HashSet<string>(StringComparer.Ordinal);
            var replacedNodes = 0;

            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                var original = image.sprite;
                Sprite replacement;
                if (original == null || !replacementByKey.TryGetValue(GetAssetKey(original), out replacement)
                    || replacement == null || !IsDirectlyWritable(image.gameObject, root))
                {
                    continue;
                }

                image.sprite = replacement;
                EditorUtility.SetDirty(image);
                replacedSlots.Add(GetAssetKey(original));
                replacedNodes++;
            }

            foreach (var rawImage in root.GetComponentsInChildren<RawImage>(true))
            {
                var original = rawImage.texture;
                Sprite replacement;
                if (original == null || !replacementByKey.TryGetValue(GetAssetKey(original), out replacement)
                    || replacement == null || replacement.texture == null
                    || !IsDirectlyWritable(rawImage.gameObject, root))
                {
                    continue;
                }

                rawImage.texture = replacement.texture;
                EditorUtility.SetDirty(rawImage);
                replacedSlots.Add(GetAssetKey(original));
                replacedNodes++;
            }

            if (replacedNodes == 0)
            {
                EditorUtility.DisplayDialog(
                    "没有可替换的引用",
                    "没有配置有效替换图，或确认窗口中未选择任何异常槽位，或所有引用都属于只读嵌套 Prefab。",
                    "确定");
                return;
            }

            var outputPath = GetOutputPath(prefabPath);
            bool saveSucceeded;
            GameObject savedPrefab;
            if (saveMode == SaveMode.ModifyOriginal
                && PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant)
            {
                PrefabUtility.ApplyPrefabInstance(root, InteractionMode.UserAction);
                savedPrefab = prefab;
                saveSucceeded = true;
            }
            else
            {
                savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, outputPath, out saveSucceeded);
            }
            if (!saveSucceeded || savedPrefab == null)
            {
                EditorUtility.DisplayDialog("生成失败", "Unity 未能保存新 Prefab。请检查输出路径和资源状态。", "确定");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = savedPrefab;
            EditorGUIUtility.PingObject(savedPrefab);
            EditorUtility.DisplayDialog(
                saveMode == SaveMode.ModifyOriginal ? "修改完成" : "生成完成",
                string.Format("{0}：{1}\n替换槽位：{2}\n受影响节点：{3}\n问题项：{4}",
                    saveMode == SaveMode.ModifyOriginal ? "目标" : "输出",
                    outputPath,
                    replacedSlots.Count,
                    replacedNodes,
                    lastReviewIssueCount),
                "确定");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorUtility.DisplayDialog("生成失败", exception.Message, "确定");
        }
        finally
        {
            if (context != null)
            {
                context.Dispose();
            }

            isBusy = false;
            ScanPrefab();
        }
    }

    private static bool IsDirectlyWritable(GameObject gameObject, GameObject prefabRoot)
    {
        if (gameObject == null)
        {
            return false;
        }

        var nearestInstanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
        if (nearestInstanceRoot == null)
        {
            return true;
        }

        return nearestInstanceRoot == prefabRoot;
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

    private static string GetAssetKey(UnityEngine.Object source)
    {
        if (source == null)
        {
            return "<missing>";
        }

        string guid;
        long localId;
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out guid, out localId))
        {
            return guid + ":" + localId;
        }

        try
        {
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(source);
            if (globalId.identifierType != 0)
            {
                return "global:" + globalId;
            }
        }
        catch (Exception)
        {
            // Some built-in objects do not expose a GlobalObjectId.
        }

        var path = AssetDatabase.GetAssetPath(source);
        return path + "#" + source.GetInstanceID();
    }

    private static string GetNextOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = "Assets";
        }

        directory = directory.Replace('\\', '/');
        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = string.Format("{0}/{1}{2}.prefab", directory, sourceName, OutputSuffix);
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = string.Format("{0}/{1}{2}_{3:00}.prefab", directory, sourceName, OutputSuffix, index++);
        }

        return candidate;
    }

    private string GetOutputPath(string sourcePath)
    {
        return saveMode == SaveMode.ModifyOriginal
            ? sourcePath
            : GetNextOutputPath(sourcePath);
    }

    private sealed class PrefabOperationContext : IDisposable
    {
        public readonly GameObject Root;
        private readonly Scene scene;
        private readonly Scene previousActiveScene;
        private readonly bool isIsolatedContents;

        private PrefabOperationContext(
            GameObject root,
            Scene scene,
            Scene previousActiveScene,
            bool isIsolatedContents)
        {
            Root = root;
            this.scene = scene;
            this.previousActiveScene = previousActiveScene;
            this.isIsolatedContents = isIsolatedContents;
        }

        public static PrefabOperationContext Open(GameObject prefabAsset, string assetPath)
        {
            if (PrefabUtility.GetPrefabAssetType(prefabAsset) == PrefabAssetType.Variant)
            {
                var previousActiveScene = SceneManager.GetActiveScene();
                var tempScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                var instance = PrefabUtility.InstantiatePrefab(prefabAsset, tempScene) as GameObject;
                if (instance == null)
                {
                    EditorSceneManager.CloseScene(tempScene, true);
                    throw new InvalidOperationException("无法实例化 Prefab Variant。");
                }

                return new PrefabOperationContext(instance, tempScene, previousActiveScene, false);
            }

            return new PrefabOperationContext(
                PrefabUtility.LoadPrefabContents(assetPath),
                default(Scene),
                default(Scene),
                true);
        }

        public void Dispose()
        {
            if (isIsolatedContents)
            {
                if (Root != null)
                {
                    PrefabUtility.UnloadPrefabContents(Root);
                }

                return;
            }

            if (Root != null)
            {
                UnityEngine.Object.DestroyImmediate(Root);
            }

            if (scene.IsValid())
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            if (previousActiveScene.IsValid())
            {
                SceneManager.SetActiveScene(previousActiveScene);
            }
        }
    }

    private enum SaveMode
    {
        CreateCopy,
        ModifyOriginal
    }

    private enum SourceKind
    {
        Sprite,
        Texture
    }

    private enum IssueKind
    {
        ImageMissingReference,
        RawImageMissingReference,
        MissingReplacement,
        ResolutionMismatch,
        NestedPrefabReadOnly,
        ScanFailure
    }

    private sealed class ImageSlot
    {
        public readonly string Key;
        public readonly UnityEngine.Object Source;
        public readonly SourceKind SourceKind;
        public readonly List<ImageUsage> Usages = new List<ImageUsage>();
        public Sprite Replacement;
        public bool UsagesExpanded;

        public ImageSlot(string key, UnityEngine.Object source, SourceKind sourceKind)
        {
            Key = key;
            Source = source;
            SourceKind = sourceKind;
        }

        public string DisplayName { get { return Source == null ? "<missing>" : Source.name; } }

        public string SourcePath
        {
            get
            {
                var path = Source == null ? string.Empty : AssetDatabase.GetAssetPath(Source);
                return string.IsNullOrEmpty(path) ? "（非项目资源）" : path;
            }
        }

        public string OriginalResolutionText
        {
            get { return FormatResolution(GetResolution(Source, SourceKind == SourceKind.Sprite)); }
        }

        public string ReplacementResolutionText
        {
            get
            {
                if (Replacement == null)
                {
                    return "未配置";
                }

                return FormatResolution(GetResolution(Replacement, SourceKind == SourceKind.Sprite));
            }
        }

        public bool HasResolutionMismatch
        {
            get
            {
                if (Replacement == null)
                {
                    return false;
                }

                return GetResolution(Source, SourceKind == SourceKind.Sprite) != GetResolution(
                    Replacement,
                    SourceKind == SourceKind.Sprite);
            }
        }

        private static Vector2Int GetResolution(UnityEngine.Object source, bool useSpriteRegion)
        {
            if (source == null)
            {
                return Vector2Int.zero;
            }

            if (useSpriteRegion)
            {
                var sprite = source as Sprite;
                if (sprite != null)
                {
                    return new Vector2Int(
                        Mathf.RoundToInt(sprite.rect.width),
                        Mathf.RoundToInt(sprite.rect.height));
                }
            }

            var sourceSprite = source as Sprite;
            if (sourceSprite != null)
            {
                var spriteTexture = sourceSprite.texture;
                return spriteTexture == null
                    ? Vector2Int.zero
                    : new Vector2Int(spriteTexture.width, spriteTexture.height);
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
    }

    private sealed class ImageUsage
    {
        public readonly string Path;
        public readonly string ComponentProperty;
        public readonly bool Writable;

        public ImageUsage(string path, string componentProperty, bool writable)
        {
            Path = path;
            ComponentProperty = componentProperty;
            Writable = writable;
        }
    }

    private sealed class ScanProblem
    {
        public readonly IssueKind Kind;
        public readonly string Path;
        public readonly string Message;

        public ScanProblem(IssueKind kind, string path, string message)
        {
            Kind = kind;
            Path = path;
            Message = message;
        }
    }

    private sealed class ReviewIssue
    {
        public readonly IssueKind Kind;
        public readonly string Path;
        public readonly string Message;
        public readonly string SlotKey;

        public ReviewIssue(IssueKind kind, string path, string message, string slotKey = null)
        {
            Kind = kind;
            Path = path;
            Message = message;
            SlotKey = slotKey;
        }
    }

    private sealed class IssueReviewWindow : EditorWindow
    {
        private static Action<bool, HashSet<string>> completion;
        private List<IssueGroup> groups;
        private Vector2 scroll;
        private int selectionAnchor = -1;
        private bool isFinishing;

        public static void Show(List<ReviewIssue> reviewIssues, Action<bool, HashSet<string>> onComplete)
        {
            var window = CreateInstance<IssueReviewWindow>();
            window.groups = reviewIssues
                .Where(issue => !string.IsNullOrEmpty(issue.SlotKey))
                .GroupBy(issue => issue.SlotKey, StringComparer.Ordinal)
                .Select(group => new IssueGroup(
                    group.Key,
                    group.First().Path,
                    string.Join(
                        "\n",
                        group.Select(issue => "[" + GetIssueKindLabel(issue.Kind) + "] " + issue.Message).ToArray())))
                .ToList();
            completion = onComplete;
            window.titleContent = new GUIContent("确认问题槽位");
            window.minSize = new Vector2(520f, 320f);
            window.ShowModalUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("执行前确认问题槽位", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                string.Format(
                    "发现 {0} 个异常槽位。点击选择，Shift 选择区间，Ctrl/Command 追加或取消单项；未选中的异常槽位会跳过。未配置替换图的槽位不会进入此列表。",
                    groups.Count),
                MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全选", GUILayout.Width(80f)))
                {
                    foreach (var group in groups)
                    {
                        group.Acknowledged = true;
                    }

                    selectionAnchor = -1;
                }

                if (GUILayout.Button("全不选", GUILayout.Width(80f)))
                {
                    ClearSelection();
                    selectionAnchor = -1;
                }

                GUILayout.Space(12f);
                EditorGUILayout.LabelField(
                    string.Format("已选择 {0}/{1}", groups.Count(group => group.Acknowledged), groups.Count),
                    EditorStyles.miniLabel);
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (var index = 0; index < groups.Count; index++)
            {
                DrawIssueGroup(groups[index], index);
                GUILayout.Space(5f);
            }

            EditorGUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("取消", GUILayout.Width(90f)))
                {
                    Finish(false);
                }

                if (GUILayout.Button("确认并继续", GUILayout.Width(110f)))
                {
                    Finish(true);
                }
            }
        }

        private void DrawIssueGroup(IssueGroup group, int index)
        {
            var fullText = group.Path + "\n" + group.Message;
            var content = new GUIContent(fullText, fullText);
            var availableWidth = Mathf.Max(160f, position.width - 72f);
            var textHeight = EditorStyles.wordWrappedLabel.CalcHeight(content, availableWidth);
            var rowHeight = Mathf.Max(28f, textHeight + 10f);
            var rowRect = GUILayoutUtility.GetRect(
                GUIContent.none,
                GUIStyle.none,
                GUILayout.Height(rowHeight),
                GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint && group.Acknowledged)
            {
                EditorGUI.DrawRect(rowRect, new Color(0.24f, 0.38f, 0.56f, 0.45f));
            }

            var toggleRect = new Rect(rowRect.x + 5f, rowRect.y + 5f, 18f, 18f);
            var textRect = new Rect(
                rowRect.x + 28f,
                rowRect.y + 4f,
                Mathf.Max(100f, rowRect.width - 33f),
                rowRect.height - 8f);
            GUI.Label(textRect, content, EditorStyles.wordWrappedLabel);
            if (Event.current.type == EventType.Repaint)
            {
                EditorStyles.toggle.Draw(toggleRect, GUIContent.none, false, false, group.Acknowledged, false);
            }

            if (GUI.Button(rowRect, GUIContent.none, GUIStyle.none))
            {
                ApplySelection(index, Event.current.shift, Event.current.control || Event.current.command);
            }
        }

        private void ApplySelection(int index, bool range, bool additive)
        {
            if (range && selectionAnchor >= 0)
            {
                var start = Mathf.Min(selectionAnchor, index);
                var end = Mathf.Max(selectionAnchor, index);
                if (!additive)
                {
                    ClearSelection();
                }

                for (var rangeIndex = start; rangeIndex <= end; rangeIndex++)
                {
                    groups[rangeIndex].Acknowledged = true;
                }
            }
            else if (additive)
            {
                groups[index].Acknowledged = !groups[index].Acknowledged;
                selectionAnchor = index;
            }
            else
            {
                ClearSelection();
                groups[index].Acknowledged = true;
                selectionAnchor = index;
            }

            Repaint();
        }

        private void ClearSelection()
        {
            foreach (var group in groups)
            {
                group.Acknowledged = false;
            }
        }

        private void Finish(bool accepted)
        {
            if (isFinishing)
            {
                return;
            }

            isFinishing = true;
            var callback = completion;
            completion = null;
            var selectedIssueSlots = accepted
                ? new HashSet<string>(
                    groups.Where(group => group.Acknowledged).Select(group => group.SlotKey),
                    StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            EditorApplication.delayCall += () =>
            {
                if (callback != null)
                {
                    callback(accepted, selectedIssueSlots);
                }
            };
            Close();
            GUIUtility.ExitGUI();
        }

        private void OnDestroy()
        {
            if (!isFinishing)
            {
                completion = null;
            }
        }

        private sealed class IssueGroup
        {
            public readonly string SlotKey;
            public readonly string Path;
            public readonly string Message;
            public bool Acknowledged;

            public IssueGroup(string slotKey, string path, string message)
            {
                SlotKey = slotKey;
                Path = path;
                Message = message;
            }
        }
    }
}
