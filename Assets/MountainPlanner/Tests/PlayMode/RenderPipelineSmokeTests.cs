using NUnit.Framework;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    public sealed class RenderPipelineSmokeTests
    {
        [Test]
        public void UniversalRenderPipelineIsActive()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;

            Assert.That(pipeline, Is.Not.Null, "No render pipeline asset is assigned.");
            Assert.That(pipeline.GetType().Name, Is.EqualTo("UniversalRenderPipelineAsset"));
        }
    }
}
