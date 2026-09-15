using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A persisted ignore-list item. Assets and folders use their GUID; Sprite
/// sub-assets additionally use a local file ID so one slice can be identified
/// without retaining a dangling Unity object reference.
/// </summary>
[Serializable]
public sealed class PrefabImageReplacerIgnoreEntry
{
    [SerializeField] private string guid = string.Empty;
    [SerializeField] private long localFileId;
    [SerializeField] private string lastKnownPath = string.Empty;
    [SerializeField] private string lastKnownName = string.Empty;

    public string Guid => guid ?? string.Empty;
    public long LocalFileId => localFileId;
    public string LastKnownPath => lastKnownPath ?? string.Empty;
    public string LastKnownName => lastKnownName ?? string.Empty;

    public PrefabImageReplacerIgnoreEntry()
    {
    }

    public PrefabImageReplacerIgnoreEntry(string guid, string lastKnownPath)
    {
        this.guid = NormalizeGuid(guid);
        localFileId = 0;
        this.lastKnownPath = NormalizePath(lastKnownPath);
        lastKnownName = string.Empty;
    }

    public PrefabImageReplacerIgnoreEntry(
        string guid,
        long localFileId,
        string lastKnownPath,
        string lastKnownName)
    {
        this.guid = NormalizeGuid(guid);
        this.localFileId = localFileId;
        this.lastKnownPath = NormalizePath(lastKnownPath);
        this.lastKnownName = NormalizeName(lastKnownName);
    }

    /// <summary>
    /// Creates an entry from an AssetDatabase path.  The path must resolve to
    /// an asset or a folder with a GUID.
    /// </summary>
    public PrefabImageReplacerIgnoreEntry(string assetPath)
    {
        InitializeFromAssetPath(assetPath);
    }

    public static PrefabImageReplacerIgnoreEntry Create(string assetPath)
    {
        return new PrefabImageReplacerIgnoreEntry(assetPath);
    }

    public static PrefabImageReplacerIgnoreEntry Create(Sprite sprite)
    {
        if (sprite == null)
            throw new ArgumentNullException(nameof(sprite));

        string assetGuid;
        long assetLocalFileId;
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out assetGuid, out assetLocalFileId))
        {
            throw new ArgumentException(
                "The Sprite does not resolve to a GUID and local file ID: " + sprite.name,
                nameof(sprite));
        }

        return new PrefabImageReplacerIgnoreEntry(
            assetGuid,
            assetLocalFileId,
            AssetDatabase.GetAssetPath(sprite),
            sprite.name);
    }

    /// <summary>
    /// Refreshes only the display path while preserving the persisted GUID.
    /// This is intentionally an explicit operation; matching should always
    /// resolve the current path from the GUID rather than trust this value.
    /// </summary>
    internal void UpdateLastKnownPath(string assetPath)
    {
        lastKnownPath = NormalizePath(assetPath);
    }

    // Kept as a small compatibility alias for the editor window's asset
    // refresh path.  The GUID remains unchanged when an asset is moved.
    internal void SetAssetPath(string assetPath)
    {
        UpdateLastKnownPath(assetPath);
    }

    internal void SetValues(string entryGuid, string assetPath)
    {
        guid = NormalizeGuid(entryGuid);
        localFileId = 0;
        lastKnownPath = NormalizePath(assetPath);
        lastKnownName = string.Empty;
    }

    internal void SetValues(
        string entryGuid,
        long entryLocalFileId,
        string assetPath,
        string assetName)
    {
        guid = NormalizeGuid(entryGuid);
        localFileId = entryLocalFileId;
        lastKnownPath = NormalizePath(assetPath);
        lastKnownName = NormalizeName(assetName);
    }

    internal Sprite ResolveSprite(out string currentPath)
    {
        currentPath = string.IsNullOrEmpty(Guid)
            ? string.Empty
            : AssetDatabase.GUIDToAssetPath(Guid);
        if (string.IsNullOrEmpty(currentPath) || localFileId == 0)
            return null;

        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(currentPath))
        {
            var sprite = asset as Sprite;
            if (sprite == null)
                continue;

            string spriteGuid;
            long spriteLocalFileId;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out spriteGuid, out spriteLocalFileId)
                && string.Equals(spriteGuid, Guid, StringComparison.OrdinalIgnoreCase)
                && spriteLocalFileId == localFileId)
            {
                return sprite;
            }
        }

        return null;
    }

    private void InitializeFromAssetPath(string assetPath)
    {
        var normalizedPath = NormalizePath(assetPath);
        if (string.IsNullOrEmpty(normalizedPath))
            throw new ArgumentException("Asset path cannot be empty.", nameof(assetPath));

        var assetGuid = AssetDatabase.AssetPathToGUID(normalizedPath);
        if (string.IsNullOrEmpty(assetGuid))
            throw new ArgumentException(
                "The asset path does not resolve to a GUID: " + normalizedPath,
                nameof(assetPath));

        guid = NormalizeGuid(assetGuid);
        localFileId = 0;
        lastKnownPath = normalizedPath;
        lastKnownName = string.Empty;
    }

    private static string NormalizeGuid(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : value.Trim();
    }

    private static string NormalizePath(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace('\\', '/').Trim();
    }

    private static string NormalizeName(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : value.Trim();
    }
}

/// <summary>
/// Project-level configuration for PrefabImageReplacerWindow.
/// </summary>
public sealed class PrefabImageReplacerIgnoreListConfig : ScriptableObject
{
    public const string AssetPath = "Assets/_MyTest_SLG/Editor/PrefabImageReplacer/PrefabImageReplacerIgnoreListConfig.asset";

    [SerializeField, InspectorName("忽略 Prefab")]
    private List<PrefabImageReplacerIgnoreEntry> ignoredPrefabs = new List<PrefabImageReplacerIgnoreEntry>();

    [SerializeField, InspectorName("忽略 Prefab 文件夹")]
    private List<PrefabImageReplacerIgnoreEntry> ignoredFolders = new List<PrefabImageReplacerIgnoreEntry>();

    [SerializeField, InspectorName("忽略 Sprite")]
    private List<PrefabImageReplacerIgnoreEntry> ignoredSprites = new List<PrefabImageReplacerIgnoreEntry>();

    [SerializeField, InspectorName("忽略 Sprite 文件夹")]
    private List<PrefabImageReplacerIgnoreEntry> ignoredSpriteFolders = new List<PrefabImageReplacerIgnoreEntry>();

    /// <summary>
    /// The serialized lists are exposed as IList so callers can add/remove
    /// entries without replacing the backing collections.
    /// </summary>
    public IList<PrefabImageReplacerIgnoreEntry> IgnoredPrefabs
    {
        get
        {
            EnsureLists();
            return ignoredPrefabs;
        }
    }

    public IList<PrefabImageReplacerIgnoreEntry> IgnoredFolders
    {
        get
        {
            EnsureLists();
            return ignoredFolders;
        }
    }

    public IList<PrefabImageReplacerIgnoreEntry> IgnoredSprites
    {
        get
        {
            EnsureLists();
            return ignoredSprites;
        }
    }

    public IList<PrefabImageReplacerIgnoreEntry> IgnoredSpriteFolders
    {
        get
        {
            EnsureLists();
            return ignoredSpriteFolders;
        }
    }

    private void OnEnable()
    {
        EnsureLists();
    }

    /// <summary>
    /// Loads the fixed project asset, creating it when absent.  An existing
    /// asset at the path with another type is an explicit configuration error.
    /// </summary>
    public static PrefabImageReplacerIgnoreListConfig LoadOrCreate()
    {
        var existingConfig = LoadExisting(AssetPath);
        if (existingConfig != null)
            return existingConfig;

        if (AssetDatabase.IsValidFolder(AssetPath))
        {
            throw new InvalidOperationException(
                "Cannot create Prefab image replacer ignore list: asset path '" +
                AssetPath + "' is an existing folder.");
        }

        return Create(AssetPath);
    }

    /// <summary>
    /// Creates (or loads) a configuration asset at <paramref name="assetPath"/>.
    /// The normal caller should use <see cref="LoadOrCreate"/> so the project
    /// uses the canonical path.
    /// </summary>
    public static PrefabImageReplacerIgnoreListConfig Create(string assetPath)
    {
        var normalizedPath = NormalizeAssetPath(assetPath);
        if (string.IsNullOrEmpty(normalizedPath))
            throw new ArgumentException("Asset path cannot be empty.", nameof(assetPath));

        if (!normalizedPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Asset path must be under the Unity project's Assets folder: " + normalizedPath,
                nameof(assetPath));
        }

        var existingConfig = LoadExisting(normalizedPath);
        if (existingConfig != null)
            return existingConfig;

        if (AssetDatabase.IsValidFolder(normalizedPath))
        {
            throw new InvalidOperationException(
                "Cannot create Prefab image replacer ignore list: asset path '" +
                normalizedPath + "' is an existing folder.");
        }

        var directory = Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
        {
            var absoluteDirectory = Path.Combine(Directory.GetCurrentDirectory(), directory);
            Directory.CreateDirectory(absoluteDirectory);
            AssetDatabase.Refresh();
        }

        var config = CreateInstance<PrefabImageReplacerIgnoreListConfig>();
        try
        {
            AssetDatabase.CreateAsset(config, normalizedPath);
            AssetDatabase.SaveAssets();
        }
        catch
        {
            if (config != null)
                DestroyImmediate(config);
            throw;
        }

        config.EnsureLists();
        return config;
    }

    private static PrefabImageReplacerIgnoreListConfig LoadExisting(string assetPath)
    {
        var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (mainAsset != null)
        {
            var existingConfig = mainAsset as PrefabImageReplacerIgnoreListConfig;
            if (existingConfig == null)
            {
                throw new InvalidOperationException(
                    "Cannot load Prefab image replacer ignore list: asset path '" +
                    assetPath + "' is already occupied by " +
                    mainAsset.GetType().Name + ".");
            }

            existingConfig.EnsureLists();
            return existingConfig;
        }

        // GetMainAssetTypeAtPath also catches an imported asset whose object
        // cannot currently be loaded (for example, while its script is broken).
        var existingType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
        if (existingType == typeof(PrefabImageReplacerIgnoreListConfig))
        {
            throw new InvalidOperationException(
                "Cannot load Prefab image replacer ignore list: asset path '" +
                assetPath + "' contains a configuration asset that could not be loaded.");
        }

        if (existingType != null)
        {
            throw new InvalidOperationException(
                "Cannot load Prefab image replacer ignore list: asset path '" +
                assetPath + "' is already occupied by " + existingType.Name + ".");
        }

        // The asset may be present on disk while Unity is still importing it.
        // Do not attempt CreateAsset in that window, since Unity reports a
        // non-throwing import error and would otherwise hide the real cause.
        var absolutePath = Path.Combine(
            Directory.GetCurrentDirectory(),
            assetPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(absolutePath))
        {
            throw new InvalidOperationException(
                "Cannot load Prefab image replacer ignore list: asset file exists but is not loaded at '" +
                assetPath + "'.");
        }

        return null;
    }

    private void EnsureLists()
    {
        if (ignoredPrefabs == null)
            ignoredPrefabs = new List<PrefabImageReplacerIgnoreEntry>();
        if (ignoredFolders == null)
            ignoredFolders = new List<PrefabImageReplacerIgnoreEntry>();
        if (ignoredSprites == null)
            ignoredSprites = new List<PrefabImageReplacerIgnoreEntry>();
        if (ignoredSpriteFolders == null)
            ignoredSpriteFolders = new List<PrefabImageReplacerIgnoreEntry>();
    }

    private static string NormalizeAssetPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return string.Empty;

        return assetPath.Replace('\\', '/').Trim();
    }
}
