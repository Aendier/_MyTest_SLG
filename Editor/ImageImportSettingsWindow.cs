using System;
using System.Collections.Generic;
using System.Text;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D;

namespace UIR.EditorTools
{
    public sealed class ImageImportSettingsWindow : OdinEditorWindow
    {
        [ShowInInspector, PropertyOrder(-2)]
        [LabelText("当前实际生效状态")]
        private string EffectiveStatus => _savedConfig == null
            ? "配置不可用"
            : _savedConfig.Enabled ? "已启用" : "未启用";

        [ShowInInspector, PropertyOrder(-3)]
        [LabelText("详细日志（仅本机）")]
        private bool VerboseLogging
        {
            get => ImageImportSettingsConfig.VerboseLogging;
            set => ImageImportSettingsConfig.VerboseLogging = value;
        }

        [InfoBox("实际配置已被外部修改。为避免覆盖外部修改，保存时需要选择保留哪一份配置。", InfoMessageType.Warning, VisibleIf = nameof(HasExternalChanges))]
        [InlineEditor(InlineEditorObjectFieldModes.Hidden)]
        [LabelText("图片导入与压缩规则（保存后生效；列表靠前的规则优先）")]
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

        [ShowInInspector, ReadOnly, MultiLineProperty(6), PropertyOrder(3)]
        [LabelText("匹配预览（按已保存配置生成，应用时重新扫描）")]
        private string _previewSummary = "尚未预览。";

        private bool HasUnsavedChanges => _hasUnsavedChanges;
        private bool HasExternalChanges => _hasExternalChanges;
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
            saveChangesMessage = "图片规则修改后需保存；启用状态和详细日志为当前用户本机设置。";
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

        [Button("应用到已有资源", ButtonSizes.Large), PropertyOrder(2)]
        [InfoBox("每条规则可配置多个文件夹，只应用第一条命中规则。只改压缩时可留空导入预设；压缩默认保留尺寸和编码质量。预设继续保留已有图片的 Sprite 数据、Wrap Mode 和平台设置。图集仅在勾选包含 Sprite Atlas 后通过批量应用处理。", InfoMessageType.Info)]
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
            int unchanged = 0;
            int missingSettings = 0;
            int failed = 0;
            var paths = _savedConfig.FindCandidateAssetPaths();
            if (paths.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "已保存的规则没有匹配到图片或图集。", "确定");
                return;
            }
            // 批量执行前展示真实命中数量，避免规则列表调整后误用旧预览。
            _previewSummary = BuildPreviewSummary(_savedConfig, paths);
            if (!EditorUtility.DisplayDialog("应用图片导入与压缩设置", _previewSummary, "应用", "取消"))
                return;
            bool cancelled = false;
            ImageImportSettingsConfig.LogVerbose($"开始批量应用：Enabled={_savedConfig.Enabled}，候选图片数={paths.Count}，规则数={_savedConfig.Rules?.Count ?? 0}。");
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int index = 0; index < paths.Count; index++)
                {
                    if (index == 0 || index % 32 == 0)
                    {
                        string progressPath = paths[index];
                        if (EditorUtility.DisplayCancelableProgressBar(
                                "应用图片导入设置",
                                $"正在检查 {progressPath}",
                                (float)index / paths.Count))
                        {
                            cancelled = true;
                            break;
                        }
                    }

                    string path = paths[index];

                    var rule = _savedConfig.FindMatchingRule(path);
                    if (rule == null)
                    {
                        ImageImportSettingsConfig.LogVerbose($"批处理跳过：没有匹配规则，assetPath={path}。");
                        continue;
                    }

                    if (!rule.HasSettings)
                    {
                        missingSettings++;
                        ImageImportSettingsConfig.LogVerbose($"批处理跳过：规则“{rule.RuleName}”未设置预设或压缩设置，assetPath={path}。");
                        continue;
                    }

                    try
                    {
                        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                        bool assetChanged;
                        if (importer != null)
                        {
                            assetChanged = ImageImportSettingsConfig.ApplyRule(importer, rule);
                            if (assetChanged)
                                importer.SaveAndReimport();
                        }
                        else
                        {
                            SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                            assetChanged = ImageImportSettingsConfig.ApplyAtlasCompression(atlas, rule);
                            if (assetChanged)
                            {
                                EditorUtility.SetDirty(atlas);
                                AssetDatabase.SaveAssetIfDirty(atlas);
                                AssetDatabase.ImportAsset(path);
                            }
                        }
                        if (assetChanged)
                            changed++;
                        else
                            unchanged++;
                        ImageImportSettingsConfig.LogVerbose($"批处理已检查：assetPath={path}，规则={rule.RuleName}，changed={assetChanged}。");
                    }
                    catch (Exception exception)
                    {
                        failed++;
                        Debug.LogError($"[ImageImportSettings] 应用失败：{path}\n{exception}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.StopAssetEditing();
            }

            string resultSuffix = cancelled ? " 操作已取消。" : string.Empty;
            string result = $"[ImageImportSettings] 候选资源 {paths.Count} 个，已应用 {changed}，无需修改 {unchanged}，缺少设置 {missingSettings}，失败 {failed}，未处理 {paths.Count - changed - unchanged - missingSettings - failed}。{resultSuffix}";
            if (missingSettings > 0 || failed > 0)
                Debug.LogWarning(result);
            else
                Debug.Log(result);
        }

        [Button("预览匹配结果", ButtonSizes.Large), PropertyOrder(1)]
        private void PreviewRules()
        {
            RefreshWindowState();
            if (_savedConfig == null || Config == null)
                return;
            if (HasUnsavedChanges)
            {
                EditorUtility.DisplayDialog("配置尚未保存", "请先保存配置，再预览实际生效的规则。", "确定");
                return;
            }
            _previewSummary = BuildPreviewSummary(_savedConfig, _savedConfig.FindCandidateAssetPaths());
        }

        private static string BuildPreviewSummary(ImageImportSettingsConfig config, List<string> paths)
        {
            var textureCounts = new Dictionary<ImageImportSettingsRule, int>();
            var atlasCounts = new Dictionary<ImageImportSettingsRule, int>();
            foreach (string path in paths)
            {
                ImageImportSettingsRule rule = config.FindMatchingRule(path);
                if (rule == null)
                    continue;
                Dictionary<ImageImportSettingsRule, int> counts = AssetImporter.GetAtPath(path) is TextureImporter
                    ? textureCounts : atlasCounts;
                counts.TryGetValue(rule, out int count);
                counts[rule] = count + 1;
            }

            var text = new StringBuilder($"共匹配 {paths.Count} 个资源；每个资源只应用列表中第一条命中规则。\n");
            if (config.Rules == null)
                return text.ToString();
            foreach (ImageImportSettingsRule rule in config.Rules)
            {
                if (rule == null)
                    continue;
                textureCounts.TryGetValue(rule, out int textures);
                atlasCounts.TryGetValue(rule, out int atlases);
                string settings = rule.HasSettings ? (rule.Preset != null ? $"预设 {rule.Preset.name}" : "仅压缩") : "未设置，将跳过";
                if (rule.ApplyCompression)
                    settings += $" / {rule.CompressionPlatform} / {rule.CompressionFormat} / 尺寸 {(rule.OverrideMaxSize ? rule.MaxTextureSize.ToString() : "保留")} / 质量 {(rule.OverrideCompressionQuality ? rule.CompressionQuality.ToString() : "保留")}";
                text.AppendLine($"{rule.RuleName}：图片 {textures}，图集 {atlases}；{settings}");
            }
            return text.ToString();
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
            if (value)
                _previewSummary = "配置已变化，请保存后重新预览。";
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

    }
}
