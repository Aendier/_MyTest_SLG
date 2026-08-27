using System;
using System.Collections.Generic;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// Sprite 内部唯一键：资源 GUID + 子资源 fileID。
    /// Multiple Sprite 共用同一 Texture GUID，必须带 fileID 才能区分。
    /// </summary>
    public readonly struct SpriteKey : IEquatable<SpriteKey>
    {
        public readonly string Guid;
        public readonly long FileId;

        public SpriteKey(string guid, long fileId)
        {
            Guid = guid ?? string.Empty;
            FileId = fileId;
        }

        public string Token => Guid + ":" + FileId;

        public static bool TryParse(string token, out SpriteKey key)
        {
            key = default;
            if (string.IsNullOrEmpty(token))
                return false;

            int split = token.LastIndexOf(':');
            if (split <= 0 || split == token.Length - 1)
                return false;

            if (!long.TryParse(token.Substring(split + 1), out long fileId))
                return false;

            key = new SpriteKey(token.Substring(0, split), fileId);
            return true;
        }

        public bool Equals(SpriteKey other)
        {
            return FileId == other.FileId &&
                   string.Equals(Guid, other.Guid, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is SpriteKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (Guid != null ? Guid.GetHashCode() : 0) * 397 ^ FileId.GetHashCode();
        }

        public override string ToString()
        {
            return Token;
        }
    }

    public sealed class SpriteRecord
    {
        public SpriteKey Key;
        public string AssetPath;
        public string Name;
        public string Domain = "Default";
        public int Width;
        public int Height;
        public bool NeverShare;
        public bool InManualAtlas;
        public long EstimatedArea;
    }

    public sealed class HostRecord
    {
        public string Guid;
        public string AssetPath;
        public bool IsScene;
        public string DependencyHash;
        public readonly List<SpriteKey> Sprites = new List<SpriteKey>();
    }

    public sealed class AtlasCluster
    {
        public string Domain;
        public string StableName;
        public string Reason;
        public readonly List<SpriteKey> Sprites = new List<SpriteKey>();
        public long EstimatedArea;
        public int EstimatedWidth;
        public int EstimatedHeight;
        public bool Changed;
    }

    public sealed class AtlasDiffEntry
    {
        public string AtlasName;
        public readonly List<string> Added = new List<string>();
        public readonly List<string> Removed = new List<string>();
        public string Reason;
        public bool IsNew;
        public bool IsDeleted;
    }

    public sealed class ValidationIssue
    {
        public bool IsError;
        public string Message;
        public string AssetPath;
    }

    public sealed class AnalysisStats
    {
        public int SpriteCount;
        public int PrefabCount;
        public int SceneCount;
        public int ClusterCount;
        public int ChangedAtlasCount;
        public int SkippedHostCount;
    }

    /// <summary>
    /// Analyze 的只读结果。Generate 必须基于这份计划，避免 Dry Run 与落盘不一致。
    /// </summary>
    public sealed class AnalysisResult
    {
        public readonly Dictionary<SpriteKey, SpriteRecord> Sprites =
            new Dictionary<SpriteKey, SpriteRecord>();

        public readonly Dictionary<string, HashSet<SpriteKey>> PrefabToSprites =
            new Dictionary<string, HashSet<SpriteKey>>();

        public readonly Dictionary<SpriteKey, HashSet<string>> SpriteToPrefabs =
            new Dictionary<SpriteKey, HashSet<string>>();

        public readonly Dictionary<string, HashSet<SpriteKey>> SceneToSprites =
            new Dictionary<string, HashSet<SpriteKey>>();

        public readonly List<AtlasCluster> Clusters = new List<AtlasCluster>();
        public readonly List<AtlasDiffEntry> Diffs = new List<AtlasDiffEntry>();
        public readonly List<ValidationIssue> Issues = new List<ValidationIssue>();
        public readonly AnalysisStats Stats = new AnalysisStats();
        public readonly HashSet<string> AffectedDomains = new HashSet<string>();
        public bool Incremental;
    }

    public sealed class GenerateResult
    {
        public bool Success = true;
        public string Error;
        public readonly List<string> PackedAtlasPaths = new List<string>();
        public readonly List<string> WrittenAtlasPaths = new List<string>();
        public readonly List<string> DeletedAtlasPaths = new List<string>();
        public readonly List<ValidationIssue> Issues = new List<ValidationIssue>();
    }

    public interface IAtlasDomainResolver
    {
        /// <summary>
        /// 返回 Domain 名称；无法判断时返回 null，交给后续解析器。
        /// </summary>
        string ResolveDomain(string assetPath, string assetGuid);
    }
}
