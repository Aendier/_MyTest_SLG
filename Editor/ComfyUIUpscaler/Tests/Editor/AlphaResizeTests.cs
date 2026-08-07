using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ComfyUIUpscaler.Editor.Tests
{
    public sealed class AlphaResizeTests
    {
        [Test]
        public void BilinearResize_ClampsTheOuterSampleCenters()
        {
            var source = new[]
            {
                new Color32(0, 0, 0, 0),
                new Color32(0, 0, 0, 255)
            };
            MethodInfo method = typeof(AtlasImagePipeline).GetMethod(
                "ResizeAlphaBilinear",
                BindingFlags.NonPublic | BindingFlags.Static);

            var result = (byte[])method.Invoke(null, new object[] { source, 1, 2, 1, 4 });

            Assert.That(result, Is.EqualTo(new byte[] { 0, 64, 191, 255 }));
        }
    }
}
