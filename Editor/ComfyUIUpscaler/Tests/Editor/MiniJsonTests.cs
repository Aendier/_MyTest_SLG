using System.Collections.Generic;
using NUnit.Framework;

namespace ComfyUIUpscaler.Editor.Tests
{
    public sealed class MiniJsonTests
    {
        [Test]
        public void ApiWorkflow_RoundTripsNestedInputs()
        {
            const string json = "{\"12\":{\"inputs\":{\"image\":\"old.png\",\"scale\":4},\"class_type\":\"LoadImage\"}}";
            var root = (Dictionary<string, object>)MiniJson.Deserialize(json);
            var node = (Dictionary<string, object>)root["12"];
            var inputs = (Dictionary<string, object>)node["inputs"];
            inputs["image"] = "new.png";

            string serialized = MiniJson.Serialize(root);
            var reparsed = (Dictionary<string, object>)MiniJson.Deserialize(serialized);
            var reparsedNode = (Dictionary<string, object>)reparsed["12"];
            var reparsedInputs = (Dictionary<string, object>)reparsedNode["inputs"];

            Assert.That(reparsedInputs["image"], Is.EqualTo("new.png"));
            Assert.That(reparsedInputs["scale"], Is.EqualTo(4L));
        }
    }
}
