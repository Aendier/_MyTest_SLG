using System;
using UnityEngine;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// 自动图集规划器配置。只影响分析与生成行为，不会移动原始图片。
    /// </summary>
    [CreateAssetMenu(
        fileName = "SpriteAtlasAutoOrganizerConfig",
        menuName = "Sprite Atlas/Auto Organizer Config")]
    public sealed class SpriteAtlasAutoOrganizerConfig : ScriptableObject
    {
        public string[] scanRoots = { "Assets/GameAssets/ABNew" };

        public int maxAtlasSize = 2048;

        public int maxSpriteCount = 500;

        public float prefabReferenceWeight = 10f;

        public float sceneReferenceWeight = 3f;

        public bool allowCrossPackage = false;

        public bool allowSharedSprite = false;

        public bool incremental = true;

        /// <summary>测试阶段只允许写到 _MyTest_SLG，避免碰到正式图集。</summary>
        public string outputPath =
            "Assets/_MyTest_SLG/Editor/SpriteAtlasAutoOrganizer/TestOutput";

        public string manualAtlasPath = "Assets/GameAssets/ABNew/SpriteAtlas/Atlas";

        [Header("约束与增量")]
        public int maxSpritePerPrefab = 64;

        public int packPadding = 4;

        [Tooltip("面积预估放大系数，用于补偿 Padding / 装箱空隙")]
        public float packingSlack = 1.15f;

        public bool validateOnGenerate = true;

        public string cacheDirectory = "Library/SpriteAtlasAutoOrganizer";

        [Header("手工约束")]
        public LockedSpriteGroup[] lockedGroups = Array.Empty<LockedSpriteGroup>();

        [Tooltip("禁止与其它 Sprite 合并的资源 GUID 或路径")]
        public string[] neverShareSprites = Array.Empty<string>();

        [Tooltip("按路径前缀指定 Domain，显式配置优先于 YooAsset 解析")]
        public DomainPathRule[] domainPathRules = Array.Empty<DomainPathRule>();
    }

    [Serializable]
    public sealed class LockedSpriteGroup
    {
        public string groupName;

        [Tooltip("必须待在同一 Atlas 的 Sprite GUID 或资源路径")]
        public string[] sprites = Array.Empty<string>();
    }

    [Serializable]
    public sealed class DomainPathRule
    {
        public string pathPrefix;
        public string domain;
    }
}
