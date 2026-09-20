using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEditor.Presets;
using UnityEngine;

namespace UIR.EditorTools
{
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
        [Tooltip("使用 Unity 原生 TextureImporter Preset；已有图片会保留独立维护的 Sprite 数据，新 Sprite 沿用 Unity 默认的 Single 和 Clamp")]
        public Preset Preset;

        [InspectorName("包含子文件夹")]
        [Tooltip("勾选后包含所有子文件夹；取消后仅匹配当前文件夹")]
        public bool IncludeSubfolders = true;

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
            return rule != null && rule.Preset != null ? rule : null;
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

            if (rule.Preset == null)
            {
                ImageImportSettingsConfig.LogVerbose($"跳过导入：规则“{rule.RuleName}”未设置预设，assetPath={assetPath}。");
                return;
            }

            // 统一写入预设中的通用设置；已有图片保留独立设置，新 Sprite 使用 Unity 默认设置。
            bool changed = ImageImportSettingsConfig.ApplyPreset((TextureImporter)assetImporter, rule.Preset);
            ImageImportSettingsConfig.LogVerbose($"新图片导入处理：assetPath={assetPath}，规则={rule.RuleName}，预设={rule.Preset.name}，changed={changed}。");
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
