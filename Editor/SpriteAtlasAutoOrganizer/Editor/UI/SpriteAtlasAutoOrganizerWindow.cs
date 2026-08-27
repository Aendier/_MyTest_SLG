using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// Dry Run / Generate / Validate 窗口。Analyze 不改资源。
    /// </summary>
    public sealed class SpriteAtlasAutoOrganizerWindow : EditorWindow
    {
        private static readonly int[] AtlasSizeOptions = { 1024, 2048, 4096 };

        private SpriteAtlasAutoOrganizerConfig _config;
        private AnalysisResult _analysis;
        private GenerateResult _generate;
        private Vector2 _scroll;
        private string _status = "尚未分析。请先点 Analyze（Dry Run）。";

        [MenuItem("Tools/Sprite Atlas/Auto Organizer")]
        public static void Open()
        {
            var window = GetWindow<SpriteAtlasAutoOrganizerWindow>("Sprite Atlas Auto Organizer");
            window.minSize = new Vector2(520f, 560f);
            window.Show();
        }

        private void OnEnable()
        {
            _config = AtlasOrganizer.LoadOrCreateConfig();
        }

        private void OnGUI()
        {
            if (_config == null)
            {
                EditorGUILayout.HelpBox("无法加载配置。", MessageType.Error);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("Sprite Atlas Auto Organizer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "当前是测试分配：Analyze 只读引用并预览分组，不改现有脚本/图片/正式图集。\n" +
                "不会把贴图像素加载进内存。过程中可点 Cancel。写出也只进 _MyTest_SLG/TestOutput。",
                MessageType.Info);

            SerializedObject so = new SerializedObject(_config);
            so.Update();
            DrawScanRoot(so);
            DrawIntPopup("Atlas Size", so.FindProperty("maxAtlasSize"), AtlasSizeOptions);
            EditorGUILayout.PropertyField(so.FindProperty("maxSpriteCount"), new GUIContent("Max Sprite Count"));
            EditorGUILayout.PropertyField(so.FindProperty("incremental"), new GUIContent("Incremental"));
            bool isolation = !so.FindProperty("allowCrossPackage").boolValue;
            isolation = EditorGUILayout.Toggle("Package Isolation", isolation);
            so.FindProperty("allowCrossPackage").boolValue = !isolation;
            so.FindProperty("outputPath").stringValue = AtlasOrganizer.DefaultTestOutputPath;
            EditorGUILayout.LabelField("Test Output", AtlasOrganizer.DefaultTestOutputPath);
            so.ApplyModifiedProperties();
            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(_config);

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Analyze 分配预览", GUILayout.Height(32)))
                RunAnalyze();

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(_analysis == null))
            {
                if (GUILayout.Button("写出测试图集（仅 _MyTest_SLG）", GUILayout.Height(24)))
                    RunGenerate();
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Analysis", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(_status, MessageType.None);
            if (_analysis != null)
            {
                EditorGUILayout.LabelField("Sprites", _analysis.Stats.SpriteCount.ToString());
                EditorGUILayout.LabelField("Prefabs", _analysis.Stats.PrefabCount.ToString());
                EditorGUILayout.LabelField("Scenes", _analysis.Stats.SceneCount.ToString());
                EditorGUILayout.LabelField("Skipped Hosts", _analysis.Stats.SkippedHostCount.ToString());
                DrawAtlasPreview();
                DrawDiff();
            }

            if (_generate != null)
                DrawGenerate();

            EditorGUILayout.EndScrollView();
        }

        private void DrawScanRoot(SerializedObject so)
        {
            SerializedProperty roots = so.FindProperty("scanRoots");
            if (roots.arraySize == 0)
                roots.arraySize = 1;
            SerializedProperty first = roots.GetArrayElementAtIndex(0);
            EditorGUILayout.PropertyField(first, new GUIContent("Scan Root"));
            EditorGUILayout.PropertyField(roots, new GUIContent("All Scan Roots"), true);
        }

        private static void DrawIntPopup(string label, SerializedProperty property, int[] options)
        {
            int current = property.intValue;
            int index = 0;
            var labels = new GUIContent[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                labels[i] = new GUIContent(options[i].ToString());
                if (options[i] == current)
                    index = i;
            }

            int next = EditorGUILayout.Popup(new GUIContent(label), index, labels);
            property.intValue = options[next];
        }

        private void DrawAtlasPreview()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Atlas Preview", EditorStyles.boldLabel);
            int show = Mathf.Min(_analysis.Clusters.Count, 80);
            for (int i = 0; i < show; i++)
            {
                AtlasCluster cluster = _analysis.Clusters[i];
                EditorGUILayout.LabelField(
                    cluster.StableName,
                    cluster.Sprites.Count + " Sprites    " +
                    cluster.EstimatedWidth + "x" + cluster.EstimatedHeight);
            }

            if (_analysis.Clusters.Count > show)
                EditorGUILayout.LabelField("... 其余 " + (_analysis.Clusters.Count - show) + " 个已省略");
        }

        private void DrawDiff()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Diff", EditorStyles.boldLabel);
            if (_analysis.Diffs.Count == 0)
            {
                EditorGUILayout.LabelField("无变化");
                return;
            }

            int show = Mathf.Min(_analysis.Diffs.Count, 40);
            for (int i = 0; i < show; i++)
            {
                AtlasDiffEntry diff = _analysis.Diffs[i];
                var builder = new StringBuilder();
                builder.Append(diff.AtlasName);
                if (diff.IsNew)
                    builder.Append("  [New]");
                if (diff.IsDeleted)
                    builder.Append("  [Delete]");
                builder.AppendLine();
                AppendTokens(builder, "+", diff.Added);
                AppendTokens(builder, "-", diff.Removed);
                if (!string.IsNullOrEmpty(diff.Reason))
                    builder.Append("Reason: ").Append(diff.Reason);
                EditorGUILayout.HelpBox(builder.ToString(), MessageType.None);
            }
        }

        private void DrawGenerate()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Written", _generate.WrittenAtlasPaths.Count.ToString());
            EditorGUILayout.LabelField("Packed", _generate.PackedAtlasPaths.Count.ToString());
            EditorGUILayout.LabelField("Deleted", _generate.DeletedAtlasPaths.Count.ToString());
            for (int i = 0; i < _generate.Issues.Count; i++)
            {
                ValidationIssue issue = _generate.Issues[i];
                EditorGUILayout.HelpBox(issue.Message, issue.IsError ? MessageType.Error : MessageType.Warning);
            }
        }

        private static void AppendTokens(StringBuilder builder, string prefix, List<string> tokens)
        {
            int show = Mathf.Min(tokens.Count, 12);
            for (int i = 0; i < show; i++)
                builder.Append(prefix).Append(' ').AppendLine(tokens[i]);
            if (tokens.Count > show)
                builder.AppendLine(prefix + " ... +" + (tokens.Count - show));
        }

        private void RunAnalyze()
        {
            _generate = null;
            try
            {
                _analysis = AtlasOrganizer.Analyze(_config);
                _status = "Analyze 完成。未改任何现有脚本/图集。Clusters=" +
                          _analysis.Stats.ClusterCount +
                          " Changed=" + _analysis.Stats.ChangedAtlasCount;
            }
            catch (System.OperationCanceledException)
            {
                _analysis = null;
                _status = "已取消分析。";
            }
            catch (System.Exception ex)
            {
                _analysis = null;
                _status = "分析失败（未改现有资源）：" + ex.Message;
                Debug.LogException(ex);
            }
        }

        private void RunGenerate()
        {
            _generate = AtlasOrganizer.Generate(_config, _analysis);
            _status = _generate.Success
                ? "Generate 完成"
                : "Generate 失败: " + _generate.Error;
        }

        private void RunValidate()
        {
            if (_analysis == null)
                _analysis = AtlasOrganizer.Analyze(_config);

            List<ValidationIssue> issues = AtlasOrganizer.Validate(_config, _analysis);
            _generate = _generate ?? new GenerateResult();
            _generate.Issues.Clear();
            _generate.Issues.AddRange(issues);
            _status = AtlasOrganizer.HasError(issues)
                ? "Validate 失败"
                : "Validate 通过";
        }

    }
}
