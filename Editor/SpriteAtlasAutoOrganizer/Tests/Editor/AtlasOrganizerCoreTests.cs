using System.Collections.Generic;
using NUnit.Framework;
using SpriteAtlasAutoOrganizer.Editor;

namespace SpriteAtlasAutoOrganizer.Editor.Tests
{
    public sealed class AtlasNamerTests
    {
        [Test]
        public void BuildStableName_IsDeterministicForSameSprites()
        {
            var sprites = new[]
            {
                new SpriteKey("bbb", 1),
                new SpriteKey("aaa", 2)
            };

            string first = AtlasNamer.BuildStableName("Package4", sprites);
            string second = AtlasNamer.BuildStableName("Package4", new[]
            {
                new SpriteKey("aaa", 2),
                new SpriteKey("bbb", 1)
            });

            Assert.That(first, Is.EqualTo(second));
            Assert.That(first.StartsWith("Atlas_Package4_"), Is.True);
        }

        [Test]
        public void ComputeContentHash_ChangesWhenSpriteSetChanges()
        {
            string before = AtlasNamer.ComputeContentHash(new[]
            {
                new SpriteKey("a", 1),
                new SpriteKey("b", 1)
            });
            string after = AtlasNamer.ComputeContentHash(new[]
            {
                new SpriteKey("a", 1),
                new SpriteKey("c", 1)
            });

            Assert.That(before, Is.Not.EqualTo(after));
        }

        [Test]
        public void SanitizeDomain_ReplacesInvalidPathChars()
        {
            Assert.That(AtlasNamer.SanitizeDomain("Pack age/1"), Is.EqualTo("Pack_age_1"));
            Assert.That(AtlasNamer.SanitizeDomain(""), Is.EqualTo("Default"));
        }
    }

    public sealed class AtlasDiffBuilderTests
    {
        [Test]
        public void Build_ReportsAddRemoveAndDeletedAtlas()
        {
            var planned = new List<AtlasCluster>
            {
                new AtlasCluster
                {
                    StableName = "Atlas_Hero",
                    Reason = "Cooccur:20"
                }
            };
            planned[0].Sprites.Add(new SpriteKey("A", 1));
            planned[0].Sprites.Add(new SpriteKey("D", 1));

            var existing = new Dictionary<string, HashSet<string>>
            {
                { "Atlas_Hero", new HashSet<string> { new SpriteKey("A", 1).Token, new SpriteKey("C", 1).Token } },
                { "Atlas_Old", new HashSet<string> { new SpriteKey("Z", 1).Token } }
            };

            List<AtlasDiffEntry> diffs = AtlasDiffBuilder.Build(planned, existing);

            AtlasDiffEntry hero = diffs.Find(item => item.AtlasName == "Atlas_Hero");
            AtlasDiffEntry old = diffs.Find(item => item.AtlasName == "Atlas_Old");
            Assert.That(hero, Is.Not.Null);
            Assert.That(hero.Added, Does.Contain(new SpriteKey("D", 1).Token));
            Assert.That(hero.Removed, Does.Contain(new SpriteKey("C", 1).Token));
            Assert.That(old.IsDeleted, Is.True);
        }
    }

    public sealed class SpriteYamlReferenceParserTests
    {
        [Test]
        public void Parse_CollectsDistinctSpritePtrs()
        {
            const string yaml =
                "m_Sprite: {fileID: 21300000, guid: 11111111111111111111111111111111, type: 3}\n" +
                "m_Sprite: {fileID: 21300000, guid: 11111111111111111111111111111111, type: 3}\n" +
                "m_Sprite: {fileID: 21300002, guid: 22222222222222222222222222222222, type: 3}\n" +
                "m_Sprite: {fileID: 0}\n";

            var lookup = new Dictionary<string, SpriteKey>();
            var first = new SpriteKey("11111111111111111111111111111111", 21300000);
            var second = new SpriteKey("22222222222222222222222222222222", 21300002);
            SpriteYamlReferenceParser.AddLookup(lookup, first);
            SpriteYamlReferenceParser.AddLookup(lookup, second);

            List<SpriteKey> keys = SpriteYamlReferenceParser.Parse(yaml, lookup);

            Assert.That(keys.Count, Is.EqualTo(2));
            Assert.That(keys, Does.Contain(first));
            Assert.That(keys, Does.Contain(second));
        }

        [Test]
        public void Parse_IgnoresUnknownGuids()
        {
            const string yaml =
                "m_Sprite: {fileID: 21300000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}";
            var lookup = new Dictionary<string, SpriteKey>();

            List<SpriteKey> keys = SpriteYamlReferenceParser.Parse(yaml, lookup);

            Assert.That(keys.Count, Is.EqualTo(0));
        }
    }

    public sealed class SpriteIdentityTests
    {
        [Test]
        public void MultiplePrefabsShareOneSpriteNode()
        {
            var sprites = new Dictionary<SpriteKey, SpriteRecord>();
            var key = new SpriteKey("shared", 21300000);
            sprites[key] = new SpriteRecord { Key = key, Name = "X" };

            var prefabToSprites = new Dictionary<string, HashSet<SpriteKey>>();
            Add(prefabToSprites, "A", key);
            Add(prefabToSprites, "B", key);
            Add(prefabToSprites, "C", key);

            Assert.That(sprites.Count, Is.EqualTo(1));
            Assert.That(prefabToSprites["A"].Contains(key), Is.True);
            Assert.That(prefabToSprites["B"].Contains(key), Is.True);
            Assert.That(prefabToSprites["C"].Contains(key), Is.True);
        }

        private static void Add(
            Dictionary<string, HashSet<SpriteKey>> map,
            string prefab,
            SpriteKey key)
        {
            HashSet<SpriteKey> set;
            if (!map.TryGetValue(prefab, out set))
            {
                set = new HashSet<SpriteKey>();
                map[prefab] = set;
            }

            set.Add(key);
        }
    }
}
