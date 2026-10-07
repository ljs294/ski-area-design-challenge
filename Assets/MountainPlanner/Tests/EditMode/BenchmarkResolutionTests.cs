using System;
using MountainPlanner.App;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>-benchres &lt;w&gt;x&lt;h&gt;: the benchmark's one-run window, read from the command line.</summary>
    public sealed class BenchmarkResolutionTests
    {
        [Test]
        public void ReadsTheSizeAfterTheArgument()
        {
            Assert.IsTrue(BenchmarkResolution.TryParse(new[] { "game.exe", "-quality", "high", "-benchres", "1920x1080", "-benchmark", "a.json" }, out var size));
            Assert.AreEqual(new Vector2Int(1920, 1080), size);
            Assert.IsTrue(BenchmarkResolution.TryParse(new[] { "-benchres", "1600X900" }, out size));
            Assert.AreEqual(new Vector2Int(1600, 900), size);
        }

        [TestCase("")]
        [TestCase("-benchres")]
        [TestCase("-benchres 1920")]
        [TestCase("-benchres 1920x")]
        [TestCase("-benchres -1920x1080")]
        [TestCase("-benchres 1920x1080x2")]
        [TestCase("-benchres 32x32")]
        [TestCase("-benchres -benchmark")]
        public void IgnoresAMissingOrMalformedSize(string commandLine)
        {
            Assert.IsFalse(BenchmarkResolution.TryParse(commandLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), out _));
        }
    }
}
