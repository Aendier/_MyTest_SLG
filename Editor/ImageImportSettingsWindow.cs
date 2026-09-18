using System;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools
{
    public sealed class ImageImportSettingsWindow : OdinEditorWindow
    {
        [ShowInInspector, PropertyOrder(-2)]
        [LabelText("当前实际生效状态")]
        private string EffectiveStatus => _savedConfig == null
            ? "配置不可用"
            : _savedConfig.Enabled ? "已启用" : "未启用";

        [InfoBox("编辑中的“启用自动设置”与当前实际生效状态不一致。保存配置前，图片导入仍使用上方显示的实际状态。", InfoMessageType.Warning, VisibleIf = nameof(HasPendingEnabledChange))]
        [InfoBox("实际配置已被外部修改。为避免覆盖外部修改，保存时需要选择保留哪一份配置。", InfoMessageType.Warning, VisibleIf = nameof(HasExternalChanges))]
        [InlineEditor(InlineEditorObjectFieldModes.Hidden)]
        [LabelText("编辑中的图片导入设置（保存后生效）")]
        [ShowInInspector, PropertyOrder(-1)]
        private ImageImportSettingsConfig Config
        {
            get => _editingConfig;
            set => _editingConfig = value;
        }

        [NonSerialized]
        private ImageImportSettingsConfig _editingConfig;
        private ImageImportSettingsConfig _savedConfig;
        private string _editingConfigSnapshot;
        private string _savedConfigSnapshot;
        private bool _hasUnsavedChanges;
        private bool _hasExternalChanges;

        private bool HasUnsavedChanges => _hasUnsavedChanges;
        private bool HasExternalChanges => _hasExternalChanges;
        private bool HasPendingEnabledChange =>
            Config != null && _savedConfig != null && Config.Enabled != _savedConfig.Enabled;

        public static void Open()
        {
            var window = GetWindow<ImageImportSettingsWindow>("图片导入设置");
            window.minSize = new Vector2(560, 420);
            window.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            OnEndGUI -= RefreshWindowState;
            DestroyEditingCopy();
            _savedConfig = ImageImportSettingsConfig.LoadOrCreate();
            _editingConfig = CreateEditingCopy(_savedConfig);
            _editingConfigSnapshot = SerializeConfig(_editingConfig);
            _savedConfigSnapshot = SerializeConfig(_savedConfig);
            SetExternalChanges(false);
            SetUnsavedChanges(false);
            saveChangesMessage = "图片导入设置已修改。保存配置后，新图片导入和已有图片应用才会使用新设置。";
            OnEndGUI += RefreshWindowState;
        }

        protected override void OnDisable()
        {
            OnEndGUI -= RefreshWindowState;
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
            RefreshWindowState();
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
            RefreshWindowState();
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
            RefreshWindowState();
            if (HasUnsavedChanges && !SaveConfigInternal())
                return;

            base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            if (_savedConfig != null && Config != null)
            {
                EditorUtility.CopySerializedManagedFieldsOnly(_savedConfig, Config);
                _editingConfigSnapshot = SerializeConfig(Config);
                _savedConfigSnapshot = SerializeConfig(_savedConfig);
            }

            SetExternalChanges(false);
            SetUnsavedChanges(false);
            base.DiscardChanges();
        }

        private bool SaveConfigInternal()
        {
            if (Config == null || _savedConfig == null)
                return false;

            RefreshWindowState();
            if (HasExternalChanges)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "实际配置已被外部修改",
                    "编辑中的配置和当前实际配置都发生了修改。覆盖保存会丢失外部修改，也可以重新加载当前实际配置并放弃窗口内的修改。",
                    "覆盖并保存",
                    "取消",
                    "重新加载实际配置");

                if (choice == 1)
                    return false;

                if (choice == 2)
                {
                    ReloadFromSavedConfig();
                    Debug.Log("[ImageImportSettings] 已重新加载当前实际配置，窗口内未保存的修改已放弃。 ");
                    return true;
                }
            }

            EditorUtility.CopySerializedManagedFieldsOnly(Config, _savedConfig);
            EditorUtility.SetDirty(_savedConfig);
            AssetDatabase.SaveAssetIfDirty(_savedConfig);
            _editingConfigSnapshot = SerializeConfig(Config);
            _savedConfigSnapshot = SerializeConfig(_savedConfig);
            SetExternalChanges(false);
            SetUnsavedChanges(false);
            Debug.Log("[ImageImportSettings] 配置已保存。 ");
            return true;
        }

        private void OnInspectorUpdate()
        {
            RefreshWindowState();
        }

        private void RefreshWindowState()
        {
            if (Config == null || _savedConfig == null ||
                _editingConfigSnapshot == null || _savedConfigSnapshot == null)
                return;

            string editingSnapshot = SerializeConfig(Config);
            string currentSavedSnapshot = SerializeConfig(_savedConfig);
            bool editingChanged = !string.Equals(_editingConfigSnapshot, editingSnapshot, StringComparison.Ordinal);
            bool savedConfigChanged = !string.Equals(_savedConfigSnapshot, currentSavedSnapshot, StringComparison.Ordinal);

            if (savedConfigChanged && string.Equals(editingSnapshot, currentSavedSnapshot, StringComparison.Ordinal))
            {
                _editingConfigSnapshot = editingSnapshot;
                _savedConfigSnapshot = currentSavedSnapshot;
                SetExternalChanges(false);
                SetUnsavedChanges(false);
                return;
            }

            if (savedConfigChanged && !editingChanged)
            {
                ReloadFromSavedConfig();
                return;
            }

            SetExternalChanges(savedConfigChanged);
            if (editingChanged != _hasUnsavedChanges)
                SetUnsavedChanges(editingChanged);
        }

        private void ReloadFromSavedConfig()
        {
            if (Config == null || _savedConfig == null)
                return;

            EditorUtility.CopySerializedManagedFieldsOnly(_savedConfig, Config);
            _editingConfigSnapshot = SerializeConfig(Config);
            _savedConfigSnapshot = SerializeConfig(_savedConfig);
            SetExternalChanges(false);
            SetUnsavedChanges(false);
        }

        private void SetExternalChanges(bool value)
        {
            if (_hasExternalChanges == value)
                return;

            _hasExternalChanges = value;
            Repaint();
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
