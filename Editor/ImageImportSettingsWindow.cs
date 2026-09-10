using System;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools
{
    public sealed class ImageImportSettingsWindow : OdinEditorWindow
    {
        [InlineEditor(InlineEditorObjectFieldModes.Hidden)]
        [LabelText("图片导入设置配置")]
        [ShowInInspector, PropertyOrder(-1)]
        private ImageImportSettingsConfig Config => _editingConfig;

        [NonSerialized]
        private ImageImportSettingsConfig _editingConfig;
        private ImageImportSettingsConfig _savedConfig;
        private string _savedConfigSnapshot;
        private bool _hasUnsavedChanges;

        private bool HasUnsavedChanges => _hasUnsavedChanges;

        public static void Open()
        {
            var window = GetWindow<ImageImportSettingsWindow>("图片导入设置");
            window.minSize = new Vector2(560, 420);
            window.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            OnEndGUI -= RefreshUnsavedChanges;
            DestroyEditingCopy();
            _savedConfig = ImageImportSettingsConfig.LoadOrCreate();
            _editingConfig = CreateEditingCopy(_savedConfig);
            _savedConfigSnapshot = SerializeConfig(Config);
            SetUnsavedChanges(false);
            saveChangesMessage = "图片导入设置已修改。保存配置后，新图片导入和已有图片应用才会使用新设置。";
            OnEndGUI += RefreshUnsavedChanges;
        }

        protected override void OnDisable()
        {
            OnEndGUI -= RefreshUnsavedChanges;
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            DestroyEditingCopy();
        }

        [Button("保存配置", ButtonSizes.Large), PropertyOrder(0)]
        [InfoBox("配置已修改，请点击“保存配置”；保存后才会生效。", InfoMessageType.Warning, VisibleIf = nameof(HasUnsavedChanges))]
        [GUIColor(0.35f, 0.8f, 0.45f)]
        private void SaveConfig()
        {
            RefreshUnsavedChanges();
            if (!HasUnsavedChanges)
            {
                Debug.Log("[ImageImportSettings] 当前没有未保存的配置修改。 ");
                return;
            }

            SaveConfigInternal();
        }

        [Button("应用到已有图片", ButtonSizes.Large), PropertyOrder(1)]
        [InfoBox("应用预设时会忽略以下内容：已有图片的 Sprite Mode、Sprite Border、Sprite Pivot、Pixels Per Unit、Sprite Sheet、Sprite Mesh Type、Alignment、Physics Shape、Tessellation Detail、Wrap Mode、平台设置。新图片被设置为 Sprite 时，Sprite Mode 默认为 Single，Wrap Mode 默认为 Clamp。", InfoMessageType.Info)]
        [GUIColor(0.35f, 0.8f, 0.45f)]
        private void ApplyToExistingImages()
        {
            RefreshUnsavedChanges();
            if (Config == null || _savedConfig == null)
                return;

            if (HasUnsavedChanges)
            {
                EditorUtility.DisplayDialog(
                    "配置尚未保存",
                    "检测到配置已修改，请先点击“保存配置”。保存后才能应用到已有图片。",
                    "确定");
                return;
            }

            if (!_savedConfig.Enabled)
            {
                Debug.Log("[ImageImportSettings] 工具已关闭，未应用任何图片。 ");
                return;
            }

            int changed = 0;
            int missingPreset = 0;
            var paths = AssetDatabase.GetAllAssetPaths();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in paths)
                {
                    if (!IsTexturePath(path))
                        continue;

                    var rule = _savedConfig.FindMatchingRule(path);
                    if (rule == null)
                        continue;

                    if (rule.Preset == null)
                    {
                        missingPreset++;
                        continue;
                    }

                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null)
                        continue;

                    ImageImportSettingsConfig.ApplyPreset(importer, rule.Preset);
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (missingPreset > 0)
                Debug.LogWarning($"[ImageImportSettings] 已应用 {changed} 张图片；有 {missingPreset} 张图片匹配到规则但未设置预设，因此未修改。 ");
            else
                Debug.Log($"[ImageImportSettings] 已应用 {changed} 张图片。 ");
        }

        public override void SaveChanges()
        {
            RefreshUnsavedChanges();
            if (HasUnsavedChanges && !SaveConfigInternal())
                return;

            base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            if (_savedConfig != null && Config != null)
            {
                EditorUtility.CopySerializedManagedFieldsOnly(_savedConfig, Config);
                _savedConfigSnapshot = SerializeConfig(Config);
            }

            SetUnsavedChanges(false);
            base.DiscardChanges();
        }

        private bool SaveConfigInternal()
        {
            if (Config == null || _savedConfig == null)
                return false;

            EditorUtility.CopySerializedManagedFieldsOnly(Config, _savedConfig);
            EditorUtility.SetDirty(_savedConfig);
            AssetDatabase.SaveAssetIfDirty(_savedConfig);
            _savedConfigSnapshot = SerializeConfig(Config);
            SetUnsavedChanges(false);
            Debug.Log("[ImageImportSettings] 配置已保存。 ");
            return true;
        }

        private void OnInspectorUpdate()
        {
            RefreshUnsavedChanges();
        }

        private void RefreshUnsavedChanges()
        {
            if (Config == null || _savedConfigSnapshot == null)
                return;

            bool changed = !string.Equals(_savedConfigSnapshot, SerializeConfig(Config), StringComparison.Ordinal);
            if (changed != _hasUnsavedChanges)
                SetUnsavedChanges(changed);
        }

        private void SetUnsavedChanges(bool value)
        {
            _hasUnsavedChanges = value;
            hasUnsavedChanges = value;
            Repaint();
        }

        private static ImageImportSettingsConfig CreateEditingCopy(ImageImportSettingsConfig source)
        {
            if (source == null)
                return null;

            var copy = Instantiate(source);
            copy.hideFlags = HideFlags.HideAndDontSave;
            return copy;
        }

        private static string SerializeConfig(ImageImportSettingsConfig config)
        {
            return config == null ? string.Empty : EditorJsonUtility.ToJson(config, false);
        }

        private void DestroyEditingCopy()
        {
            if (Config == null || Config == _savedConfig)
            {
                _editingConfig = null;
                return;
            }

            if (!EditorUtility.IsPersistent(Config))
                DestroyImmediate(Config);

            _editingConfig = null;
        }

        private static bool IsTexturePath(string path)
        {
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                   extension == ".tga" || extension == ".psd" || extension == ".tif" || extension == ".tiff" ||
                   extension == ".exr" || extension == ".gif" || extension == ".bmp";
        }
    }
}
