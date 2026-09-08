using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools
{
    public sealed class ResourceValidationWindow : EditorWindow
    {
        private const double ScanTimeBudgetSeconds = 0.008d;

        private ResourceValidationConfig _config;
        private ResourceValidationConfig _savedConfig;
        private Vector2 _scroll;
        private bool _hasUnsavedChanges;
        private ResourceValidationConfig _scanConfig;
        private string[] _scanPaths;
        private int _scanIndex;
        private int _scanWarnings;
        private bool _isScanning;

        public static void Open()
        {
            var window = GetWindow<ResourceValidationWindow>("资源合法性检查");
            window.minSize = new Vector2(620f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            LoadEditingConfig();
            AssemblyReloadEvents.beforeAssemblyReload += CancelScanForReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= CancelScanForReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            CancelScan();
            DestroyEditingConfig();
        }

        private void OnGUI()
        {
            if (_config == null) LoadEditingConfig();
            EditorGUI.BeginChangeCheck();
            if (_hasUnsavedChanges)
                EditorGUILayout.HelpBox("配置已修改，请点击保存配置写入项目。", MessageType.Warning);
            if (_isScanning)
                EditorGUILayout.HelpBox($"正在扫描：{_scanIndex}/{_scanPaths.Length} 个资源路径，已发现 {_scanWarnings} 条报警。", MessageType.Info);
            _config.Enabled = EditorGUILayout.Toggle("启用自动检查", _config.Enabled);
            _config.CheckChineseNames = EditorGUILayout.Toggle("检查全工程中文命名", _config.CheckChineseNames);
            EditorGUILayout.HelpBox("导入或移动资源时立即检查对应资源。保存配置、资源刷新或脚本编译完成后，会在 1 秒无新变化后异步检查规则文件夹中的图片。运行时不会触发扫描，全量扫描仅由按钮触发。", MessageType.Info);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("文件夹规则", EditorStyles.boldLabel);
            if (_config.Rules == null)
            {
                _config.Rules = new List<ResourceValidationFolderRule>();
                _hasUnsavedChanges = true;
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _config.Rules.Count; i++)
            {
                var rule = _config.Rules[i];
                if (rule == null)
                {
                    _config.Rules[i] = new ResourceValidationFolderRule();
                    rule = _config.Rules[i];
                    _hasUnsavedChanges = true;
                }
                using (new EditorGUILayout.VerticalScope("box"))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        rule.Folder = (DefaultAsset)EditorGUILayout.ObjectField("文件夹", rule.Folder, typeof(DefaultAsset), false);
                        if (GUILayout.Button("删除", GUILayout.Width(50f)))
                        {
                            _config.Rules.RemoveAt(i);
                            _hasUnsavedChanges = true;
                            i--;
                            continue;
                        }
                    }
                    rule.IncludeSubfolders = EditorGUILayout.Toggle("包含子文件夹", rule.IncludeSubfolders);
                    rule.MaxWidth = Mathf.Max(0, EditorGUILayout.IntField("图片最大宽度", rule.MaxWidth));
                    rule.MaxHeight = Mathf.Max(0, EditorGUILayout.IntField("图片最大高度", rule.MaxHeight));
                }
            }
            EditorGUILayout.EndScrollView();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("添加文件夹规则"))
                {
                    _config.Rules.Add(new ResourceValidationFolderRule());
                    _hasUnsavedChanges = true;
                }
                if (EditorGUI.EndChangeCheck()) _hasUnsavedChanges = true;
                if (GUILayout.Button("保存配置")) Save();
                if (_isScanning)
                {
                    if (GUILayout.Button("取消扫描")) CancelScan();
                }
                else if (GUILayout.Button("全量扫描"))
                {
                    StartScan();
                }
            }
        }

        private void Save()
        {
            if (_config == null || _savedConfig == null) return;
            EditorUtility.CopySerializedManagedFieldsOnly(_config, _savedConfig);
            EditorUtility.SetDirty(_savedConfig);
            AssetDatabase.SaveAssetIfDirty(_savedConfig);
            _hasUnsavedChanges = false;
            ResourceValidationAutoScanner.QueueScan("保存配置");
        }

        private void StartScan()
        {
            if (_isScanning || _savedConfig == null) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("运行时不检查", "请退出播放模式后再执行全量扫描。", "确定");
                return;
            }
            if (_hasUnsavedChanges)
            {
                EditorUtility.DisplayDialog("配置尚未保存", "请先点击“保存配置”，再执行全量扫描。", "确定");
                return;
            }
            if (!ResourceValidationAutoScanner.TryBeginManualScan())
            {
                EditorUtility.DisplayDialog("自动扫描进行中", "请等待当前规则文件夹扫描完成或取消后，再执行全量扫描。", "确定");
                return;
            }

            try
            {
                _scanPaths = AssetDatabase.GetAllAssetPaths();
                _scanConfig = Instantiate(_savedConfig);
                _scanConfig.hideFlags = HideFlags.HideAndDontSave;
                _scanIndex = 0;
                _scanWarnings = 0;
                _isScanning = true;
                EditorApplication.update += ProcessScanBatch;
                Repaint();
            }
            catch (Exception exception)
            {
                EditorApplication.update -= ProcessScanBatch;
                if (_scanConfig != null)
                    DestroyImmediate(_scanConfig);
                _scanConfig = null;
                _scanPaths = null;
                _scanIndex = 0;
                _scanWarnings = 0;
                _isScanning = false;
                ResourceValidationAutoScanner.EndManualScan();
                Debug.LogError($"[资源合法性检查] 全量扫描启动失败：\n{exception}");
            }
        }

        private void ProcessScanBatch()
        {
            if (!_isScanning) return;

            try
            {
                if (_scanIndex >= _scanPaths.Length)
                {
                    FinishScan(false);
                    return;
                }

                string currentPath = _scanPaths[_scanIndex];
                float progress = _scanPaths.Length == 0 ? 1f : (float)_scanIndex / _scanPaths.Length;
                if (EditorUtility.DisplayCancelableProgressBar("资源合法性检查", currentPath, progress))
                {
                    FinishScan(true);
                    return;
                }

                double frameStartTime = EditorApplication.timeSinceStartup;
                do
                {
                    string path = _scanPaths[_scanIndex];
                    if (path.StartsWith("Assets/", StringComparison.Ordinal))
                        _scanWarnings += ResourceValidationUtility.Validate(path, _scanConfig);
                    _scanIndex++;
                }
                while (_scanIndex < _scanPaths.Length &&
                       EditorApplication.timeSinceStartup - frameStartTime < ScanTimeBudgetSeconds);

                if (_scanIndex >= _scanPaths.Length)
                    FinishScan(false);
                else
                    Repaint();
            }
            catch (Exception exception)
            {
                string failedPath = _scanIndex < _scanPaths.Length ? _scanPaths[_scanIndex] : "<unknown>";
                Debug.LogError($"[资源合法性检查] 全量扫描失败，当前资源：{failedPath}\n{exception}");
                FinishScan(false, true);
            }
        }

        private void CancelScanForReload()
        {
            CancelScan();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
                CancelScan();
        }

        private void CancelScan()
        {
            if (_isScanning)
                FinishScan(true);
        }

        private void FinishScan(bool cancelled, bool failed = false)
        {
            int scanned = _scanIndex;
            int total = _scanPaths != null ? _scanPaths.Length : 0;
            int warnings = _scanWarnings;

            EditorApplication.update -= ProcessScanBatch;
            EditorUtility.ClearProgressBar();
            if (_scanConfig != null)
                DestroyImmediate(_scanConfig);

            _scanConfig = null;
            _scanPaths = null;
            _scanIndex = 0;
            _scanWarnings = 0;
            _isScanning = false;
            ResourceValidationAutoScanner.EndManualScan();
            Repaint();

            if (failed) return;
            if (cancelled)
                Debug.Log($"[资源合法性检查] 全量扫描已取消：已处理 {scanned}/{total} 个资源路径，发现 {warnings} 条报警。");
            else
                Debug.Log($"[资源合法性检查] 全量扫描完成：已处理 {scanned} 个资源路径，发现 {warnings} 条报警。");
        }

        private void LoadEditingConfig()
        {
            DestroyEditingConfig();
            _savedConfig = ResourceValidationConfig.LoadOrCreate();
            _config = Instantiate(_savedConfig);
            _config.hideFlags = HideFlags.HideAndDontSave;
            _hasUnsavedChanges = false;
        }

        private void DestroyEditingConfig()
        {
            if (_config != null && !EditorUtility.IsPersistent(_config))
                DestroyImmediate(_config);
            _config = null;
            _savedConfig = null;
        }
    }
}
