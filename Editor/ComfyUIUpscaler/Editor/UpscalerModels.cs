using System;
using System.Collections.Generic;
using UnityEngine;

namespace ComfyUIUpscaler.Editor
{
    [Serializable]
    internal sealed class TextureAssetInfo
    {
        public bool selected;
        public string assetPath;
        public string extension;
        public int width;
        public int height;
        public bool hasAlpha;
        public string textureType;
        public string spriteMode;
        public int maxTextureSize;
        public Vector4 singleBorder;
        public string warning;
        public string guid;
        public List<SpriteMetadata> sprites = new List<SpriteMetadata>();
        public List<AssetReferenceSnapshot> references = new List<AssetReferenceSnapshot>();

        [NonSerialized] public string contentSha256;
        [NonSerialized] public UpgradeAssetState upgradeState;
        [NonSerialized] public bool lastAttemptFailed;
        [NonSerialized] public string lastAttemptStatus;
        [NonSerialized] public string lastAttemptJobId;
        [NonSerialized] public string lastAttemptUtc;
        [NonSerialized] public string lastUpgradeJobId;
        [NonSerialized] public string lastUpgradeUtc;
        [NonSerialized] public string workflowSha256;
        [NonSerialized] public int lastInputWidth;
        [NonSerialized] public int lastInputHeight;
        [NonSerialized] public int lastOutputWidth;
        [NonSerialized] public int lastOutputHeight;
        [NonSerialized] public float lastActualScale;
    }

    [Serializable]
    internal sealed class SpriteMetadata
    {
        public string name;
        public string spriteId;
        public Rect rect;
        public Vector4 border;
        public Vector2 pivot;
        public int alignment;
    }

    [Serializable]
    internal sealed class AssetReferenceSnapshot
    {
        public string name;
        public string guid;
        public long localFileId;
    }

    [Serializable]
    internal sealed class AtlasPlacement
    {
        public string assetPath;
        public int pageIndex;
        public RectInt contentRect;
        public int padding;
        public int outputWidth;
        public int outputHeight;
        public float scale;
        public string stagedFile;
        public string outputSha256;
        public string outputMetaSha256;
    }

    [Serializable]
    internal sealed class AtlasPageManifest
    {
        public int pageIndex;
        public int width;
        public int height;
        public string inputFile;
        public string outputFile;
        public string promptId;
        public float outputScale;
        public List<AtlasPlacement> placements = new List<AtlasPlacement>();
    }

    [Serializable]
    internal sealed class UpscaleJobManifest
    {
        public string formatVersion = "1";
        public string jobId;
        public string createdUtc;
        public string completedUtc;
        public string status;
        public string error;
        public string sourceFolder;
        public List<string> sourceFolders = new List<string>();
        public string comfyUrl;
        public string workflowPath;
        public string workflowSha256;
        public string inputNodeId;
        public string inputFieldName;
        public string outputNodeId;
        public float expectedScale;
        public int padding;
        public int maxAtlasEdge;
        public long maxAtlasPixels;
        // 续跑所需的运行参数，随任务落盘以便脱离当前窗口状态独立恢复
        public int requestTimeoutSeconds;
        public int jobTimeoutMinutes;
        public int jpegQuality;
        public long originalTotalBytes;
        public long outputTotalBytes;
        public List<TextureAssetInfo> assets = new List<TextureAssetInfo>();
        public List<AtlasPageManifest> pages = new List<AtlasPageManifest>();
        public List<string> log = new List<string>();
    }

    internal sealed class UpscalerRunSettings
    {
        public string sourceFolder;
        public List<string> sourceFolders = new List<string>();
        public string comfyUrl;
        public string workflowPath;
        public string inputNodeId;
        public string inputFieldName;
        public string outputNodeId;
        public float expectedScale;
        public int padding;
        public int maxAtlasEdge;
        public long maxAtlasPixels;
        public int requestTimeoutSeconds;
        public int jobTimeoutMinutes;
        public int jpegQuality;
    }

    internal static class JobStatus
    {
        public const string Created = "Created";
        public const string Processing = "Processing";
        public const string ReadyToCommit = "ReadyToCommit";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Canceled = "Canceled";
        public const string RolledBack = "RolledBack";
    }
}
