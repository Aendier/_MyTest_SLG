using NUnit.Framework;

namespace ComfyUIUpscaler.Editor.Tests
{
    public sealed class OutputScaleValidationTests
    {
        [Test]
        public void MatchesExpectedDimensions_AcceptsFractionalScaleWithPixelTruncation()
        {
            bool matches = AtlasImagePipeline.MatchesExpectedDimensions(
                101,
                99,
                252,
                247,
                2.5f,
                1);

            Assert.That(matches, Is.True);
        }

        [Test]
        public void MatchesExpectedDimensions_AcceptsReportedSpriteDimensions()
        {
            bool matches = AtlasImagePipeline.MatchesExpectedDimensions(
                99,
                125,
                142,
                180,
                1.44f,
                2);

            Assert.That(matches, Is.True);
        }

        [Test]
        public void MatchesExpectedDimensions_RejectsDifferentScale()
        {
            bool matches = AtlasImagePipeline.MatchesExpectedDimensions(
                2424,
                1112,
                12120,
                5560,
                2.5f,
                1);

            Assert.That(matches, Is.False);
        }
    }
}
