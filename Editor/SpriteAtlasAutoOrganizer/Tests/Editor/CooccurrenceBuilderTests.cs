using System.Collections.Generic;
using NUnit.Framework;
using SpriteAtlasAutoOrganizer.Editor;

namespace SpriteAtlasAutoOrganizer.Editor.Tests
{
    public sealed class CooccurrenceBuilderTests
    {
        [Test]
        public void Build_UsesHostPairsInsteadOfFullSpriteMatrix()
        {
            var hosts = new List<HostRecord>
            {
                Host(false, Key("1"), Key("2"), Key("3")),
                Host(false, Key("1"), Key("2")),
                Host(false, Key("1"), Key("4"))
            };
            var eligible = new HashSet<SpriteKey>
            {
                Key("1"), Key("2"), Key("3"), Key("4")
            };

            Dictionary<string, float> scores = CooccurrenceBuilder.Build(
                hosts, 10f, 3f, 200, eligible);

            Assert.That(scores[Pair("1", "2")], Is.EqualTo(20f));
            Assert.That(scores[Pair("1", "3")], Is.EqualTo(10f));
            Assert.That(scores[Pair("2", "3")], Is.EqualTo(10f));
            Assert.That(scores[Pair("1", "4")], Is.EqualTo(10f));
            Assert.That(scores.ContainsKey(Pair("2", "4")), Is.False);
        }

        [Test]
        public void Build_SceneWeightIsLowerThanPrefab()
        {
            var hosts = new List<HostRecord>
            {
                Host(false, Key("A"), Key("B")),
                Host(true, Key("A"), Key("C"))
            };
            var eligible = new HashSet<SpriteKey> { Key("A"), Key("B"), Key("C") };

            Dictionary<string, float> scores = CooccurrenceBuilder.Build(
                hosts, 10f, 3f, 200, eligible);

            Assert.That(scores[Pair("A", "B")], Is.EqualTo(10f));
            Assert.That(scores[Pair("A", "C")], Is.EqualTo(3f));
        }

        [Test]
        public void Build_SkipsHostWhenSpriteCountExceedsLimit()
        {
            var host = Host(false, Key("1"), Key("2"), Key("3"));
            var eligible = new HashSet<SpriteKey> { Key("1"), Key("2"), Key("3") };

            Dictionary<string, float> scores = CooccurrenceBuilder.Build(
                new[] { host }, 10f, 3f, 2, eligible);

            Assert.That(scores.Count, Is.EqualTo(0));
        }

        [Test]
        public void Build_SameSpriteAppearsOncePerHost()
        {
            var host = Host(false, Key("1"), Key("1"), Key("2"));
            var eligible = new HashSet<SpriteKey> { Key("1"), Key("2") };

            Dictionary<string, float> scores = CooccurrenceBuilder.Build(
                new[] { host }, 10f, 3f, 200, eligible);

            Assert.That(scores[Pair("1", "2")], Is.EqualTo(10f));
        }

        private static HostRecord Host(bool isScene, params SpriteKey[] sprites)
        {
            var host = new HostRecord { IsScene = isScene };
            host.Sprites.AddRange(sprites);
            return host;
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
