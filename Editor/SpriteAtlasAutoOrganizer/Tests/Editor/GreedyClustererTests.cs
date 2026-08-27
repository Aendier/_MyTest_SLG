using System.Collections.Generic;
using NUnit.Framework;
using SpriteAtlasAutoOrganizer.Editor;

namespace SpriteAtlasAutoOrganizer.Editor.Tests
{
    public sealed class GreedyClustererTests
    {
        [Test]
        public void Cluster_MergesHighestCooccurrenceFirst()
        {
            var sprites = new[]
            {
                Sprite("A", "P1"),
                Sprite("B", "P1"),
                Sprite("C", "P1")
            };
            var scores = new Dictionary<string, float>
            {
                { Pair("A", "B"), 95f },
                { Pair("A", "C"), 80f },
                { Pair("B", "C"), 20f }
            };

            List<ClusterWork> clusters = Run(sprites, scores, false);

            Assert.That(clusters.Count, Is.EqualTo(1));
            Assert.That(clusters[0].Sprites.Count, Is.EqualTo(3));
        }

        [Test]
        public void Cluster_DoesNotMixPackagesByDefault()
        {
            var sprites = new[]
            {
                Sprite("A", "PackageA"),
                Sprite("B", "PackageB")
            };
            var scores = new Dictionary<string, float>
            {
                { Pair("A", "B"), 100f }
            };

            List<ClusterWork> clusters = Run(sprites, scores, false);

            Assert.That(clusters.Count, Is.EqualTo(2));
        }

        [Test]
        public void Cluster_CanMixPackagesWhenAllowed()
        {
            var sprites = new[]
            {
                Sprite("A", "PackageA"),
                Sprite("B", "PackageB")
            };
            var scores = new Dictionary<string, float>
            {
                { Pair("A", "B"), 100f }
            };

            List<ClusterWork> clusters = Run(sprites, scores, true);

            Assert.That(clusters.Count, Is.EqualTo(1));
        }

        [Test]
        public void Cluster_NeverShareStaysAlone()
        {
            var sprites = new[]
            {
                Sprite("Huge", "P1", true),
                Sprite("Icon", "P1")
            };
            var scores = new Dictionary<string, float>
            {
                { Pair("Huge", "Icon"), 100f }
            };

            List<ClusterWork> clusters = Run(sprites, scores, false);

            Assert.That(clusters.Count, Is.EqualTo(2));
        }

        [Test]
        public void Cluster_LockedGroupIsNotSplit()
        {
            var sprites = new[]
            {
                Sprite("A", "P1"),
                Sprite("B", "P1"),
                Sprite("C", "P1")
            };
            var locked = new[]
            {
                new LockedSpriteGroup
                {
                    groupName = "Hero_Common",
                    sprites = new[] { Key("A").Token, Key("B").Token }
                }
            };

            List<ClusterWork> clusters = GreedyClusterer.Cluster(
                sprites,
                new Dictionary<string, float>(),
                locked,
                token => SpriteKey.TryParse(token, out SpriteKey key) ? key : (SpriteKey?)null,
                new ClusterConstraints
                {
                    MaxSpriteCount = 500,
                    MaxEstimatedArea = 2048L * 2048L,
                    AllowCrossPackage = false
                });

            ClusterWork lockedCluster = clusters.Find(item => item.Sprites.Count == 2);
            Assert.That(lockedCluster, Is.Not.Null);
            Assert.That(lockedCluster.Sprites.Contains(Key("A")), Is.True);
            Assert.That(lockedCluster.Sprites.Contains(Key("B")), Is.True);
        }

        [Test]
        public void Cluster_RespectsMaxSpriteCount()
        {
            var sprites = new[]
            {
                Sprite("A", "P1"),
                Sprite("B", "P1"),
                Sprite("C", "P1")
            };
            var scores = new Dictionary<string, float>
            {
                { Pair("A", "B"), 10f },
                { Pair("A", "C"), 10f },
                { Pair("B", "C"), 10f }
            };

            List<ClusterWork> clusters = GreedyClusterer.Cluster(
                sprites,
                scores,
                null,
                null,
                new ClusterConstraints
                {
                    MaxSpriteCount = 2,
                    MaxEstimatedArea = 2048L * 2048L
                });

            Assert.That(clusters.Count, Is.EqualTo(2));
            Assert.That(clusters[0].Sprites.Count + clusters[1].Sprites.Count, Is.EqualTo(3));
        }

        [Test]
        public void Cluster_ManualAtlasSpritesAreIgnored()
        {
            SpriteRecord manual = Sprite("M", "P1");
            manual.InManualAtlas = true;
            var sprites = new[]
            {
                manual,
                Sprite("A", "P1")
            };

            List<ClusterWork> clusters = Run(sprites, new Dictionary<string, float>(), false);

            Assert.That(clusters.Count, Is.EqualTo(1));
            Assert.That(clusters[0].Sprites.Contains(Key("M")), Is.False);
        }

        private static List<ClusterWork> Run(
            IEnumerable<SpriteRecord> sprites,
            Dictionary<string, float> scores,
            bool allowCross)
        {
            var neverShare = new HashSet<SpriteKey>();
            foreach (SpriteRecord sprite in sprites)
            {
                if (sprite.NeverShare)
                    neverShare.Add(sprite.Key);
            }

            return GreedyClusterer.Cluster(
                sprites,
                scores,
                null,
                null,
                new ClusterConstraints
                {
                    MaxSpriteCount = 500,
                    MaxEstimatedArea = 2048L * 2048L,
                    AllowCrossPackage = allowCross,
                    NeverShareKeys = neverShare
                });
        }

        private static SpriteRecord Sprite(string guid, string domain, bool neverShare = false)
        {
            return new SpriteRecord
            {
                Key = Key(guid),
                Domain = domain,
                NeverShare = neverShare,
                EstimatedArea = 32
            };
        }

        private static SpriteKey Key(string guid)
        {
            return new SpriteKey(guid, 21300000);
        }

        private static string Pair(string left, string right)
        {
            return CooccurrenceBuilder.MakePairKey(Key(left), Key(right));
        }
    }
}
