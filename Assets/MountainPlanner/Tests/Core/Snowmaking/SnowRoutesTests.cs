using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using NUnit.Framework;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>Task 4.15: an inline pump's default direction points away from the water (plan: MapLibre's commonest error never arises).</summary>
    public sealed class SnowRoutesTests
    {
        const ulong Pond = 0x50;

        [Test]
        public void APumpPushesAwayFromTheHouseWhicheverWayTheLineWasDrawn()
        {
            var n = new SnowNet().Water(Pond, 0);
            var house = n.House(0, 0, Pond, PumpSpec.House);
            var top = n.Node(SnowNodeKind.Hydrant, 2000, 0);
            var up = n.Pipe(house, top, 8);                       // drawn from the house
            var far = n.Node(SnowNodeKind.Hydrant, 2000, 1000);
            var back = n.Pipe(far, top, 8);                       // drawn towards the main
            var net = n.Network();
            Assert.That(SnowRoutes.AwayFromSource(net, up.Id, 1000 * 304.8), Is.True, "drawn from the house: along the route");
            Assert.That(SnowRoutes.AwayFromSource(net, back.Id, 500 * 304.8), Is.False, "drawn towards it: against the route");
        }

        [Test]
        public void TheNearerOfTwoHousesDecides()
        {
            var n = new SnowNet().Water(Pond, 0).Water(Pond + 1, 0);
            var a = n.House(0, 0, Pond, PumpSpec.House);
            var b = n.House(3000, 0, Pond + 1, PumpSpec.House);
            var line = n.Pipe(a, b, 8);
            var net = n.Network();
            Assert.That(SnowRoutes.AwayFromSource(net, line.Id, 500 * 304.8), Is.True, "near A: away from A");
            Assert.That(SnowRoutes.AwayFromSource(net, line.Id, 2500 * 304.8), Is.False, "near B: away from B");
        }

        [Test]
        public void NoWaterPushesAlongTheRoute()
        {
            var n = new SnowNet();
            var j1 = n.Node(SnowNodeKind.Junction, 0, 0);
            var j2 = n.Node(SnowNodeKind.Junction, 1000, 0);
            var p = n.Pipe(j1, j2, 8);
            Assert.That(SnowRoutes.AwayFromSource(n.Network(), p.Id, 500 * 304.8), Is.True);
        }
    }
}
