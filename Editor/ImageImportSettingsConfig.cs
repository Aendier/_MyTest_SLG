using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEditor.Presets;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace UIR.EditorTools
{
    public enum ImageCompressionPlatform
    {
        Android,
        iPhone,
        Both
    }

    [Serializable]
    public sealed class ImageImportSettingsRule
    {
        [InspectorName("规则名称")]
        [Tooltip("便于识别这条规则的名称")]
        public string RuleName = "新规则";

        [InspectorName("文件夹列表")]
        [Tooltip("从 Project 窗口拖入文件夹；同一规则可配置多个文件夹")]
        public List<DefaultAsset> Folders = new List<DefaultAsset>();

        [InspectorName("导入预设")]
        [Tooltip("可选。只修改压缩时可以留空；已有图片保留独立维护的 Sprite 数据。平台压缩由下方独立设置控制")]
        public Preset Preset;

        [InspectorName("包含子文件夹")]
        [Tooltip("勾选后包含所有子文件夹；取消后仅匹配当前文件夹")]
        public bool IncludeSubfolders = true;

        [FoldoutGroup("压缩设置")]
        [LabelText("启用压缩设置")]
        [Tooltip("默认关闭，保留已有规则行为。启用后，同一规则的所有文件夹共用下面的压缩设置")]
        public bool ApplyCompression;

        [FoldoutGroup("压缩设置"), ShowIf(nameof(ApplyCompression))]
        [LabelText("目标平台")]
        public ImageCompressionPlatform CompressionPlatform = ImageCompressionPlatform.Android;

        [FoldoutGroup("压缩设置"), ShowIf(nameof(ApplyCompression))]
        [LabelText("压缩格式")]
        public TextureImporterFormat CompressionFormat = TextureImporterFormat.ASTC_8x8;

        [FoldoutGroup("压缩设置"), ShowIf(nameof(ApplyCompression))]
        [LabelText("同时修改 Max Size")]
        public bool OverrideMaxSize;

        [FoldoutGroup("压缩设置"), ShowIf(nameof(ApplyCompression)), EnableIf(nameof(OverrideMaxSize))]
        [LabelText("Max Size"), ValueDropdown(nameof(MaxSizeOptions))]
        public int MaxTextureSize = 2048;

        [FoldoutGroup("压缩设置"), ShowIf(nameof(ApplyCompression))]
        [LabelText("同时修改编码质量")]
        public bool OverrideCompressionQuality;

        [FoldoutGroup("压缩设置"), ShowIf(nameof(ApplyCompression)), EnableIf(nameof(OverrideCompressionQuality))]
        [LabelText("编码质量"), Range(0, 100)]
        public int CompressionQuality = 50;

        [FoldoutGroup("压缩设置"), ShowIf(nameof(ApplyCompression))]
        [LabelText("包含 Sprite Atlas（批量应用）")]
        [Tooltip("批量应用时同时设置匹配图集的压缩格式；图集不应用 TextureImporter 预设")]
        public bool IncludeSpriteAtlases;

        private static readonly int[] MaxSizeOptions = { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384 };

        public bool HasSettings => Preset != null || ApplyCompression;

        public bool Matches(string assetPath)
        {
            if (Folders == null || string.IsNullOrEmpty(assetPath))
                return false;

            foreach (DefaultAsset folderAsset in Folders)
            {
                if (folderAsset == null)
                    continue;

                string folder = AssetDatabase.GetAssetPath(folderAsset);
                if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                    continue;

                folder = folder.Replace('\\', '/').TrimEnd('/');

                if (IncludeSubfolders && assetPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                    return true;

                int slash = assetPath.LastIndexOf('/');
                if (!IncludeSubfolders && slash >= 0 && assetPath.Substring(0, slash).Equals(folder, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    [CreateAssetMenu(fileName = "ImageImportSettingsConfig", menuName = "UIR/Image Import Settings Config")]
    public sealed class ImageImportSettingsConfig : ScriptableObject
    {
        public const string AssetPath = "Assets/_MyTest_SLG/Editor/ImageImportSettingsConfig.asset";

        private static readonly string[] ExcludedSpriteProperties =
        {
            "m_SpriteBorder",
            "m_SpriteBorder.x",
            "m_SpriteBorder.y",
            "m_SpriteBorder.z",
            "m_SpriteBorder.w",
            "m_SpritePivot",
            "m_SpritePivot.x",
            "m_SpritePivot.y",
            "m_SpritePixelsToUnits",
            "m_SpriteMode",
            "m_SpriteSheet",
            "m_SpriteMeshType",
            "m_Alignment",
            "m_SpriteGenerateFallbackPhysicsShape",
            "m_SpriteTessellationDetail",
            "m_PlatformSettings",
            "m_TextureSettings.m_WrapU",
            "m_TextureSettings.m_WrapV",
            "m_TextureSettings.m_WrapW"
        };

        private const string EnabledPrefsKey = "UIR.ImageImportSettings.Enabled";
        private const string VerboseLoggingPrefsKey = "UIR.ImageImportSettings.VerboseLogging";

        [ShowInInspector]
        [LabelText("启用自动设置（仅本机）")]
        [Tooltip("保存到当前 Unity 用户的 EditorPrefs；关闭后新图片仍会正常导入，但不会自动应用规则")]
        public bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPrefsKey, true);
            set => EditorPrefs.SetBool(EnabledPrefsKey, value);
        }

        [InspectorName("规则列表")]
        [Tooltip("从上到下匹配，第一条符合条件的规则生效")]
        public List<ImageImportSettingsRule> Rules = new List<ImageImportSettingsRule>();

        public static bool VerboseLogging
        {
            get => EditorPrefs.GetBool(VerboseLoggingPrefsKey, false);
            set => EditorPrefs.SetBool(VerboseLoggingPrefsKey, value);
        }

        public static void LogVerbose(string message)
        {
            if (VerboseLogging)
                Debug.Log($"[ImageImportSettings][Verbose] {message}");
        }

        public ImageImportSettingsRule FindRule(string assetPath)
        {
            var rule = FindMatchingRule(assetPath);
            return rule != null && rule.HasSettings ? rule : null;
        }

        public ImageImportSettingsRule FindMatchingRule(string assetPath)
        {
            if (Rules == null)
                return null;

            for (int index = 0; index < Rules.Count; index++)
            {
                var rule = Rules[index];
                if (rule == null)
                {
                    LogVerbose($"规则匹配检查：index={index}，规则为空，assetPath={assetPath}。");
                    continue;
                }

                bool matched = rule.Matches(assetPath);
                LogVerbose($"规则匹配检查：index={index}，规则={rule.RuleName}，matched={matched}，assetPath={assetPath}。");
                if (matched)
                    return rule;
            }

            return null;
        }

        public static ImageImportSettingsConfig LoadOrCreate()
        {
            var config = AssetDatabase.LoadAssetAtPath<ImageImportSettingsConfig>(AssetPath);
            if (config != null)
            {
                LogVerbose($"加载配置成功：{AssetPath}，本机 Enabled={config.Enabled}，规则数={config.Rules?.Count ?? 0}。");
                return config;
            }

            config = CreateInstance<ImageImportSettingsConfig>();
            AssetDatabase.CreateAsset(config, AssetPath);
            AssetDatabase.SaveAssets();
            LogVerbose($"配置不存在，已创建：{AssetPath}，本机 Enabled={config.Enabled}。");
            return config;
        }

        /// <summary>预设管理通用导入属性，压缩设置独立管理平台属性，避免复制预设中的整个平台数组。</summary>
        public static bool ApplyRule(TextureImporter importer, ImageImportSettingsRule rule)
        {
            if (importer == null || rule == null)
                return false;

            bool changed = rule.Preset != null && ApplyPreset(importer, rule.Preset);
            if (rule.ApplyCompression)
                changed |= ApplyCompressionSettings(rule, importer.GetPlatformTextureSettings,
                    importer.GetDefaultPlatformTextureSettings, importer.SetPlatformTextureSettings);
            return changed;
        }

        public static bool ApplyAtlasCompression(SpriteAtlas atlas, ImageImportSettingsRule rule)
        {
            if (atlas == null || rule == null || !rule.ApplyCompression || !rule.IncludeSpriteAtlases)
                return false;

            return ApplyCompressionSettings(rule, atlas.GetPlatformSettings,
                () => atlas.GetPlatformSettings("DefaultTexturePlatform"), atlas.SetPlatformSettings);
        }

        private static bool ApplyCompressionSettings(ImageImportSettingsRule rule,
            Func<string, TextureImporterPlatformSettings> getSettings,
            Func<TextureImporterPlatformSettings> getDefaultSettings,
            Action<TextureImporterPlatformSettings> setSettings)
        {
            string[] platforms = rule.CompressionPlatform == ImageCompressionPlatform.Both
                ? new[] { "Android", "iPhone" }
                : new[] { rule.CompressionPlatform.ToString() };
            bool changed = false;
            foreach (string platform in platforms)
            {
                TextureImporterPlatformSettings settings = getSettings(platform);
                if (settings.overridden && settings.format == rule.CompressionFormat
                    && (!rule.OverrideMaxSize || settings.maxTextureSize == rule.MaxTextureSize)
                    && (!rule.OverrideCompressionQuality || settings.compressionQuality == rule.CompressionQuality))
                    continue;

                // 首次开启平台覆盖时沿用当前生效的默认值，不重新启用平台中残留的旧尺寸和质量。
                if (!settings.overridden)
                {
                    settings = getDefaultSettings();
                    settings.name = platform;
                }
                settings.overridden = true;
                settings.format = rule.CompressionFormat;
                if (rule.OverrideMaxSize)
                    settings.maxTextureSize = rule.MaxTextureSize;
                if (rule.OverrideCompressionQuality)
                    settings.compressionQuality = rule.CompressionQuality;
                setSettings(settings);
                changed = true;
            }
            return changed;
        }

        /// <summary>只检索已配置的文件夹，并统一使用第一条命中规则，供预览和批量应用复用。</summary>
        public List<string> FindCandidateAssetPaths()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool includeAtlases = false;
            if (Rules != null)
            {
                foreach (ImageImportSettingsRule rule in Rules)
                {
                    if (rule == null)
                        continue;
                    includeAtlases |= rule.ApplyCompression && rule.IncludeSpriteAtlases;
                    if (rule.Folders == null)
                        continue;
                    foreach (DefaultAsset folderAsset in rule.Folders)
                    {
                        if (folderAsset == null)
                            continue;
                        string path = AssetDatabase.GetAssetPath(folderAsset);
                        if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                            folders.Add(path.Replace('\\', '/').TrimEnd('/'));
                    }
                }
            }
            if (folders.Count == 0)
                return new List<string>();

            string[] searchFolders = new List<string>(folders).ToArray();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", searchFolders))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is TextureImporter && FindMatchingRule(path) != null)
                    paths.Add(path);
            }
            if (includeAtlases)
            {
                foreach (string guid in AssetDatabase.FindAssets("t:SpriteAtlas", searchFolders))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    ImageImportSettingsRule rule = FindMatchingRule(path);
                    if (rule != null && rule.ApplyCompression && rule.IncludeSpriteAtlases)
                        paths.Add(path);
                }
            }
            var result = new List<string>(paths);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>应用原生预设；保留已有图片的独立设置，并为新的 Sprite 图片补齐 Unity 默认设置。</summary>
        public static bool ApplyPreset(TextureImporter importer, Preset preset)
        {
            if (importer == null)
            {
                LogVerbose("预设未应用：TextureImporter 为空。");
                return false;
            }

            if (preset == null)
            {
                LogVerbose("预设未应用：Preset 为空。");
                return false;
            }

            if (!preset.CanBeAppliedTo(importer))
            {
                LogVerbose($"预设未应用：Preset“{preset.name}”不适用于当前 TextureImporter。");
                return false;
            }

            string originalState = EditorJsonUtility.ToJson(importer, false);

            bool importSettingsMissing = importer.importSettingsMissing;
            SpriteImportMode originalSpriteMode = importer.spriteImportMode;
            var selectedProperties = new List<string>();
            var modifications = preset.PropertyModifications;
            if (modifications == null)
            {
                LogVerbose($"预设未应用：Preset“{preset.name}”没有属性修改项。");
                return false;
            }

            foreach (var modification in modifications)
            {
                if (modification == null || string.IsNullOrEmpty(modification.propertyPath))
                    continue;

                if (IsExcludedSpriteProperty(modification.propertyPath))
                    continue;

                selectedProperties.Add(modification.propertyPath);
            }

            if (selectedProperties.Count > 0)
                preset.ApplyTo(importer, selectedProperties.ToArray());

            // 预设字段经过筛选后不会触发 Texture Type 切换时的默认联动，首次导入的 Sprite 需显式补齐默认值。
            if (importSettingsMissing &&
                importer.textureType == TextureImporterType.Sprite)
            {
                if (originalSpriteMode == SpriteImportMode.None)
                    importer.spriteImportMode = SpriteImportMode.Single;

                // 二维图片只使用 U/V；保留 W 的默认值，避免产生无意义的 meta 差异。
                importer.wrapModeU = TextureWrapMode.Clamp;
                importer.wrapModeV = TextureWrapMode.Clamp;
            }

            return !string.Equals(originalState, EditorJsonUtility.ToJson(importer, false), StringComparison.Ordinal);
        }

        private static bool IsExcludedSpriteProperty(string propertyPath)
        {
            foreach (string excludedProperty in ExcludedSpriteProperties)
            {
                if (propertyPath.Equals(excludedProperty, StringComparison.Ordinal) ||
                    propertyPath.StartsWith(excludedProperty + ".", StringComparison.Ordinal) ||
                    propertyPath.StartsWith(excludedProperty + ".Array", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

    public sealed class ImageImportSettingsPostprocessor : AssetPostprocessor
    {
        private static ImageImportSettingsConfig _config;

        private void OnPreprocessTexture()
        {
            var config = GetConfig();
            if (config == null)
            {
                ImageImportSettingsConfig.LogVerbose($"跳过导入：配置不存在，assetPath={assetPath}。");
                return;
            }

            if (!config.Enabled)
            {
                ImageImportSettingsConfig.LogVerbose($"跳过导入：本机 Enabled=false，assetPath={assetPath}。");
                return;
            }

            var rule = config.FindMatchingRule(assetPath);
            if (rule == null)
            {
                ImageImportSettingsConfig.LogVerbose($"跳过导入：没有匹配规则，assetPath={assetPath}。");
                return;
            }

            if (!rule.HasSettings)
            {
                ImageImportSettingsConfig.LogVerbose($"跳过导入：规则“{rule.RuleName}”未设置预设或压缩设置，assetPath={assetPath}。");
                return;
            }

            // 统一写入预设中的通用设置；已有图片保留独立设置，新 Sprite 使用 Unity 默认设置。
            bool changed = ImageImportSettingsConfig.ApplyRule((TextureImporter)assetImporter, rule);
            ImageImportSettingsConfig.LogVerbose($"图片导入处理：assetPath={assetPath}，规则={rule.RuleName}，预设={rule.Preset?.name ?? "无"}，启用压缩={rule.ApplyCompression}，changed={changed}。");
        }

        private static ImageImportSettingsConfig GetConfig()
        {
            if (_config == null)
            {
                _config = AssetDatabase.LoadAssetAtPath<ImageImportSettingsConfig>(ImageImportSettingsConfig.AssetPath);
                ImageImportSettingsConfig.LogVerbose($"AssetPostprocessor 获取配置：found={_config != null}。");
            }
            return _config;
        }

    }
}
