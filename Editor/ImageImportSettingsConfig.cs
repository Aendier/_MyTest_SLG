using System;
using System.Collections.Generic;
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
        [Tooltip("使用 Unity 原生 TextureImporter Preset；应用时会排除图片独立维护的 Sprite 数据")]
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

        [InspectorName("启用自动设置")]
        [Tooltip("关闭后新图片仍会正常导入，但不会自动应用规则")]
        public bool Enabled = true;

        [InspectorName("规则列表")]
        [Tooltip("从上到下匹配，第一条符合条件的规则生效")]
        public List<ImageImportSettingsRule> Rules = new List<ImageImportSettingsRule>();

        public ImageImportSettingsRule FindRule(string assetPath)
        {
            var rule = FindMatchingRule(assetPath);
            return rule != null && rule.Preset != null ? rule : null;
        }

        public ImageImportSettingsRule FindMatchingRule(string assetPath)
        {
            if (Rules == null)
                return null;

            foreach (var rule in Rules)
            {
                if (rule != null && rule.Matches(assetPath))
                    return rule;
            }

            return null;
        }

        public static ImageImportSettingsConfig LoadOrCreate()
        {
            var config = AssetDatabase.LoadAssetAtPath<ImageImportSettingsConfig>(AssetPath);
            if (config != null)
                return config;

            config = CreateInstance<ImageImportSettingsConfig>();
            AssetDatabase.CreateAsset(config, AssetPath);
            AssetDatabase.SaveAssets();
            return config;
        }

        /// <summary>应用原生预设，但排除每张图片独立维护的 Sprite 数据。</summary>
        public static void ApplyPreset(TextureImporter importer, Preset preset)
        {
            if (importer == null || preset == null || !preset.CanBeAppliedTo(importer))
                return;

            var selectedProperties = new List<string>();
            var modifications = preset.PropertyModifications;
            if (modifications == null)
                return;

            foreach (var modification in modifications)
            {
                if (modification == null || string.IsNullOrEmpty(modification.propertyPath) ||
                    IsExcludedSpriteProperty(modification.propertyPath))
                    continue;

                selectedProperties.Add(modification.propertyPath);
            }

            if (selectedProperties.Count > 0)
                preset.ApplyTo(importer, selectedProperties.ToArray());
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
            if (config == null || !config.Enabled)
                return;

            var rule = config.FindRule(assetPath);
            if (rule == null || rule.Preset == null)
                return;

            // 统一写入纹理类型和 Advanced 设置，并保留图片自身的 Sprite Border。
            ImageImportSettingsConfig.ApplyPreset((TextureImporter)assetImporter, rule.Preset);
        }

        private static ImageImportSettingsConfig GetConfig()
        {
            if (_config == null)
                _config = AssetDatabase.LoadAssetAtPath<ImageImportSettingsConfig>(ImageImportSettingsConfig.AssetPath);
            return _config;
        }

    }
}
