using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools
{
    public sealed class ImageImportSettingsWindow : OdinEditorWindow
    {
        [InlineEditor(InlineEditorObjectFieldModes.Foldout)]
        [LabelText("图片导入设置配置")]
        [ShowInInspector, PropertyOrder(-1)]
        private ImageImportSettingsConfig Config { get; set; }

        public static void Open()
        {
            var window = GetWindow<ImageImportSettingsWindow>("图片导入设置");
            window.minSize = new Vector2(560, 420);
            window.Show();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            Config = ImageImportSettingsConfig.LoadOrCreate();
        }

        protected override void OnDisable()
        {
            SaveConfigInternal();
            base.OnDisable();
        }

        [Button("应用到已有图片（自动保存）", ButtonSizes.Large), PropertyOrder(1)]
        [InfoBox("应用预设时会忽略以下内容：Sprite Border、Sprite Pivot、Pixels Per Unit、Sprite Sheet、Sprite Mesh Type、Alignment、Physics Shape、Tessellation Detail、Wrap Mode、平台设置。", InfoMessageType.Info)]
        [GUIColor(0.35f, 0.8f, 0.45f)]
        private void ApplyToExistingImages()
        {
            if (Config == null)
                return;

            // Odin 的修改先存在内存对象中；应用前自动落盘，确保导入回调使用的就是当前配置。
            SaveConfigInternal();

            if (!Config.Enabled)
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

                    var rule = Config.FindMatchingRule(path);
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

        private void SaveConfigInternal()
        {
            if (Config == null)
                return;
            EditorUtility.SetDirty(Config);
            AssetDatabase.SaveAssets();
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
