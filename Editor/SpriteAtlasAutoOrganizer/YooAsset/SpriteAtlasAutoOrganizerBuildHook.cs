using SpriteAtlasAutoOrganizer.Editor;
using UnityEngine;
using YooAsset.Editor;

namespace SpriteAtlasAutoOrganizer.YooAsset.Editor
{
    /// <summary>
    /// YooAsset 正式 Build 前置入口。Core 不依赖 YooAsset，由本程序集桥接。
    /// Validate 失败时抛异常，禁止继续打包。
    /// </summary>
    public static class SpriteAtlasAutoOrganizerBuildHook
    {
        public static void Execute()
        {
            // 测试阶段只跑分配分析，不写正式图集、不介入现有打包脚本。
            SpriteAtlasAutoOrganizerConfig config = AtlasOrganizer.LoadOrCreateConfig();
            AnalysisResult analysis = AtlasOrganizer.Analyze(config);
            Debug.Log("[AtlasOrganizer] 测试分析完成。Clusters=" +
                      analysis.Stats.ClusterCount +
                      " Changed=" + analysis.Stats.ChangedAtlasCount);
        }
    }

    /// <summary>
    /// 可插入自定义 YooAsset pipeline 的前置任务。
    /// </summary>
    public sealed class TaskPrepareSpriteAtlas : IBuildTask
    {
        public void Run(BuildContext context)
        {
            Debug.Log("[AtlasOrganizer] 测试阶段只分析分配，不写正式图集");
            SpriteAtlasAutoOrganizerBuildHook.Execute();
        }
    }
}
