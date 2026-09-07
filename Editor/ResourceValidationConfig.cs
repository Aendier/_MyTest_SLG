using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace UIR.EditorTools
{
    [Serializable]
    public sealed class ResourceValidationFolderRule
    {
        public DefaultAsset Folder;
        public bool IncludeSubfolders = true;
        public int MaxWidth;
        public int MaxHeight;

        public bool Matches(string assetPath)
        {
            if (Folder == null || string.IsNullOrEmpty(assetPath)) return false;
            string folder = AssetDatabase.GetAssetPath(Folder)?.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder)) return false;
            if (IncludeSubfolders) return assetPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
            int slash = assetPath.LastIndexOf('/');
            return slash >= 0 && assetPath.Substring(0, slash).Equals(folder, StringComparison.OrdinalIgnoreCase);
        }
    }

    [CreateAssetMenu(fileName = "ResourceValidationConfig", menuName = "UIR/Resource Validation Config")]
    public sealed class ResourceValidationConfig : ScriptableObject
    {
        public const string AssetPath = "Assets/_MyTest_SLG/Editor/ResourceValidationConfig.asset";

        public bool Enabled = true;
        public bool CheckChineseNames = true;
        public List<ResourceValidationFolderRule> Rules = new List<ResourceValidationFolderRule>();

        public static ResourceValidationConfig Load()
        {
            return AssetDatabase.LoadAssetAtPath<ResourceValidationConfig>(AssetPath);
        }

        public static ResourceValidationConfig LoadOrCreate()
        {
            var config = Load();
            if (config != null) return config;
            config = CreateInstance<ResourceValidationConfig>();
            string directory = Path.GetDirectoryName(AssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
                Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), directory));
            AssetDatabase.CreateAsset(config, AssetPath);
            AssetDatabase.SaveAssets();
            return config;
        }
    }

    public sealed class ResourceValidationPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var config = ResourceValidationConfig.Load();
            if (config == null || !config.Enabled) return;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(paths, imported); Add(paths, moved);
            foreach (string path in paths) ResourceValidationUtility.Validate(path, config);
        }

        private static void Add(HashSet<string> paths, string[] values)
        {
            if (values == null) return;
            foreach (string value in values) if (!string.IsNullOrEmpty(value)) paths.Add(value);
        }
    }

    [InitializeOnLoad]
    internal static class ResourceValidationAutoScanner
    {
        private const double DebounceSeconds = 1d;
        private const double ScanTimeBudgetSeconds = 0.008d;

        private static bool _scanQueued;
        private static bool _isScanning;
        private static bool _cancelRequested;
        private static double _scanAt;
        private static string _reason;
        private static ResourceValidationConfig _scanConfig;
        private static string[] _paths;
        private static int _index;
        private static int _warnings;
        private static int _progressId = -1;

        public static bool IsScanning => _isScanning;
        private static bool ManualScanRunning { get; set; }

        static ResourceValidationAutoScanner()
        {
            EditorApplication.projectChanged += OnProjectChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += CleanupBeforeReload;
        }

        public static void QueueScan(string reason)
        {
            if (_isScanning || ManualScanRunning) return;
            _scanQueued = true;
            _scanAt = EditorApplication.timeSinceStartup + DebounceSeconds;
            _reason = reason;
        }

        public static bool TryBeginManualScan()
        {
            if (_isScanning || ManualScanRunning) return false;
            _scanQueued = false;
            ManualScanRunning = true;
            return true;
        }

        public static void EndManualScan()
        {
            ManualScanRunning = false;
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            QueueScan("脚本编译完成");
        }

        private static void OnProjectChanged()
        {
            QueueScan("资源刷新");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                QueueScan("进入播放模式");
        }

        private static void Update()
        {
            if (_isScanning)
            {
                ProcessBatch();
                return;
            }

            if (_scanQueued && !ManualScanRunning && EditorApplication.timeSinceStartup >= _scanAt)
                StartScan();
        }

        private static void StartScan()
        {
            _scanQueued = false;
            var config = ResourceValidationConfig.Load();
            if (config == null || !config.Enabled || !HasImageRules(config)) return;

            try
            {
                _paths = AssetDatabase.GetAllAssetPaths();
                _scanConfig = UnityEngine.Object.Instantiate(config);
                _scanConfig.hideFlags = HideFlags.HideAndDontSave;
                _index = 0;
                _warnings = 0;
                _cancelRequested = false;
                _isScanning = true;
                _progressId = Progress.Start("资源合法性检查", $"正在准备规则文件夹扫描（{_reason}）");
                Progress.RegisterCancelCallback(_progressId, RequestCancel);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[资源合法性检查] 自动扫描启动失败（{_reason}）：\n{exception}");
                FinishScan(true, true);
            }
        }

        private static void ProcessBatch()
        {
            if (_cancelRequested)
            {
                FinishScan(true);
                return;
            }

            if (_paths == null || _index >= _paths.Length)
            {
                FinishScan(false);
                return;
            }

            try
            {
                double frameStartTime = EditorApplication.timeSinceStartup;
                do
                {
                    string path = _paths[_index];
                    if (path.StartsWith("Assets/", StringComparison.Ordinal))
                        _warnings += ResourceValidationUtility.ValidateImageSize(path, _scanConfig);
                    _index++;
                }
                while (_index < _paths.Length &&
                       EditorApplication.timeSinceStartup - frameStartTime < ScanTimeBudgetSeconds);

                float progress = _paths.Length == 0 ? 1f : (float)_index / _paths.Length;
                string currentPath = _index < _paths.Length ? _paths[_index] : "扫描完成";
                Progress.Report(_progressId, progress, currentPath);

                if (_index >= _paths.Length)
                    FinishScan(false);
            }
            catch (Exception exception)
            {
                string failedPath = _paths != null && _index < _paths.Length ? _paths[_index] : "<unknown>";
                Debug.LogError($"[资源合法性检查] 自动扫描失败，当前资源：{failedPath}\n{exception}");
                FinishScan(false, true);
            }
        }

        private static bool RequestCancel()
        {
            _cancelRequested = true;
            return true;
        }

        private static void FinishScan(bool cancelled, bool failed = false)
        {
            int scanned = _index;
            int total = _paths != null ? _paths.Length : 0;
            int warnings = _warnings;
            string reason = _reason;

            if (_progressId >= 0)
                Progress.Finish(_progressId, cancelled || failed ? Progress.Status.Canceled : Progress.Status.Succeeded);
            if (_scanConfig != null)
                UnityEngine.Object.DestroyImmediate(_scanConfig);

            _progressId = -1;
            _scanConfig = null;
            _paths = null;
            _index = 0;
            _warnings = 0;
            _cancelRequested = false;
            _isScanning = false;

            if (failed) return;
            if (cancelled)
                Debug.Log($"[资源合法性检查] 自动扫描已取消（{reason}）：已处理 {scanned}/{total} 个资源路径，发现 {warnings} 条报警。");
            else if (warnings > 0)
                Debug.Log($"[资源合法性检查] 自动扫描完成（{reason}）：发现 {warnings} 条报警。");
        }

        private static void CleanupBeforeReload()
        {
            _scanQueued = false;
            if (_isScanning)
                FinishScan(true);
        }

        private static bool HasImageRules(ResourceValidationConfig config)
        {
            if (config.Rules == null) return false;
            foreach (var rule in config.Rules)
            {
                if (rule != null && rule.Folder != null && (rule.MaxWidth > 0 || rule.MaxHeight > 0))
                    return true;
            }
            return false;
        }
    }

    internal static class ResourceValidationUtility
    {
        public static int Validate(string assetPath, ResourceValidationConfig config)
        {
            if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath)) return 0;
            int warnings = 0;
            bool nameRule = config.CheckChineseNames;
            if (nameRule && ContainsChineseAssetName(assetPath))
            {
                LogWarning("name|" + assetPath, "[资源合法性检查] 资源命名包含中文：" + assetPath, assetPath);
                warnings++;
            }
            warnings += ValidateImageSize(assetPath, config);
            return warnings;
        }

        public static int ValidateImageSize(string assetPath, ResourceValidationConfig config)
        {
            if (string.IsNullOrEmpty(assetPath) || config == null || AssetDatabase.IsValidFolder(assetPath) || !IsTexturePath(assetPath))
                return 0;

            ResourceValidationFolderRule matched = null;
            if (config.Rules != null)
            {
                foreach (var rule in config.Rules)
                {
                    if (rule != null && rule.Matches(assetPath)) { matched = rule; break; }
                }
            }
            if (matched != null && (matched.MaxWidth > 0 || matched.MaxHeight > 0))
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (texture != null && ((matched.MaxWidth > 0 && texture.width >= matched.MaxWidth) ||
                                        (matched.MaxHeight > 0 && texture.height >= matched.MaxHeight)))
                {
                    LogWarning("size|" + assetPath, $"[资源合法性检查] 图片尺寸达到或超过规则：{assetPath} ({texture.width}x{texture.height}，阈值 {FormatLimit(matched)})", assetPath);
                    return 1;
                }
            }
            return 0;
        }

        private static readonly Dictionary<string, double> LastWarningTimes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private static void LogWarning(string issueKey, string message, string assetPath)
        {
            double now = EditorApplication.timeSinceStartup;
            if (LastWarningTimes.TryGetValue(issueKey, out double lastTime) && now - lastTime < 1d)
                return;
            LastWarningTimes[issueKey] = now;

            // 传入资源对象后，点击 Console 日志会直接选中并定位该资源。
            UnityEngine.Object context = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (context != null)
                Debug.LogWarning(message, context);
            else
                Debug.LogWarning(message);
        }

        public static bool ContainsChinese(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value) if ((c >= '\u3400' && c <= '\u4DBF') || (c >= '\u4E00' && c <= '\u9FFF')) return true;
            return false;
        }

        private static bool ContainsChineseAssetName(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            for (int i = 1; i < segments.Length; i++)
            {
                string segment = i == segments.Length - 1 ? Path.GetFileNameWithoutExtension(segments[i]) : segments[i];
                if (ContainsChinese(segment)) return true;
            }
            return false;
        }

        private static string FormatLimit(ResourceValidationFolderRule rule)
        {
            return (rule.MaxWidth > 0 ? rule.MaxWidth.ToString() : "*") + "x" + (rule.MaxHeight > 0 ? rule.MaxHeight.ToString() : "*");
        }

        private static bool IsTexturePath(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga" || ext == ".psd" || ext == ".tif" || ext == ".tiff" || ext == ".exr" || ext == ".bmp";
        }
    }
}
