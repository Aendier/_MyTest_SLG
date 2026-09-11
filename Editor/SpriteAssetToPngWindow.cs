using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Converts standalone Sprite .asset files into editable PNG Sprite assets and migrates their references.
/// </summary>
public sealed class SpriteAssetToPngWindow : EditorWindow
{
    private const string DefaultFolder = "Assets";
    private const long SpriteLocalId = 21300000L;
    private static readonly Regex GuidLineRegex = new Regex(
        "^guid:\\s*[0-9a-fA-F]{32}\\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex SerializedGuidRegex = new Regex(
        "guid:\\s*([0-9a-fA-F]{32})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SerializedSpriteReferenceRegex = new Regex(
        "\\{fileID:\\s*(-?[0-9]+),\\s*guid:\\s*([0-9a-fA-F]{32}),\\s*type:\\s*2\\s*\\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private string folderPath = DefaultFolder;
    private DefaultAsset folderObject;
    private Vector2 scrollPosition;
    private readonly Dictionary<string, Texture2D> previewCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ConversionCandidate> previewQueue = new Queue<ConversionCandidate>();
    private readonly HashSet<string> previewQueued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConversionCandidate> candidates = new List<ConversionCandidate>();
    private readonly List<ScanProblemItem> problemItems = new List<ScanProblemItem>();
    private readonly List<string> scanProblems = new List<string>();
    private readonly List<string> specialAssetReports = new List<string>();
    private int scannedAssetCount;
    private int layoutApproximationCount;
    private bool hasScanned;
    private bool isBusy;
    private string[] scanPaths = new string[0];
    private int scanIndex;
    private string[] referenceScanPaths = new string[0];
    private int referenceScanIndex;
    private int referenceFinalizeIndex;
    private int referencePrepareIndex;
    private bool referenceScanPrepared;
    private bool findReferenceReadyChecked;
    private bool findReferenceRefreshStarted;
    private readonly HashSet<string> targetedReferencePaths =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConversionCandidate> scanCandidatesByGuid =
        new Dictionary<string, ConversionCandidate>(StringComparer.OrdinalIgnoreCase);
    private string scanFolderLabel = string.Empty;
    private ConversionRun conversionRun;
    private string progressMessage = string.Empty;
    private float progressValue;

    [MenuItem("UIR/Sprite Asset To PNG")]
    public static void Open()
    {
        GetWindow<SpriteAssetToPngWindow>("Sprite Asset To PNG");
    }

    private void OnEnable()
    {
        SetFolderPath(folderPath);
    }

    private void OnDisable()
    {
        EditorApplication.update -= ScanStep;
        EditorApplication.update -= ConversionStep;
        EditorApplication.update -= PreviewStep;
        ClearPreviewCache();
        if (isBusy)
        {
            isBusy = false;
            EditorUtility.ClearProgressBar();
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Sprite .asset 批量转 PNG", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "将 Sprite .asset 变成设计师可直接编辑的单图 PNG：完整同名源图直接复用，图集切片按 Sprite Rect 导出。预检使用 FindReference2 定位直接引用文件并定向检查；源图集始终保留。转换前自动备份，逐项失败逐项回滚。",
            MessageType.Info);
        EditorGUI.BeginDisabledGroup(isBusy);
        DrawFolderSelection();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("预检", GUILayout.Height(28f))) Scan();
            EditorGUI.BeginDisabledGroup(!hasScanned || candidates.All(candidate => candidate.Blocked));
            if (GUILayout.Button("转换全部可转换项", GUILayout.Height(28f))) ConvertAll();
            EditorGUI.EndDisabledGroup();
        }
        EditorGUI.EndDisabledGroup();
        if (isBusy)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(progressMessage, EditorStyles.miniLabel);
            var progressRect = GUILayoutUtility.GetRect(1f, 18f);
            EditorGUI.ProgressBar(progressRect, progressValue, Mathf.RoundToInt(progressValue * 100f) + "%");
        }
        DrawScanResultReadable();
    }

    private void DrawFolderSelection()
    {
        EditorGUI.BeginChangeCheck();
        var selectedFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("目录", "拖入 Assets 下的目录"),
            folderObject,
            typeof(DefaultAsset),
            false);
        if (EditorGUI.EndChangeCheck())
        {
            var selectedPath = selectedFolder == null ? string.Empty : AssetDatabase.GetAssetPath(selectedFolder);
            if (AssetDatabase.IsValidFolder(selectedPath))
            {
                folderObject = selectedFolder;
                folderPath = selectedPath;
                ClearScan();
            }
        }

        EditorGUI.BeginChangeCheck();
        var enteredPath = EditorGUILayout.TextField(
            new GUIContent("路径", "支持 Assets/... 或项目 Assets 目录内的绝对路径"),
            folderPath);
        if (EditorGUI.EndChangeCheck())
        {
            folderPath = enteredPath.Trim();
            folderObject = null;
            ClearScan();
        }
    }

    private void DrawScanResultReadable()
    {
        if (!hasScanned) return;
        EditorGUILayout.Space();
        var itemWarningCount = problemItems.Count + candidates.Sum(candidate => candidate.Warnings.Count);
        var itemNoteCount = candidates.Sum(candidate => candidate.Notes.Count);
        EditorGUILayout.LabelField(
            string.Format("扫描 .asset：{0}    可转换：{1}    复用单图：{2}    图集导出：{3}    覆盖 PNG：{4}    问题：{5}    条目警告：{6}    条目说明：{7}",
                scannedAssetCount, candidates.Count(c => !c.Blocked),
                candidates.Count(c => c.ReuseExistingPng && !c.Blocked),
                candidates.Count(c => !c.ReuseExistingPng && !c.OverwriteExistingPng && !c.Blocked),
                candidates.Count(c => c.OverwriteExistingPng && !c.Blocked),
                scanProblems.Count + specialAssetReports.Count, itemWarningCount, itemNoteCount),
            EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox(
            "快速预检依赖 FindReference2 缓存，只覆盖资源直接引用；代码或序列化字符串中的 .asset 路径不会自动迁移。",
            MessageType.Warning);
        if (scanProblems.Count > 0)
        {
            EditorGUILayout.HelpBox(string.Join("\n", scanProblems.Take(10).ToArray()), MessageType.Warning);
        }
        if (layoutApproximationCount > 0)
        {
            EditorGUILayout.HelpBox("有 " + layoutApproximationCount + " 个 Sprite 带 m_Offset；输出保留裁切图和 Pivot，但布局只做近似，建议转换后抽查 UI。", MessageType.Warning);
        }
        if (specialAssetReports.Count > 0)
        {
            EditorGUILayout.HelpBox("特殊资源（仅报告并保留）：\n" + string.Join("\n", specialAssetReports.Take(10).ToArray()), MessageType.Info);
        }
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
        foreach (var problem in problemItems)
        {
            DrawProblemItem(problem);
        }
        foreach (var candidate in candidates)
        {
            DrawCandidatePreview(candidate);
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
            var size = sprite == null ? "无法读取" : string.Format("{0} x {1}{2}",
                Mathf.RoundToInt(sprite.rect.width), Mathf.RoundToInt(sprite.rect.height),
                candidate.ApproximatePivot ? "，Pivot 将归一化" : string.Empty);
            EditorGUILayout.LabelField(Path.GetFileName(candidate.AssetPath), size, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(candidate.ActionLabel + "    序列化引用：" + candidate.SerializedReferenceCount +
                                       " 处    FR2：" + candidate.Fr2DirectReferencerCount + " 个文件",
                EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField(candidate.AssetPath + " -> " + candidate.OutputPath, EditorStyles.miniLabel);
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawCandidatePreview(ConversionCandidate candidate)
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
        var preview = GetPreview(candidate, sprite);
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(78f)))
        {
            if (GUILayout.Button(new GUIContent(preview, preview == null ? "预览加载中" : string.Empty),
                GUILayout.Width(72f), GUILayout.Height(72f)))
            {
                LocateCandidate(candidate);
            }
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField("点击缩略图定位图片", EditorStyles.miniLabel);
            }
        }
        if (candidate.Notes.Count > 0)
        {
            EditorGUILayout.HelpBox(string.Join("\n", candidate.Notes.ToArray()), MessageType.Info);
        }
        if (candidate.Warnings.Count > 0)
        {
            EditorGUILayout.HelpBox(string.Join("\n", candidate.Warnings.ToArray()), MessageType.Warning);
        }
    }

    private static void DrawProblemItem(ScanProblemItem problem)
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.MinHeight(34f)))
        {
            if (GUILayout.Button("⚠", GUILayout.Width(28f), GUILayout.Height(24f)))
            {
                LocateAssetPath(problem.AssetPath);
            }
            using (new EditorGUILayout.VerticalScope())
            {
                if (GUILayout.Button(Path.GetFileName(problem.AssetPath), EditorStyles.linkLabel))
                {
                    LocateAssetPath(problem.AssetPath);
                }
                EditorGUILayout.LabelField(problem.AssetPath, EditorStyles.miniLabel);
            }
        }
        EditorGUILayout.HelpBox(problem.Message, MessageType.Warning);
    }

    private Texture2D GetPreview(ConversionCandidate candidate, Sprite sprite)
    {
        if (sprite == null) return null;
        Texture2D preview;
        if (previewCache.TryGetValue(candidate.AssetPath, out preview) && preview != null) return preview;
        if (previewQueued.Add(candidate.AssetPath))
        {
            previewQueue.Enqueue(candidate);
            EditorApplication.update -= PreviewStep;
            EditorApplication.update += PreviewStep;
        }
        return preview;
    }

    private void PreviewStep()
    {
        if (previewQueue.Count == 0)
        {
            EditorApplication.update -= PreviewStep;
            return;
        }

        var candidate = previewQueue.Dequeue();
        previewQueued.Remove(candidate.AssetPath);
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
        if (sprite != null)
        {
            try
            {
                var preview = CreatePreviewTexture(sprite);
                if (preview != null)
                {
                    previewCache[candidate.AssetPath] = preview;
                }
                else
                {
                    AddCandidateWarning(candidate, "预览生成失败：无法解码裁切后的像素。");
                }
            }
            catch (Exception exception)
            {
                AddCandidateWarning(candidate, "预览生成失败：" + exception.Message);
                Debug.LogWarning("[Sprite Asset To PNG] 预览生成失败：" + candidate.AssetPath + "，" + exception.Message);
            }
        }
        Repaint();
    }

    private static void AddCandidateWarning(ConversionCandidate candidate, string warning)
    {
        if (candidate == null || string.IsNullOrEmpty(warning) || candidate.Warnings.Contains(warning)) return;
        candidate.Warnings.Add(warning);
    }

    private static void AddCandidateNote(ConversionCandidate candidate, string note)
    {
        if (candidate == null || string.IsNullOrEmpty(note) || candidate.Notes.Contains(note)) return;
        candidate.Notes.Add(note);
    }

    private static void AddConversionWarnings(ConversionCandidate candidate)
    {
        if (candidate.ApproximatePivot)
        {
            AddCandidateWarning(candidate, "Pivot 超出 0~1，转换时会归一化到 0~1。");
        }
        if (candidate.HasOffset)
        {
            AddCandidateWarning(candidate, "检测到 m_Offset，裁切后的布局只做近似，请转换后抽查 UI。");
        }
        if (candidate.Blocked)
        {
            AddCandidateWarning(candidate, "该条目已被预检阻断，不会自动合并或删除原 .asset。");
        }
    }

    private static void ConfigureExistingPngCandidate(ConversionCandidate candidate)
    {
        if (candidate == null) return;

        var desired = Path.ChangeExtension(candidate.AssetPath, ".png").Replace('\\', '/');
        var desiredAbsolute = AssetPathToAbsolute(desired);
        var sourceSprite = AssetDatabase.LoadAssetAtPath<Sprite>(candidate.AssetPath);
        if (sourceSprite == null || sourceSprite.texture == null)
        {
            candidate.Blocked = true;
            candidate.ActionLabel = "阻断：源纹理缺失";
            AddCandidateWarning(candidate, "Sprite 的像素只存在于源纹理中；源纹理缺失时无法从 .asset 自身重建 PNG。");
            return;
        }

        candidate.OutputPath = desired;
        var hasExistingPng = File.Exists(desiredAbsolute);
        if (hasExistingPng)
        {
            var desiredMetaPath = desiredAbsolute + ".meta";
            if (File.Exists(desiredMetaPath))
            {
                try { candidate.ExistingPngGuid = ReadMetaGuid(desiredMetaPath); } catch (Exception) { }
            }
            if (string.IsNullOrEmpty(candidate.ExistingPngGuid))
            {
                candidate.Blocked = true;
                candidate.ActionLabel = "阻断：同名 PNG 缺少 GUID";
                AddCandidateWarning(candidate, "同名 PNG 缺少有效 meta/GUID，无法在保留其身份的前提下迁移引用。");
                return;
            }
            var existingImporter = AssetImporter.GetAtPath(desired) as TextureImporter;
            if (existingImporter == null)
            {
                candidate.Blocked = true;
                candidate.ActionLabel = "阻断：同名 PNG 导入器不可用";
                AddCandidateWarning(candidate, "同名 PNG 没有可用的 TextureImporter，无法安全对齐 Sprite 参数。");
                return;
            }
            if (existingImporter.spriteImportMode == SpriteImportMode.Multiple)
            {
                candidate.Blocked = true;
                candidate.ActionLabel = "阻断：同名 PNG 是多 Sprite";
                AddCandidateWarning(candidate, "同名 PNG 使用 Multiple Sprite；强制改为 Single 会使现有子 Sprite fileID 失效。");
                return;
            }
        }

        var sourceIsDesired = string.Equals(candidate.SourceTexturePath, desired, StringComparison.OrdinalIgnoreCase);
        if (sourceIsDesired && IsFullTextureSprite(sourceSprite))
        {
            candidate.ReuseExistingPng = true;
            candidate.ActionLabel = "复用单图";
            AddCandidateNote(candidate, "同名 PNG 是覆盖完整画布的 backing texture：保留 PNG 文件和 GUID，只对齐显示参数并迁移引用。");
        }
        else if (sourceIsDesired)
        {
            candidate.Blocked = true;
            candidate.ActionLabel = "阻断：输出与图集源相同";
            AddCandidateWarning(candidate, "Sprite 只占同名源纹理的一部分，直接覆盖会破坏同图集的其他切片。");
            return;
        }
        else if (hasExistingPng)
        {
            candidate.OverwriteExistingPng = true;
            candidate.ActionLabel = "从图集导出并覆盖同名 PNG";
            AddCandidateNote(candidate, "同名 PNG 已存在：将备份后覆盖像素，但保留它当前的 GUID。");
        }
        else
        {
            candidate.ActionLabel = "从图集导出新 PNG";
            AddCandidateNote(candidate, "将按完整 Sprite Rect 导出独立 PNG，不裁掉透明边缘。");
        }

        var settingDifferences = CompareSpriteImportSettings(sourceSprite, desired);
        if (settingDifferences.Count > 0)
        {
            candidate.SettingDifferences.AddRange(settingDifferences);
            AddCandidateNote(candidate, "将对齐 PNG 参数：" + string.Join("、", settingDifferences.ToArray()));
        }
    }

    private static void FinalizeCandidateReferenceChecks(ConversionCandidate candidate)
    {
        if (candidate == null) return;
        candidate.SerializedReferenceCount = candidate.MigratableReferenceCount;
        if (candidate.AllGuidReferenceCount != candidate.MigratableReferenceCount)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, string.Format(
                "发现 {0} 处旧 GUID 引用，其中只有 {1} 处是可迁移的 Sprite type:2 引用；已阻断该项。",
                candidate.AllGuidReferenceCount, candidate.MigratableReferenceCount));
        }

        var missingFromTargetedScan = candidate.Fr2DirectReferencePaths
            .Where(path => !candidate.AllGuidReferencePaths.Contains(path))
            .Take(4)
            .ToArray();
        if (missingFromTargetedScan.Length > 0)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate,
                "FindReference2 与定向 YAML 扫描结果不一致，请刷新缓存后重试：" +
                string.Join("、", missingFromTargetedScan));
        }
        if (candidate.Blocked && !candidate.ActionLabel.StartsWith("阻断：", StringComparison.Ordinal))
        {
            candidate.ActionLabel = "阻断：" + candidate.ActionLabel;
        }
    }

    private static bool IsFullTextureSprite(Sprite sprite)
    {
        if (sprite == null || sprite.texture == null) return false;
        var rect = sprite.packed ? sprite.textureRect : sprite.rect;
        return Mathf.RoundToInt(rect.x) == 0 && Mathf.RoundToInt(rect.y) == 0 &&
               Mathf.RoundToInt(rect.width) == sprite.texture.width &&
               Mathf.RoundToInt(rect.height) == sprite.texture.height;
    }

    private static bool TryCompareSpritePixels(Sprite sprite, string pngPath, out string difference)
    {
        difference = string.Empty;
        var absolutePngPath = AssetPathToAbsolute(pngPath);
        Texture2D actual = null;
        Texture2D expected = null;
        try
        {
            var pngBytes = File.ReadAllBytes(absolutePngPath);
            actual = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!actual.LoadImage(pngBytes, false))
            {
                difference = "PNG 无法解码";
                return false;
            }

            var rect = sprite.packed ? sprite.textureRect : sprite.rect;
            var width = Mathf.RoundToInt(rect.width);
            var height = Mathf.RoundToInt(rect.height);
            if (actual.width != width || actual.height != height)
            {
                difference = string.Format("尺寸为 {0}x{1}，期望 {2}x{3}", actual.width, actual.height, width, height);
                return false;
            }

            // When the PNG is the exact backing texture and the Sprite covers
            // the full image, the source bytes are the pixels being compared.
            if (string.Equals(sprite.texture == null ? string.Empty : AssetDatabase.GetAssetPath(sprite.texture), pngPath,
                    StringComparison.OrdinalIgnoreCase) &&
                Mathf.RoundToInt(rect.x) == 0 && Mathf.RoundToInt(rect.y) == 0 &&
                actual.width == sprite.texture.width && actual.height == sprite.texture.height)
            {
                return true;
            }

            var expectedBytes = ExtractPng(sprite);
            expected = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!expected.LoadImage(expectedBytes, false) || expected.width != actual.width || expected.height != actual.height)
            {
                difference = "无法解码 Sprite 裁切结果";
                return false;
            }

            var actualPixels = actual.GetPixels32();
            var expectedPixels = expected.GetPixels32();
            if (actualPixels.Length != expectedPixels.Length)
            {
                difference = "像素数量不一致";
                return false;
            }
            for (var i = 0; i < actualPixels.Length; i++)
            {
                if (!actualPixels[i].Equals(expectedPixels[i]))
                {
                    difference = "存在像素差异";
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception)
        {
            difference = exception.Message;
            return false;
        }
        finally
        {
            if (actual != null) DestroyImmediate(actual);
            if (expected != null) DestroyImmediate(expected);
        }
    }

    private static List<string> CompareSpriteImportSettings(Sprite sourceSprite, string pngPath)
    {
        var differences = new List<string>();
        if (!File.Exists(AssetPathToAbsolute(pngPath)))
        {
            differences.Add("新建 Sprite/Full Rect");
            return differences;
        }
        var importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
        if (importer == null)
        {
            differences.Add("TextureImporter 不存在");
            return differences;
        }
        if (importer.textureType != TextureImporterType.Sprite) differences.Add("TextureType");
        if (importer.spriteImportMode != SpriteImportMode.Single) differences.Add("SpriteImportMode");
        if (!Mathf.Approximately(importer.spritePixelsPerUnit, sourceSprite.pixelsPerUnit)) differences.Add("PPU");
        if (!Approximately(importer.spriteBorder, sourceSprite.border)) differences.Add("Border");
        if (sourceSprite.texture != null)
        {
            if (importer.filterMode != sourceSprite.texture.filterMode) differences.Add("FilterMode");
            if (importer.wrapMode != sourceSprite.texture.wrapMode) differences.Add("WrapMode");
            if (importer.anisoLevel != sourceSprite.texture.anisoLevel) differences.Add("AnisoLevel");
        }
        var sourceImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sourceSprite));
        if (sourceImporter != null)
        {
            if (!string.Equals(importer.assetBundleName, sourceImporter.assetBundleName, StringComparison.Ordinal) ||
                !string.Equals(importer.assetBundleVariant, sourceImporter.assetBundleVariant, StringComparison.Ordinal))
            {
                differences.Add("AssetBundle");
            }
        }

        var textureSettings = new TextureImporterSettings();
        importer.ReadTextureSettings(textureSettings);
        var sourcePivot = GetNormalizedPivot(sourceSprite);
        if (textureSettings.spriteMeshType != SpriteMeshType.FullRect) differences.Add("MeshType=Full Rect");
        if (textureSettings.spriteAlignment != (int)SpriteAlignment.Custom ||
            !Approximately(textureSettings.spritePivot, sourcePivot))
        {
            differences.Add("Pivot/Alignment");
        }
        return differences;
    }

    private static bool Approximately(Vector2 a, Vector2 b)
    {
        return Mathf.Abs(a.x - b.x) <= 0.0001f && Mathf.Abs(a.y - b.y) <= 0.0001f;
    }

    private static bool Approximately(Vector4 a, Vector4 b)
    {
        return Mathf.Abs(a.x - b.x) <= 0.0001f && Mathf.Abs(a.y - b.y) <= 0.0001f &&
               Mathf.Abs(a.z - b.z) <= 0.0001f && Mathf.Abs(a.w - b.w) <= 0.0001f;
    }

    private static Texture2D CreatePreviewTexture(Sprite sprite)
    {
        var pngBytes = ExtractPng(sprite);
        var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        RenderTexture renderTexture = null;
        Texture2D preview = null;
        var previous = RenderTexture.active;
        try
        {
            if (!decoded.LoadImage(pngBytes, false)) return null;
            const int maxPreviewSize = 128;
            var scale = Mathf.Min(1f, Mathf.Min((float)maxPreviewSize / decoded.width, (float)maxPreviewSize / decoded.height));
            var width = Math.Max(1, Mathf.RoundToInt(decoded.width * scale));
            var height = Math.Max(1, Mathf.RoundToInt(decoded.height * scale));
            renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(decoded, renderTexture);
            RenderTexture.active = renderTexture;
            preview = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            preview.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            preview.Apply(false, true);
            preview.hideFlags = HideFlags.HideAndDontSave;
            return preview;
        }
        finally
        {
            RenderTexture.active = previous;
            if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
            DestroyImmediate(decoded);
        }
    }

    private void ClearPreviewCache()
    {
        foreach (var preview in previewCache.Values)
        {
            if (preview != null) DestroyImmediate(preview);
        }
        previewCache.Clear();
        previewQueue.Clear();
        previewQueued.Clear();
    }

    private static void LocateCandidate(ConversionCandidate candidate)
    {
        var target = AssetDatabase.LoadMainAssetAtPath(candidate.OutputPath);
        if (target == null) target = AssetDatabase.LoadMainAssetAtPath(candidate.AssetPath);
        if (target == null) return;
        Selection.activeObject = target;
        EditorGUIUtility.PingObject(target);
    }

    private static void LocateAssetPath(string assetPath)
    {
        var target = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (target == null) return;
        Selection.activeObject = target;
        EditorGUIUtility.PingObject(target);
    }

    private void Scan()
    {
        if (isBusy) return;
        ClearScan();
        string normalizedFolder;
        string error;
        if (!TryNormalizeFolder(folderPath, out normalizedFolder, out error))
        {
            scanProblems.Add(error);
            hasScanned = true;
            return;
        }

        SetFolderPath(normalizedFolder);
        scanFolderLabel = normalizedFolder;
        string[] paths;
        try
        {
            paths = Directory.GetFiles(AssetPathToAbsolute(normalizedFolder), "*.asset", SearchOption.AllDirectories)
                .Select(AbsoluteToAssetPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            scanProblems.Add("扫描目录失败：" + exception.Message);
            hasScanned = true;
            return;
        }

        scanPaths = paths;
        scanIndex = 0;
        referenceScanPaths = new string[0];
        referenceScanIndex = 0;
        referenceFinalizeIndex = 0;
        referencePrepareIndex = 0;
        referenceScanPrepared = false;
        findReferenceReadyChecked = false;
        findReferenceRefreshStarted = false;
        targetedReferencePaths.Clear();
        scanCandidatesByGuid.Clear();
        scannedAssetCount = paths.Length;
        hasScanned = false;
        isBusy = true;
        EditorApplication.update -= ScanStep;
        EditorApplication.update += ScanStep;
        progressMessage = "准备扫描 " + normalizedFolder;
        progressValue = 0f;
        EditorUtility.DisplayProgressBar("Sprite 预检", "准备扫描 " + normalizedFolder, 0f);
        Repaint();
    }

    private void ScanStep()
    {
        if (!isBusy || scanPaths == null)
        {
            EditorApplication.update -= ScanStep;
            return;
        }

        try
        {
            if (scanIndex < scanPaths.Length)
            {
                var path = scanPaths[scanIndex++];
                ProcessScanPath(path);
                var progress = scanPaths.Length == 0 ? 0.35f : 0.35f * scanIndex / scanPaths.Length;
                progressMessage = "预检：正在检查 " + path;
                progressValue = progress;
                if (EditorUtility.DisplayCancelableProgressBar("Sprite 预检", "正在检查 " + path, progress))
                {
                    CancelScan();
                }
                Repaint();
                return;
            }

            if (!referenceScanPrepared)
            {
                if (!findReferenceReadyChecked)
                {
                    if (!candidates.Any(item => !item.Blocked))
                    {
                        findReferenceReadyChecked = true;
                        referencePrepareIndex = candidates.Count;
                    }
                    else
                    {
                        string findReferenceError;
                        if (!findReferenceRefreshStarted)
                        {
                            if (!TryStartFindReference2Refresh(out findReferenceError))
                            {
                                BlockAllConvertibleCandidatesForFindReferenceError(findReferenceError);
                                findReferenceReadyChecked = true;
                                referencePrepareIndex = candidates.Count;
                            }
                            else
                            {
                                findReferenceRefreshStarted = true;
                                progressMessage = "预检：正在刷新 FindReference2 增量缓存";
                                progressValue = 0.35f;
                                EditorUtility.DisplayProgressBar("Sprite 预检", progressMessage, progressValue);
                                Repaint();
                                return;
                            }
                        }
                        else if (!TryEnsureFindReference2Ready(out findReferenceError))
                        {
                            if (!findReferenceError.StartsWith(
                                    "FindReference2 缓存尚未就绪", StringComparison.Ordinal))
                            {
                                BlockAllConvertibleCandidatesForFindReferenceError(findReferenceError);
                                findReferenceReadyChecked = true;
                                referencePrepareIndex = candidates.Count;
                            }
                            else
                            {
                                progressMessage = "预检：等待 FindReference2 增量缓存完成";
                                progressValue = 0.35f;
                                if (EditorUtility.DisplayCancelableProgressBar(
                                        "Sprite 预检", progressMessage, progressValue))
                                {
                                    CancelScan();
                                }
                                Repaint();
                                return;
                            }
                        }
                        else
                        {
                            findReferenceReadyChecked = true;
                        }
                    }
                }

                if (referencePrepareIndex < candidates.Count)
                {
                    var candidate = candidates[referencePrepareIndex++];
                    if (!candidate.Blocked)
                    {
                        PrepareCandidateReferenceScan(candidate);
                    }
                    var prepareProgress = candidates.Count == 0
                        ? 0.5f
                        : 0.35f + 0.15f * referencePrepareIndex / candidates.Count;
                    progressMessage = "预检：FindReference2 查询 " + candidate.AssetPath;
                    progressValue = prepareProgress;
                    if (EditorUtility.DisplayCancelableProgressBar("Sprite 预检", progressMessage, prepareProgress))
                    {
                        CancelScan();
                    }
                    Repaint();
                    return;
                }

                referenceScanPaths = targetedReferencePaths
                    .Select(AssetPathToAbsolute)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                referenceScanIndex = 0;
                referenceFinalizeIndex = 0;
                referenceScanPrepared = true;
                progressMessage = "预检：准备检查 " + referenceScanPaths.Length + " 个 FR2 直接引用文件";
                progressValue = 0.5f;
                EditorUtility.DisplayProgressBar("Sprite 预检", progressMessage, progressValue);
                Repaint();
                return;
            }

            if (referenceScanIndex < referenceScanPaths.Length)
            {
                var startedAt = EditorApplication.timeSinceStartup;
                string currentPath = string.Empty;
                do
                {
                    currentPath = referenceScanPaths[referenceScanIndex++];
                    ProcessProjectReferencePath(currentPath);
                }
                while (referenceScanIndex < referenceScanPaths.Length &&
                       EditorApplication.timeSinceStartup - startedAt < 0.006d);

                var progress = referenceScanPaths.Length == 0
                    ? 0.9f
                    : 0.5f + 0.4f * referenceScanIndex / referenceScanPaths.Length;
                var currentAssetPath = string.IsNullOrEmpty(currentPath) ? "Assets" : AbsoluteToAssetPath(currentPath);
                progressMessage = "预检：正在检查 FR2 直接引用 " + currentAssetPath;
                progressValue = progress;
                if (EditorUtility.DisplayCancelableProgressBar("Sprite 预检", progressMessage, progress))
                {
                    CancelScan();
                }
                Repaint();
                return;
            }

            if (referenceFinalizeIndex < candidates.Count)
            {
                var candidate = candidates[referenceFinalizeIndex++];
                FinalizeCandidateReferenceChecks(candidate);
                AddConversionWarnings(candidate);
                var progress = candidates.Count == 0
                    ? 1f
                    : 0.9f + 0.1f * referenceFinalizeIndex / candidates.Count;
                progressMessage = "预检：正在汇总 " + candidate.AssetPath;
                progressValue = progress;
                if (EditorUtility.DisplayCancelableProgressBar("Sprite 预检", progressMessage, progress))
                {
                    CancelScan();
                }
                Repaint();
                return;
            }

            progressMessage = "预检完成";
            progressValue = 1f;
            hasScanned = true;
            isBusy = false;
            EditorApplication.update -= ScanStep;
            EditorUtility.ClearProgressBar();
            Repaint();
        }
        catch (Exception exception)
        {
            scanProblems.Add("预检失败：" + exception.Message);
            hasScanned = true;
            isBusy = false;
            EditorApplication.update -= ScanStep;
            EditorUtility.ClearProgressBar();
            Repaint();
        }
    }

    private void CancelScan()
    {
        scanProblems.Add("用户取消，预检结果不完整，所有候选项均已阻断。重新预检后才可转换。");
        foreach (var candidate in candidates)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "预检被取消，该条目不会转换。");
        }
        hasScanned = true;
        isBusy = false;
        EditorApplication.update -= ScanStep;
        EditorUtility.ClearProgressBar();
    }

    private void PrepareCandidateReferenceScan(ConversionCandidate candidate)
    {
        if (candidate == null || string.IsNullOrEmpty(candidate.Guid)) return;
        scanCandidatesByGuid[candidate.Guid] = candidate;

        string[] referencers;
        string error;
        if (!TryGetFindReference2DirectReferencers(candidate.Guid, out referencers, out error))
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "FindReference2 查询失败，预检已阻断：" + error);
            return;
        }

        var directPaths = NormalizeFindReferencePaths(candidate.AssetPath, referencers);
        candidate.Fr2DirectReferencerCount = directPaths.Length;
        foreach (var path in directPaths)
        {
            candidate.Fr2DirectReferencePaths.Add(path);
            if (IsUnitySerializedTextAsset(path))
            {
                targetedReferencePaths.Add(path);
            }
        }

        var unsupportedPaths = directPaths
            .Where(path => !IsUnitySerializedTextAsset(path))
            .Take(4)
            .ToArray();
        if (unsupportedPaths.Length > 0)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate,
                "FindReference2 检出无法安全文本迁移的直接引用：" + string.Join("、", unsupportedPaths));
        }
        AddCandidateNote(candidate, "FindReference2 检出 " + directPaths.Length + " 个直接引用文件。");

        if (string.IsNullOrEmpty(candidate.ExistingPngGuid)) return;

        string[] targetReferencers;
        string targetError;
        if (!TryGetFindReference2DirectReferencers(candidate.ExistingPngGuid, out targetReferencers, out targetError))
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "无法确认同名 PNG 的现有引用，预检已阻断：" + targetError);
            return;
        }

        var targetDirectPaths = NormalizeFindReferencePaths(candidate.AssetPath, targetReferencers);
        candidate.ExistingPngDirectReferencePaths.UnionWith(targetDirectPaths);
        if (candidate.OverwriteExistingPng && targetDirectPaths.Length > 0)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate,
                "同名 PNG 已被其他资源直接引用，覆盖像素和导入参数可能改变现有内容：" +
                string.Join("、", targetDirectPaths.Take(4).ToArray()));
        }
        else if (candidate.ReuseExistingPng && candidate.SettingDifferences.Count > 0 &&
                 targetDirectPaths.Length > 0)
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate,
                "同名 PNG 已被其他资源直接引用，且导入参数需要变化：" +
                string.Join("、", targetDirectPaths.Take(4).ToArray()));
        }
    }

    private void BlockAllConvertibleCandidatesForFindReferenceError(string error)
    {
        scanProblems.Add("FindReference2 不可用，快速预检已阻断：" + error);
        foreach (var candidate in candidates.Where(item => !item.Blocked))
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, "FindReference2 不可用，无法确认直接引用文件。");
        }
    }

    private static string[] NormalizeFindReferencePaths(string selfPath, IEnumerable<string> paths)
    {
        return (paths ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/'))
            .Where(path => !string.Equals(path, selfPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void BlockCandidatesForReferencePath(string assetPath, string message)
    {
        foreach (var candidate in candidates.Where(item => item.Fr2DirectReferencePaths.Contains(assetPath)))
        {
            candidate.Blocked = true;
            AddCandidateWarning(candidate, message);
        }
    }

    private void ProcessProjectReferencePath(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) || absolutePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var assetPath = AbsoluteToAssetPath(absolutePath);
        if (assetPath.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isYaml = IsUnitySerializedTextFile(absolutePath);
        if (!isYaml)
        {
            BlockCandidatesForReferencePath(assetPath, "FR2 直接引用文件不是可迁移的 Unity YAML：" + assetPath);
            return;
        }

        string content;
        try
        {
            content = File.ReadAllText(absolutePath);
        }
        catch (Exception exception)
        {
            AddScanProblemItem(assetPath, "读取 FR2 直接引用文件失败：" + exception.Message);
            BlockCandidatesForReferencePath(assetPath, "无法读取直接引用文件，预检已阻断：" + assetPath);
            return;
        }

        foreach (Match match in SerializedGuidRegex.Matches(content))
        {
            var guid = match.Groups[1].Value;
            ConversionCandidate candidate;
            if (scanCandidatesByGuid.TryGetValue(guid, out candidate))
            {
                candidate.AllGuidReferenceCount++;
                candidate.AllGuidReferencePaths.Add(assetPath);
            }
        }

        foreach (Match match in SerializedSpriteReferenceRegex.Matches(content))
        {
            ConversionCandidate candidate;
            long localId;
            if (!scanCandidatesByGuid.TryGetValue(match.Groups[2].Value, out candidate) ||
                !long.TryParse(match.Groups[1].Value, out localId) || localId != candidate.LocalId)
            {
                continue;
            }
            candidate.MigratableReferenceCount++;
            candidate.SerializedReferencePaths.Add(assetPath);
        }
    }

    private void ProcessScanPath(string path)
    {
        var sprite = AssetDatabase.LoadMainAssetAtPath(path) as Sprite;
        if (sprite == null)
        {
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            if (mainAsset != null && string.Equals(mainAsset.GetType().Name, "TMP_SpriteAsset", StringComparison.Ordinal))
            {
                specialAssetReports.Add(path + "（TMP SpriteAsset，图集与字符映射不自动拆分）");
            }
            return;
        }
        if (sprite.texture == null)
        {
            AddScanProblemItem(path, "Sprite 没有源纹理，已跳过。");
            return;
        }
        var sourceTexturePath = AssetDatabase.GetAssetPath(sprite.texture);
        if (!sourceTexturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            AddScanProblemItem(path, "源纹理不是 PNG，已跳过。");
            return;
        }
        if (sprite.packed && sprite.packingRotation != SpritePackingRotation.None)
        {
            AddScanProblemItem(path, "Sprite 使用了旋转图集，已跳过以避免方向错误。");
            return;
        }
        string guid;
        long localId;
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out guid, out localId) || string.IsNullOrEmpty(guid))
        {
            AddScanProblemItem(path, "无法读取资源 GUID，已跳过。");
            return;
        }
        if (localId != SpriteLocalId)
        {
            AddScanProblemItem(path, "fileID 不是 21300000，无法保证引用不变，已跳过。");
            return;
        }
        var normalizedPivot = GetNormalizedPivot(sprite);
        var hasOffset = HasSerializedOffset(path);
        if (hasOffset) layoutApproximationCount++;
        var outputPath = GetOutputPath(path);
        var candidate = new ConversionCandidate
        {
            AssetPath = path,
            Guid = guid,
            LocalId = localId,
            SourceTexturePath = sourceTexturePath,
            OutputPath = outputPath,
            NormalizedPivot = normalizedPivot,
            ApproximatePivot = RequiresPivotNormalization(normalizedPivot),
            HasOffset = hasOffset,
            Packed = sprite.packed
        };
        ConfigureExistingPngCandidate(candidate);
        candidates.Add(candidate);
    }

    private void ConvertAll()
    {
        if (isBusy || !hasScanned || candidates.Count == 0) return;
        var convertibleCount = candidates.Count(candidate => !candidate.Blocked);
        if (convertibleCount == 0) return;
        if (!EditorUtility.DisplayDialog(
                "确认批量转换",
                string.Format("将处理 {0} 个 Sprite .asset（另有 {1} 个条目因预检冲突而保留）。工具会逐帧执行，每项完成后更新进度；原文件备份到 Library/SpriteAssetToPngBackups，源图集始终保留。是否继续？", convertibleCount, candidates.Count - convertibleCount),
                "转换", "取消"))
        {
            return;
        }

        var batchName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        conversionRun = new ConversionRun
        {
            BackupRoot = Path.Combine(ProjectRoot, "Library", "SpriteAssetToPngBackups", batchName),
            Pending = candidates.Where(candidate => !candidate.Blocked).ToArray(),
            Successes = new List<string>(),
            Failures = new List<string>()
        };
        isBusy = true;
        EditorApplication.update -= ConversionStep;
        EditorApplication.update += ConversionStep;
        progressMessage = "准备转换 " + conversionRun.Pending.Length + " 个资源";
        progressValue = 0f;
        EditorUtility.DisplayProgressBar("Sprite 转 PNG", "准备转换 " + conversionRun.Pending.Length + " 个资源", 0f);
        Repaint();
    }

    private void ConversionStep()
    {
        var run = conversionRun;
        if (!isBusy || run == null)
        {
            EditorApplication.update -= ConversionStep;
            return;
        }

        try
        {
            if (!run.FindReferenceRefreshStarted)
            {
                string refreshError;
                if (!TryStartFindReference2Refresh(out refreshError))
                {
                    run.Failures.Add("转换前刷新 FindReference2 失败：" + refreshError);
                    FinishConversion(run);
                    return;
                }
                run.FindReferenceRefreshStarted = true;
                progressMessage = "转换：正在刷新 FindReference2 增量缓存";
                progressValue = 0f;
                EditorUtility.DisplayProgressBar("Sprite 转 PNG", progressMessage, progressValue);
                Repaint();
                return;
            }

            string findReferenceError;
            if (!TryEnsureFindReference2Ready(out findReferenceError))
            {
                if (!findReferenceError.StartsWith("FindReference2 缓存尚未就绪", StringComparison.Ordinal))
                {
                    run.Failures.Add("转换前检查 FindReference2 失败：" + findReferenceError);
                    FinishConversion(run);
                    return;
                }
                progressMessage = "转换：等待 FindReference2 增量缓存完成";
                progressValue = run.Pending.Length == 0 ? 0f : (float)run.Index / run.Pending.Length;
                if (EditorUtility.DisplayCancelableProgressBar("Sprite 转 PNG", progressMessage, progressValue))
                {
                    run.Failures.Add("用户取消，剩余项目未处理。");
                    FinishConversion(run);
                }
                Repaint();
                return;
            }

            if (run.Index < run.Pending.Length)
            {
                var candidate = run.Pending[run.Index];
                var progress = run.Pending.Length == 0 ? 1f : (float)run.Index / run.Pending.Length;
                progressMessage = "转换：正在处理 " + candidate.AssetPath;
                progressValue = progress;
                if (EditorUtility.DisplayCancelableProgressBar("Sprite 转 PNG",
                    "正在转换 " + candidate.AssetPath + "（" + (run.Index + 1) + "/" + run.Pending.Length + "）", progress))
                {
                    run.Failures.Add("用户取消，剩余项目未处理。");
                    run.Index = run.Pending.Length;
                }
                else
                {
                    string outputPath;
                    string error;
                    if (TryConvert(candidate, run.BackupRoot, out outputPath, out error))
                    {
                        run.Successes.Add(candidate.AssetPath + " -> " + outputPath);
                        if (candidate.ReuseExistingPng) run.ReusedPng++;
                    }
                    else
                    {
                        AddCandidateWarning(candidate, "转换失败：" + error);
                        run.Failures.Add(candidate.AssetPath + "：" + error);
                    }
                    run.Index++;
                }
                Repaint();
                return;
            }

            FinishConversion(run);
        }
        catch (Exception exception)
        {
            run.Failures.Add("转换流程异常：" + exception.Message);
            FinishConversion(run);
        }
    }

    private void FinishConversion(ConversionRun run)
    {
        EditorApplication.update -= ConversionStep;
        EditorUtility.ClearProgressBar();
        isBusy = false;
        progressMessage = string.Empty;
        progressValue = 0f;
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

        var summary = new StringBuilder();
        summary.AppendLine("转换成功：" + run.Successes.Count);
        summary.AppendLine("复用现有 PNG 并删除旧 .asset：" + run.ReusedPng);
        summary.AppendLine("失败/未处理：" + run.Failures.Count);
        summary.AppendLine("源图集：全部保留");
        summary.AppendLine("备份：" + run.BackupRoot);
        if (run.Failures.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine(string.Join("\n", run.Failures.Take(12).ToArray()));
        }
        Debug.Log("[Sprite Asset To PNG]\n" + summary);
        EditorUtility.DisplayDialog("转换完成", summary.ToString(), "确定");
        conversionRun = null;
        Scan();
    }

    private static bool TryConvert(ConversionCandidate candidate, string backupRoot, out string outputPath, out string error)
    {
        var assetPath = candidate.AssetPath;
        outputPath = candidate.OutputPath;
        error = string.Empty;

        var sourceSprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        if (sourceSprite == null || sourceSprite.texture == null)
        {
            error = "无法读取 Sprite 或源纹理；.asset 本身不包含可恢复的 PNG 像素。";
            return false;
        }

        string oldGuid;
        long oldLocalId;
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sourceSprite, out oldGuid, out oldLocalId))
        {
            error = "无法读取原 .asset 的 GUID/fileID。";
            return false;
        }
        if (!string.Equals(oldGuid, candidate.Guid, StringComparison.OrdinalIgnoreCase) ||
            oldLocalId != candidate.LocalId)
        {
            error = "原 .asset 的 GUID/fileID 在预检后发生变化，请重新预检。";
            return false;
        }

        var sourceWidth = Mathf.RoundToInt(sourceSprite.rect.width);
        var sourceHeight = Mathf.RoundToInt(sourceSprite.rect.height);
        var settings = SpriteSettings.Capture(sourceSprite, AssetImporter.GetAtPath(assetPath));
        var labels = AssetDatabase.GetLabels(sourceSprite);
        byte[] pngBytes = null;
        if (!candidate.ReuseExistingPng)
        {
            try
            {
                pngBytes = ExtractPng(sourceSprite);
            }
            catch (Exception exception)
            {
                error = "提取 Sprite Rect 像素失败：" + exception.Message;
                return false;
            }
        }

        var absoluteAssetPath = AssetPathToAbsolute(assetPath);
        var absoluteOutputPath = AssetPathToAbsolute(outputPath);
        var backupAssetPath = Path.Combine(backupRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        var backupMetaPath = backupAssetPath + ".meta";
        var outputBackupPath = Path.Combine(backupRoot, "Outputs", outputPath.Replace('/', Path.DirectorySeparatorChar));
        var outputBackupMetaPath = outputBackupPath + ".meta";
        var hadOutput = File.Exists(absoluteOutputPath);
        var hadOutputMeta = File.Exists(absoluteOutputPath + ".meta");
        var referenceEdits = new List<ReferenceEdit>();

        try
        {
            if (candidate.ReuseExistingPng && (!hadOutput || !hadOutputMeta))
            {
                throw new InvalidOperationException("预检时准备复用的同名 PNG 或 meta 已不存在，请重新预检。");
            }

            string[] fr2Referencers;
            string fr2Error;
            if (!TryGetFindReference2DirectReferencers(oldGuid, out fr2Referencers, out fr2Error))
            {
                throw new InvalidOperationException("转换前无法重新查询 FindReference2：" + fr2Error);
            }
            var currentReferencers = NormalizeFindReferencePaths(assetPath, fr2Referencers);
            var nonTextReferencers = currentReferencers
                .Where(path => !IsUnitySerializedTextAsset(path))
                .Take(4)
                .ToArray();
            if (nonTextReferencers.Length > 0)
            {
                throw new InvalidOperationException(
                    "FindReference2 检出无法安全文本迁移的直接引用：" + string.Join("、", nonTextReferencers));
            }
            if (!candidate.Fr2DirectReferencePaths.SetEquals(currentReferencers))
            {
                throw new InvalidOperationException("FindReference2 引用文件在预检后发生变化，请重新预检。");
            }

            if (!string.IsNullOrEmpty(candidate.ExistingPngGuid))
            {
                string[] targetReferencers;
                string targetError;
                if (!TryGetFindReference2DirectReferencers(
                        candidate.ExistingPngGuid, out targetReferencers, out targetError))
                {
                    throw new InvalidOperationException("转换前无法重新查询同名 PNG 引用：" + targetError);
                }
                var currentTargetReferencers = NormalizeFindReferencePaths(assetPath, targetReferencers);
                if (!candidate.ExistingPngDirectReferencePaths.SetEquals(currentTargetReferencers))
                {
                    throw new InvalidOperationException("同名 PNG 的引用在预检后发生变化，请重新预检。");
                }
                if (candidate.OverwriteExistingPng && currentTargetReferencers.Length > 0)
                {
                    throw new InvalidOperationException("同名 PNG 已被其他资源直接引用，不能安全覆盖。");
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absoluteOutputPath) ?? ProjectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(backupAssetPath) ?? backupRoot);
            File.Copy(absoluteAssetPath, backupAssetPath, true);
            File.Copy(absoluteAssetPath + ".meta", backupMetaPath, true);
            if (hadOutput || hadOutputMeta)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputBackupPath) ?? backupRoot);
                if (hadOutput) File.Copy(absoluteOutputPath, outputBackupPath, true);
                if (hadOutputMeta) File.Copy(absoluteOutputPath + ".meta", outputBackupMetaPath, true);
            }

            if (!candidate.ReuseExistingPng)
            {
                File.WriteAllBytes(absoluteOutputPath, pngBytes);
            }
            AssetDatabase.ImportAsset(outputPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(outputPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("Unity 没有为最终 PNG 创建 TextureImporter。");
            }
            settings.Apply(importer);
            importer.SaveAndReimport();

            var converted = AssetDatabase.LoadAssetAtPath<Sprite>(outputPath);
            string targetGuid;
            long targetLocalId;
            if (converted == null ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(converted, out targetGuid, out targetLocalId) ||
                string.IsNullOrEmpty(targetGuid) || targetLocalId != SpriteLocalId)
            {
                throw new InvalidOperationException("最终 PNG 无法作为单 Sprite 加载或其 fileID 不是 21300000。");
            }
            if (!string.IsNullOrEmpty(candidate.ExistingPngGuid) &&
                !string.Equals(targetGuid, candidate.ExistingPngGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("最终 PNG 的 GUID 发生变化；为避免破坏它原有引用，已回滚。");
            }

            string displayError;
            if (!TryValidateSpriteDisplay(settings, sourceWidth, sourceHeight, converted, importer, out displayError))
            {
                throw new InvalidOperationException("最终 PNG 显示参数校验失败：" + displayError);
            }
            if (!candidate.ReuseExistingPng)
            {
                string pixelError;
                if (!TryCompareSpritePixels(sourceSprite, outputPath, out pixelError))
                {
                    throw new InvalidOperationException("最终 PNG 像素校验失败：" + pixelError);
                }
            }

            referenceEdits = PrepareReferenceEdits(
                oldGuid, oldLocalId, targetGuid, targetLocalId, assetPath, backupRoot,
                candidate.SerializedReferencePaths);
            var preparedReferenceCount = referenceEdits.Sum(edit => edit.ReplacementCount);
            if (preparedReferenceCount != candidate.SerializedReferenceCount)
            {
                throw new InvalidOperationException(string.Format(
                    "预检后引用发生变化：预期迁移 {0} 处，当前找到 {1} 处；原 .asset 未删除，请重新预检。",
                    candidate.SerializedReferenceCount, preparedReferenceCount));
            }
            foreach (var edit in referenceEdits)
            {
                File.WriteAllText(edit.AbsolutePath, edit.UpdatedContent, new UTF8Encoding(false));
            }

            var remainingReferences = CountGuidReferencesInPaths(oldGuid, candidate.AllGuidReferencePaths);
            if (remainingReferences > 0)
            {
                throw new InvalidOperationException(
                    "迁移后仍发现 " + remainingReferences + " 处旧 GUID 序列化引用，原 .asset 未删除。");
            }

            AssetDatabase.DisallowAutoRefresh();
            try
            {
                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    throw new InvalidOperationException("无法删除已无序列化引用的原 Sprite .asset。");
                }
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            converted = AssetDatabase.LoadAssetAtPath<Sprite>(outputPath);
            string finalGuid;
            long finalLocalId;
            if (converted == null ||
                !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(converted, out finalGuid, out finalLocalId) ||
                !string.Equals(finalGuid, targetGuid, StringComparison.OrdinalIgnoreCase) ||
                finalLocalId != targetLocalId)
            {
                throw new InvalidOperationException("删除原 .asset 后最终 PNG 身份校验失败，已回滚。");
            }

            AssetDatabase.SetLabels(converted, labels);
            EditorUtility.SetDirty(converted);
            return true;
        }
        catch (Exception exception)
        {
            RollBack(assetPath, outputPath, backupAssetPath, backupMetaPath,
                outputBackupPath, outputBackupMetaPath, hadOutput, hadOutputMeta, referenceEdits);
            error = exception.Message;
            return false;
        }
    }

    private static bool TryValidateSpriteDisplay(
        SpriteSettings expected,
        int expectedWidth,
        int expectedHeight,
        Sprite actual,
        TextureImporter importer,
        out string error)
    {
        error = string.Empty;
        if (actual == null || actual.texture == null || importer == null)
        {
            error = "Sprite 或 TextureImporter 无法读取";
            return false;
        }
        if (Mathf.RoundToInt(actual.rect.width) != expectedWidth ||
            Mathf.RoundToInt(actual.rect.height) != expectedHeight)
        {
            error = string.Format("尺寸不一致（期望 {0}x{1}，实际 {2}x{3}）", expectedWidth, expectedHeight,
                Mathf.RoundToInt(actual.rect.width), Mathf.RoundToInt(actual.rect.height));
            return false;
        }
        if (!Mathf.Approximately(actual.pixelsPerUnit, expected.PixelsPerUnit))
        {
            error = "PPU 不一致";
            return false;
        }
        if (!Approximately(actual.border, expected.Border))
        {
            error = "Border 不一致";
            return false;
        }
        var expectedPivot = new Vector2(NormalizePivotAxis(expected.Pivot.x), NormalizePivotAxis(expected.Pivot.y));
        if (!Approximately(GetNormalizedPivot(actual), expectedPivot))
        {
            error = "Pivot 不一致";
            return false;
        }
        if (actual.texture.filterMode != expected.FilterMode || actual.texture.wrapMode != expected.WrapMode ||
            actual.texture.anisoLevel != expected.AnisoLevel)
        {
            error = "Filter/Wrap/Aniso 参数不一致";
            return false;
        }
        var textureSettings = new TextureImporterSettings();
        importer.ReadTextureSettings(textureSettings);
        if (importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Single ||
            textureSettings.spriteMeshType != SpriteMeshType.FullRect)
        {
            error = "导入类型不是 Single Sprite / Full Rect";
            return false;
        }
        if (!string.Equals(importer.assetBundleName, expected.AssetBundleName, StringComparison.Ordinal) ||
            !string.Equals(importer.assetBundleVariant, expected.AssetBundleVariant, StringComparison.Ordinal))
        {
            error = "AssetBundle 名称或变体不一致";
            return false;
        }
        return true;
    }

    private static bool TryStartFindReference2Refresh(out string error)
    {
        if (!TryEnsureFindReference2Ready(out error)) return false;

        Type cacheType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            cacheType = assembly.GetType("vietlabs.fr2.FR2_Cache", false);
            if (cacheType != null) break;
        }
        if (cacheType == null)
        {
            error = "未找到 FindReference2 缓存类型。";
            return false;
        }

        try
        {
            var apiProperty = cacheType.GetProperty(
                "Api", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var api = apiProperty == null ? null : apiProperty.GetValue(null, null);
            var checkChanges = api == null
                ? null
                : api.GetType().GetMethod("Check4Changes",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(bool) }, null);
            if (api == null || checkChanges == null)
            {
                error = "FindReference2 缺少增量缓存刷新接口。";
                return false;
            }

            checkChanges.Invoke(api, new object[] { false });
            error = string.Empty;
            return true;
        }
        catch (TargetInvocationException exception)
        {
            error = "刷新 FindReference2 缓存失败：" +
                    (exception.InnerException == null ? exception.Message : exception.InnerException.Message);
            return false;
        }
        catch (Exception exception)
        {
            error = "刷新 FindReference2 缓存失败：" + exception.Message;
            return false;
        }
    }

    private static bool TryEnsureFindReference2Ready(out string error)
    {
        error = string.Empty;
        Type cacheType = null;
        Type refType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (cacheType == null) cacheType = assembly.GetType("vietlabs.fr2.FR2_Cache", false);
            if (refType == null) refType = assembly.GetType("vietlabs.fr2.FR2_Ref", false);
        }
        if (cacheType == null || refType == null)
        {
            error = "未找到 FindReference2 程序集。";
            return false;
        }

        try
        {
            var readyProperty = cacheType.GetProperty("isReady", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (readyProperty == null || !(bool)readyProperty.GetValue(null, null))
            {
                error = "FindReference2 缓存尚未就绪，请先打开 Find Reference 2 并完成缓存扫描。";
                return false;
            }

            var apiProperty = cacheType.GetProperty("Api", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var api = apiProperty == null ? null : apiProperty.GetValue(null, null);
            if (api == null)
            {
                error = "FindReference2 缓存对象不可用，请先刷新缓存。";
                return false;
            }
            var disabledProperty = api.GetType().GetProperty("disabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (disabledProperty != null && (bool)disabledProperty.GetValue(api, null))
            {
                error = "FindReference2 当前已禁用，请启用并刷新缓存。";
                return false;
            }
            var findUsedBy = refType.GetMethod("FindUsedBy", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string[]) }, null);
            if (findUsedBy == null)
            {
                error = "FindReference2 缺少引用查询接口，请更新或刷新缓存。";
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            error = "读取 FindReference2 缓存状态失败：" + exception.Message;
            return false;
        }
    }

    private static bool TryGetFindReference2DirectReferencers(string guid, out string[] paths, out string error)
    {
        paths = new string[0];
        if (string.IsNullOrEmpty(guid))
        {
            error = "资源 GUID 为空。";
            return false;
        }
        if (!TryEnsureFindReference2Ready(out error)) return false;

        Type refType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            refType = assembly.GetType("vietlabs.fr2.FR2_Ref", false);
            if (refType != null) break;
        }
        if (refType == null)
        {
            error = "未找到 FindReference2 引用查询类型。";
            return false;
        }

        try
        {
            var method = refType.GetMethod("FindUsedBy", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string[]) }, null);
            var result = method == null ? null : method.Invoke(null, new object[] { new[] { guid } }) as IDictionary;
            if (result == null)
            {
                error = "FindReference2 没有返回可用的引用结果。";
                return false;
            }

            var directPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var malformedEntryCount = 0;
            foreach (DictionaryEntry entry in result)
            {
                var value = entry.Value;
                if (value == null)
                {
                    malformedEntryCount++;
                    continue;
                }
                var valueType = value.GetType();
                var depthField = valueType.GetField("depth", BindingFlags.Public | BindingFlags.Instance);
                if (depthField == null)
                {
                    malformedEntryCount++;
                    continue;
                }
                if ((int)depthField.GetValue(value) != 1) continue;
                var assetField = valueType.GetField("asset", BindingFlags.Public | BindingFlags.Instance);
                var asset = assetField == null ? null : assetField.GetValue(value);
                if (asset == null)
                {
                    malformedEntryCount++;
                    continue;
                }
                var pathProperty = asset.GetType().GetProperty("assetPath", BindingFlags.Public | BindingFlags.Instance);
                var path = pathProperty == null ? null : pathProperty.GetValue(asset, null) as string;
                if (string.IsNullOrEmpty(path))
                {
                    malformedEntryCount++;
                    continue;
                }
                directPaths.Add(path.Replace('\\', '/'));
            }
            if (malformedEntryCount > 0)
            {
                error = "FindReference2 返回了 " + malformedEntryCount + " 个无法解析的引用条目。";
                return false;
            }
            paths = directPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            error = string.Empty;
            return true;
        }
        catch (TargetInvocationException exception)
        {
            error = "FindReference2 引用查询失败：" +
                    (exception.InnerException == null ? exception.Message : exception.InnerException.Message);
            return false;
        }
        catch (Exception exception)
        {
            error = "FindReference2 引用查询失败：" + exception.Message;
            return false;
        }
    }

    private static int CountGuidReferencesInPaths(string guid, IEnumerable<string> assetPaths)
    {
        if (string.IsNullOrEmpty(guid)) return 0;
        var count = 0;
        foreach (var assetPath in assetPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var absolutePath = AssetPathToAbsolute(assetPath);
            if (!IsUnitySerializedTextFile(absolutePath)) continue;
            string content;
            try { content = File.ReadAllText(absolutePath); } catch (Exception) { continue; }
            count += SerializedGuidRegex.Matches(content).Cast<Match>()
                .Count(match => string.Equals(match.Groups[1].Value, guid, StringComparison.OrdinalIgnoreCase));
        }
        return count;
    }

    private static byte[] ExtractPng(Sprite sprite)
    {
        var source = sprite.texture;
        var rect = sprite.packed ? sprite.textureRect : sprite.rect;
        var x = Mathf.RoundToInt(rect.x);
        var y = Mathf.RoundToInt(rect.y);
        var width = Mathf.RoundToInt(rect.width);
        var height = Mathf.RoundToInt(rect.height);
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > source.width || y + height > source.height)
        {
            throw new InvalidOperationException("Sprite 裁切区域超出源纹理范围。");
        }

        var temporary = RenderTexture.GetTemporary(
            source.width,
            source.height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        Texture2D cropped = null;
        try
        {
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            cropped = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            cropped.ReadPixels(new Rect(x, y, width, height), 0, 0, false);
            cropped.Apply(false, false);
            return cropped.EncodeToPNG();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
            if (cropped != null)
            {
                DestroyImmediate(cropped);
            }
        }
    }

    private static void RollBack(
        string assetPath,
        string outputPath,
        string backupAssetPath,
        string backupMetaPath,
        string outputBackupPath,
        string outputBackupMetaPath,
        bool hadOutput,
        bool hadOutputMeta,
        IEnumerable<ReferenceEdit> referenceEdits)
    {
        AssetDatabase.DisallowAutoRefresh();
        try
        {
            var absoluteOutput = AssetPathToAbsolute(outputPath);
            DeleteFileIfExists(absoluteOutput);
            DeleteFileIfExists(absoluteOutput + ".meta");

            var absoluteAsset = AssetPathToAbsolute(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteAsset) ?? ProjectRoot);
            if (File.Exists(backupAssetPath)) File.Copy(backupAssetPath, absoluteAsset, true);
            if (File.Exists(backupMetaPath)) File.Copy(backupMetaPath, absoluteAsset + ".meta", true);

            if (hadOutput && File.Exists(outputBackupPath)) File.Copy(outputBackupPath, absoluteOutput, true);
            if (hadOutputMeta && File.Exists(outputBackupMetaPath)) File.Copy(outputBackupMetaPath, absoluteOutput + ".meta", true);

            foreach (var edit in referenceEdits)
            {
                if (File.Exists(edit.BackupPath)) File.Copy(edit.BackupPath, edit.AbsolutePath, true);
            }
        }
        finally
        {
            AssetDatabase.AllowAutoRefresh();
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
    }

    private static List<ReferenceEdit> PrepareReferenceEdits(
        string guid,
        long localId,
        string targetGuid,
        long targetLocalId,
        string sourceAssetPath,
        string backupRoot,
        IEnumerable<string> referenceAssetPaths)
    {
        var result = new List<ReferenceEdit>();
        var pattern = new Regex(
            "(\\{fileID:\\s*" + localId + ",\\s*guid:\\s*" + Regex.Escape(guid) + ",\\s*type:\\s*)2(\\s*\\})",
            RegexOptions.CultureInvariant);
        var replacement = "{fileID: " + targetLocalId + ", guid: " + targetGuid + ", type: 3}";

        foreach (var assetPath in referenceAssetPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(assetPath, sourceAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (assetPath.EndsWith("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var absolutePath = AssetPathToAbsolute(assetPath);
            if (!IsUnitySerializedTextFile(absolutePath)) continue;

            string content;
            try
            {
                content = File.ReadAllText(absolutePath);
            }
            catch (Exception)
            {
                continue;
            }

            if (!pattern.IsMatch(content))
            {
                continue;
            }

            var replacementCount = pattern.Matches(content).Count;

            var backupPath = Path.Combine(
                backupRoot,
                "ReferenceEdits",
                guid,
                assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? backupRoot);
            File.Copy(absolutePath, backupPath, true);
            result.Add(new ReferenceEdit
            {
                AbsolutePath = absolutePath,
                BackupPath = backupPath,
                UpdatedContent = pattern.Replace(content, replacement),
                ReplacementCount = replacementCount
            });
        }

        return result;
    }

    private static string GetOutputPath(string assetPath)
    {
        var desired = Path.ChangeExtension(assetPath, ".png").Replace('\\', '/');
        return desired;
    }

    private static string ReadMetaGuid(string metaPath)
    {
        var content = File.ReadAllText(metaPath);
        var match = GuidLineRegex.Match(content);
        return match.Success ? match.Value.Substring(match.Value.IndexOf(':') + 1).Trim() : string.Empty;
    }

    private static bool IsUnitySerializedTextAsset(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return IsUnitySerializedTextFile(AssetPathToAbsolute(assetPath));
    }

    private static bool IsUnitySerializedTextFile(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath) ||
            absolutePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(absolutePath)) return false;
        try
        {
            var header = new byte[5];
            using (var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Read(header, 0, header.Length) != header.Length) return false;
            }
            return header[0] == (byte)'%' && header[1] == (byte)'Y' && header[2] == (byte)'A' &&
                   header[3] == (byte)'M' && header[4] == (byte)'L';
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private void SetFolderPath(string path)
    {
        folderPath = string.IsNullOrWhiteSpace(path) ? DefaultFolder : path.Replace('\\', '/').TrimEnd('/');
        folderObject = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folderPath);
    }

    private void AddScanProblemItem(string assetPath, string message)
    {
        scanProblems.Add(assetPath + "：" + message);
        problemItems.Add(new ScanProblemItem
        {
            AssetPath = assetPath,
            Message = message
        });
    }

    private void ClearScan()
    {
        ClearPreviewCache();
        candidates.Clear();
        problemItems.Clear();
        scanProblems.Clear();
        specialAssetReports.Clear();
        scannedAssetCount = 0;
        layoutApproximationCount = 0;
        scanPaths = new string[0];
        scanIndex = 0;
        referenceScanPaths = new string[0];
        referenceScanIndex = 0;
        referenceFinalizeIndex = 0;
        referencePrepareIndex = 0;
        referenceScanPrepared = false;
        findReferenceReadyChecked = false;
        findReferenceRefreshStarted = false;
        targetedReferencePaths.Clear();
        scanCandidatesByGuid.Clear();
        hasScanned = false;
    }

    private static bool TryNormalizeFolder(string value, out string assetPath, out string error)
    {
        assetPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请输入 Assets 下的目录路径。";
            return false;
        }

        var candidate = value.Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
        if (Path.IsPathRooted(candidate))
        {
            var absolute = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dataPath = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!absolute.Equals(dataPath, StringComparison.OrdinalIgnoreCase) &&
                !absolute.StartsWith(dataPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                error = "目录必须位于当前项目的 Assets 内。";
                return false;
            }

            candidate = "Assets" + absolute.Substring(dataPath.Length).Replace('\\', '/');
        }

        if (!candidate.Equals("Assets", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            error = "路径必须是 Assets 或 Assets/...。";
            return false;
        }

        candidate = "Assets" + candidate.Substring(6);
        if (!AssetDatabase.IsValidFolder(candidate))
        {
            error = "目录不存在或不是 Unity 项目目录：" + candidate;
            return false;
        }

        assetPath = candidate;
        return true;
    }

    private static string AbsoluteToAssetPath(string absolutePath)
    {
        var relative = Path.GetFullPath(absolutePath).Substring(ProjectRoot.Length)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Replace('\\', '/');
    }

    private static string AssetPathToAbsolute(string assetPath)
    {
        return Path.GetFullPath(Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static Vector2 GetNormalizedPivot(Sprite sprite)
    {
        return new Vector2(
            sprite.rect.width <= 0f ? 0.5f : sprite.pivot.x / sprite.rect.width,
            sprite.rect.height <= 0f ? 0.5f : sprite.pivot.y / sprite.rect.height);
    }

    private static bool RequiresPivotNormalization(Vector2 pivot)
    {
        return float.IsNaN(pivot.x) || float.IsInfinity(pivot.x) ||
               float.IsNaN(pivot.y) || float.IsInfinity(pivot.y) ||
               pivot.x < 0f || pivot.x > 1f || pivot.y < 0f || pivot.y > 1f;
    }

    private static float NormalizePivotAxis(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? 0.5f : Mathf.Clamp01(value);
    }

    private static bool HasSerializedOffset(string assetPath)
    {
        try
        {
            var content = File.ReadAllText(AssetPathToAbsolute(assetPath));
            var match = Regex.Match(content, @"m_Offset:\s*\{x:\s*([-+0-9.eE]+),\s*y:\s*([-+0-9.eE]+)\}");
            if (!match.Success) return false;
            float x;
            float y;
            return float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out x) &&
                   float.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out y) &&
                   (Math.Abs(x) > 0.0001f || Math.Abs(y) > 0.0001f);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ProjectRoot
    {
        get { return Directory.GetParent(Application.dataPath).FullName; }
    }

    private sealed class ConversionCandidate
    {
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> SettingDifferences = new List<string>();
        public string AssetPath;
        public string Guid;
        public long LocalId;
        public string SourceTexturePath;
        public string OutputPath;
        public Vector2 NormalizedPivot;
        public bool ApproximatePivot;
        public bool HasOffset;
        public bool Packed;
        public bool ReuseExistingPng;
        public bool OverwriteExistingPng;
        public string ExistingPngGuid;
        public string ActionLabel;
        public int SerializedReferenceCount;
        public int AllGuidReferenceCount;
        public int MigratableReferenceCount;
        public int Fr2DirectReferencerCount;
        public readonly HashSet<string> AllGuidReferencePaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> SerializedReferencePaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Fr2DirectReferencePaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ExistingPngDirectReferencePaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Blocked;
    }

    private sealed class ScanProblemItem
    {
        public string AssetPath;
        public string Message;
    }

    private sealed class ConversionRun
    {
        public string BackupRoot;
        public ConversionCandidate[] Pending;
        public List<string> Successes;
        public List<string> Failures;
        public int Index;
        public bool FindReferenceRefreshStarted;
        public int ReusedPng;
    }

    private sealed class SpriteSettings
    {
        public float PixelsPerUnit;
        public Vector4 Border;
        public Vector2 Pivot;
        public FilterMode FilterMode;
        public TextureWrapMode WrapMode;
        public int AnisoLevel;
        public string UserData;
        public string AssetBundleName;
        public string AssetBundleVariant;

        public static SpriteSettings Capture(Sprite sprite, AssetImporter originalImporter)
        {
            return new SpriteSettings
            {
                PixelsPerUnit = sprite.pixelsPerUnit,
                Border = sprite.border,
                Pivot = GetNormalizedPivot(sprite),
                FilterMode = sprite.texture.filterMode,
                WrapMode = sprite.texture.wrapMode,
                AnisoLevel = sprite.texture.anisoLevel,
                UserData = originalImporter == null ? string.Empty : originalImporter.userData,
                AssetBundleName = originalImporter == null ? string.Empty : originalImporter.assetBundleName,
                AssetBundleVariant = originalImporter == null ? string.Empty : originalImporter.assetBundleVariant
            };
        }

        public void Apply(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = PixelsPerUnit;
            importer.spriteBorder = Border;
            var textureSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(textureSettings);
            textureSettings.spriteAlignment = (int)SpriteAlignment.Custom;
            textureSettings.spriteMeshType = SpriteMeshType.FullRect;
            textureSettings.spritePivot = new Vector2(
                NormalizePivotAxis(Pivot.x),
                NormalizePivotAxis(Pivot.y));
            importer.SetTextureSettings(textureSettings);
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.filterMode = FilterMode;
            importer.wrapMode = WrapMode;
            importer.anisoLevel = AnisoLevel;
            importer.userData = UserData;
            importer.assetBundleName = AssetBundleName;
            importer.assetBundleVariant = AssetBundleVariant;
        }
    }

    private sealed class ReferenceEdit
    {
        public string AbsolutePath;
        public string BackupPath;
        public string UpdatedContent;
        public int ReplacementCount;
    }
}
