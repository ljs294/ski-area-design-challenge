using MountainPlanner.UI;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Names on the map hide behind the ground (owner, 2026-10-20: "labels are visible through terrain").
    public sealed class LabelSightTests
    {
        /// <summary>Flat ground at 1,000 m with a 300 m ridge running north-south at x = 0.</summary>
        sealed class Ridge : ITerrainSurface
        {
            public Rect Footprint => new Rect(-2000, -2000, 4000, 4000);
            public float HeightAt(float x, float z) => 1000 + Mathf.Max(0, 300 - Mathf.Abs(x) * 3);
        }

        [Test]
        public void ALabelBehindARidgeIsHiddenAndOneInSightIsNot()
        {
            var ground = new Ridge();
            var eye = new Vector3(-800, 1100, 0);
            Assert.IsTrue(LabelSight.Hidden(eye, new Vector3(800, 1002, 0), ground), "the far side of the ridge");
            Assert.IsFalse(LabelSight.Hidden(eye, new Vector3(-400, 1002, 300), ground), "this side, in plain sight");
            Assert.IsFalse(LabelSight.Hidden(new Vector3(-800, 2500, 0), new Vector3(800, 1002, 0), ground), "seen from high above the ridge");
            Assert.IsFalse(LabelSight.Hidden(eye, new Vector3(800, 1002, 0), null), "no ground: never hidden");
        }
    }
}
