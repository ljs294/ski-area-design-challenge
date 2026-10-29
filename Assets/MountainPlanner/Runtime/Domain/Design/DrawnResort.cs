using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The drawn resort (Phase 3 task 11, DB1-DB4): six lifts and twenty trails on Jackson Hole 5 km, for the frame
    /// benchmark with a drawn resort shown and the edit timing with a whole design loaded (exit criteria 2 and 3).
    /// Only the lines are frozen here, in metres from the site's centre (TE5's local frame): the lift ends and each
    /// trail's centreline. Everything else comes from the engine on every build, through the design's own commands as
    /// the tools commit them: AddLift with LiftBuild's towers and levels (taking the review's level hints as a player
    /// would, LT34), and AddTrail with one painted stroke (TrailShape.FromStrokes) and the earthwork fold's ground (TL3).
    /// So the resort follows every rule change and save version, and the same mountain always gives the same design.
    /// The lines were planned once over the 5 km package's slopes (pods of about three runs off each lift's top, two
    /// cat tracks across pods) and frozen; nothing here is tuned to make a rule pass (fixes-must-be-general).
    /// </summary>
    public static class DrawnResort
    {
        /// <summary>A lift line: its type and its bottom and top bullwheels (local metres: x east, z north).</summary>
        public sealed class LiftLine
        {
            public readonly string Name, CatalogId;
            public readonly float BottomX, BottomZ, TopX, TopZ;
            public LiftLine(string name, string catalogId, float bottomX, float bottomZ, float topX, float topZ)
            {
                Name = name; CatalogId = catalogId; BottomX = bottomX; BottomZ = bottomZ; TopX = topX; TopZ = topZ;
            }
        }

        /// <summary>A run: graded or natural, its brush width, an optional island of trees (an erased circle at a centreline point) and its centreline, head first.</summary>
        public sealed class Run
        {
            public readonly string Name;
            public readonly bool Graded;
            public readonly int WidthFeet, IslandAt;
            public readonly float[] Centreline;
            public Run(string name, bool graded, int widthFeet, int islandAt, float[] centreline)
            {
                Name = name; Graded = graded; WidthFeet = widthFeet; IslandAt = islandAt; Centreline = centreline;
            }
            /// <summary>A cat track: the narrow graded traverses joining pods.</summary>
            public bool CatTrack => WidthFeet <= CatTrackFeet;
        }

        public const int CatTrackFeet = 40, IslandFeet = 50;

        /// <summary>Two of each buildable type (DB3), in the order they're built.</summary>
        public static readonly LiftLine[] Lifts =
        {
            new LiftLine("Base Six", LiftCatalogIds.SesselliftD6, -300.0f, -1700.0f, -1250.0f, -900.0f),
            new LiftLine("Valley Quad", LiftCatalogIds.SesselliftFgq4, 500.0f, -1000.0f, -250.0f, -250.0f),
            new LiftLine("East Monta", LiftCatalogIds.MontaFg4, 1250.0f, -250.0f, 500.0f, 500.0f),
            new LiftLine("Summit Six", LiftCatalogIds.SesselliftD6, -1300.0f, -800.0f, -1800.0f, 300.0f),
            new LiftLine("North Quad", LiftCatalogIds.SesselliftFgq4, 1750.0f, 250.0f, 800.0f, 1250.0f),
            new LiftLine("Ridge Monta", LiftCatalogIds.MontaFg4, -200.0f, -150.0f, -700.0f, 900.0f),
        };

        /// <summary>Ten graded and ten natural runs, of which the two cat tracks are graded (DB4); Big Meadow and Sunny Side keep an island.</summary>
        public static readonly Run[] Trails =
        {
            new Run("Lower Timbers", true, 120, -1, new[]
            {
                -1200.0f, -960.0f, -1193.3f, -984.0f, -1179.3f, -1004.7f, -1161.2f, -1021.8f, -1142.6f, -1038.6f, -1127.7f, -1058.5f,
                -1119.9f, -1082.2f, -1112.9f, -1106.1f, -1103.6f, -1129.4f, -1095.8f, -1153.1f, -1089.5f, -1177.3f, -1083.2f, -1201.5f,
                -1076.9f, -1225.7f, -1069.5f, -1249.5f, -1059.7f, -1272.5f, -1047.1f, -1294.1f, -1032.4f, -1314.3f, -1016.9f, -1333.9f,
                -1001.5f, -1353.6f, -986.8f, -1373.8f, -972.9f, -1394.6f, -959.9f, -1416.0f, -947.6f, -1437.7f, -935.4f, -1459.5f,
                -922.8f, -1481.1f, -908.8f, -1501.8f, -891.0f, -1519.2f, -868.8f, -1530.2f, -843.9f, -1531.9f, -819.0f, -1529.7f,
                -794.2f, -1527.0f, -769.4f, -1524.0f, -745.3f, -1517.3f, -722.8f, -1506.4f, -701.5f, -1493.4f, -680.2f, -1480.3f,
                -657.4f, -1470.3f, -632.7f, -1468.2f, -608.5f, -1474.2f, -585.8f, -1484.5f, -563.6f, -1496.1f, -541.5f, -1507.8f,
                -519.4f, -1519.5f, -497.2f, -1530.9f, -474.9f, -1542.3f, -452.6f, -1553.6f, -430.3f, -1564.8f, -407.9f, -1576.0f,
                -385.6f, -1587.2f, -363.2f, -1598.4f, -340.9f, -1609.6f, -318.5f, -1620.7f, -296.2f, -1631.9f, -273.8f, -1643.1f,
                -260.0f, -1650.0f,
            }),
            new Run("Wapiti Glades", false, 150, -1, new[]
            {
                -1300.0f, -950.0f, -1310.5f, -972.7f, -1316.9f, -996.8f, -1319.3f, -1021.7f, -1319.4f, -1046.7f, -1317.0f, -1071.5f,
                -1311.2f, -1095.8f, -1302.9f, -1119.4f, -1293.0f, -1142.3f, -1281.4f, -1164.5f, -1267.9f, -1185.5f, -1253.0f, -1205.5f,
                -1237.3f, -1225.0f, -1221.2f, -1244.2f, -1204.9f, -1263.1f, -1188.5f, -1282.0f, -1172.7f, -1301.3f, -1157.2f, -1321.0f,
                -1141.9f, -1340.7f, -1126.0f, -1360.0f, -1109.5f, -1378.8f, -1092.4f, -1397.0f, -1074.9f, -1414.9f, -1057.4f, -1432.8f,
                -1040.3f, -1451.0f, -1024.0f, -1469.9f, -1008.3f, -1489.4f, -1000.0f, -1500.0f, -978.3f, -1512.4f, -956.3f, -1524.3f,
                -933.9f, -1535.4f, -910.9f, -1545.1f, -887.1f, -1552.9f, -862.6f, -1557.6f, -837.7f, -1559.6f, -812.8f, -1561.5f,
                -788.0f, -1565.2f, -763.7f, -1570.7f, -739.5f, -1577.0f, -715.2f, -1583.0f, -690.6f, -1587.3f, -665.8f, -1590.2f,
                -641.2f, -1594.6f, -617.2f, -1601.6f, -593.8f, -1610.4f, -570.7f, -1620.0f, -547.8f, -1630.0f, -525.0f, -1640.2f,
                -502.2f, -1650.6f, -479.6f, -1661.2f, -457.1f, -1672.0f, -434.6f, -1683.0f, -412.2f, -1694.1f, -389.8f, -1705.2f,
                -367.4f, -1716.3f, -345.0f, -1727.5f, -340.0f, -1730.0f,
            }),
            new Run("Moose Meadow", false, 100, -1, new[]
            {
                -1180.0f, -900.0f, -1175.3f, -924.5f, -1174.0f, -949.5f, -1173.5f, -974.5f, -1161.0f, -993.9f, -1136.1f, -992.8f,
                -1111.7f, -994.6f, -1109.3f, -1018.3f, -1115.7f, -1042.4f, -1120.0f, -1067.0f, -1113.7f, -1090.8f, -1105.5f, -1114.3f,
                -1100.8f, -1138.8f, -1095.7f, -1163.3f, -1085.5f, -1186.0f, -1074.6f, -1208.5f, -1057.1f, -1224.9f, -1032.5f, -1222.3f,
                -1008.1f, -1216.8f, -983.3f, -1215.0f, -958.4f, -1217.5f, -933.8f, -1221.9f, -909.1f, -1225.8f, -884.2f, -1226.0f,
                -859.8f, -1220.7f, -837.2f, -1210.3f, -816.7f, -1196.0f, -798.0f, -1179.4f, -780.2f, -1161.9f, -762.3f, -1144.5f,
                -743.2f, -1128.3f, -722.7f, -1114.0f, -701.4f, -1100.9f, -690.2f, -1122.9f, -677.0f, -1144.1f, -663.1f, -1164.9f,
                -655.9f, -1188.5f, -639.3f, -1206.3f, -618.3f, -1219.7f, -599.4f, -1236.0f, -581.5f, -1253.5f, -563.8f, -1271.1f,
                -546.4f, -1289.1f, -528.2f, -1306.1f, -505.3f, -1315.8f, -481.0f, -1321.8f, -459.6f, -1333.2f, -450.5f, -1356.4f,
                -444.5f, -1380.6f, -438.4f, -1404.9f, -431.8f, -1429.0f, -425.1f, -1453.1f, -418.5f, -1477.2f, -411.5f, -1501.2f,
                -403.3f, -1524.8f, -392.8f, -1547.5f, -380.3f, -1569.1f, -365.6f, -1589.3f, -349.3f, -1608.2f, -331.5f, -1625.8f,
                -312.3f, -1641.8f, -291.9f, -1656.3f, -270.4f, -1669.0f, -250.0f, -1680.0f,
            }),
            new Run("Summit Chutes", false, 100, -1, new[]
            {
                -1760.0f, 250.0f, -1775.1f, 230.1f, -1789.1f, 209.4f, -1801.3f, 187.6f, -1812.3f, 165.1f, -1821.7f, 142.0f,
                -1829.2f, 118.1f, -1834.2f, 93.7f, -1835.6f, 68.7f, -1834.5f, 43.8f, -1832.1f, 18.9f, -1829.0f, -5.9f,
                -1824.0f, -30.4f, -1816.7f, -54.3f, -1807.2f, -77.4f, -1796.1f, -99.8f, -1783.5f, -121.4f, -1769.6f, -142.2f,
                -1754.3f, -161.9f, -1738.0f, -180.9f, -1720.6f, -198.8f, -1701.8f, -215.2f, -1681.3f, -229.6f, -1659.9f, -242.5f,
                -1638.5f, -255.4f, -1617.8f, -269.5f, -1598.2f, -285.0f, -1579.6f, -301.6f, -1561.5f, -318.9f, -1543.7f, -336.4f,
                -1526.0f, -354.1f, -1508.3f, -371.7f, -1490.6f, -389.4f, -1473.0f, -407.2f, -1455.8f, -425.3f, -1439.5f, -444.2f,
                -1424.8f, -464.5f, -1411.7f, -485.7f, -1400.2f, -507.9f, -1389.9f, -530.7f, -1381.5f, -554.2f, -1375.1f, -578.4f,
                -1371.6f, -603.1f, -1370.3f, -628.1f, -1370.0f, -653.1f, -1369.5f, -678.1f, -1366.7f, -702.9f, -1360.7f, -727.1f,
                -1353.1f, -750.9f, -1350.0f, -760.0f,
            }),
            new Run("Hidden Bowl", true, 100, -1, new[]
            {
                -1840.0f, 260.0f, -1846.2f, 235.8f, -1853.4f, 211.8f, -1860.5f, 187.9f, -1865.7f, 163.4f, -1867.9f, 138.6f,
                -1865.8f, 113.7f, -1860.5f, 89.3f, -1853.6f, 65.3f, -1846.1f, 41.4f, -1839.0f, 17.4f, -1832.3f, -6.6f,
                -1825.3f, -30.6f, -1817.1f, -54.3f, -1807.4f, -77.3f, -1796.3f, -99.7f, -1783.7f, -121.3f, -1770.1f, -142.2f,
                -1755.3f, -162.4f, -1739.7f, -181.9f, -1722.8f, -200.3f, -1704.8f, -217.7f, -1685.9f, -234.1f, -1666.6f, -250.0f,
                -1647.1f, -265.6f, -1627.3f, -280.8f, -1606.9f, -295.3f, -1600.0f, -300.0f, -1581.5f, -316.8f, -1560.5f, -330.2f,
                -1536.5f, -337.1f, -1511.7f, -340.4f, -1488.0f, -347.9f, -1471.2f, -365.9f, -1462.6f, -389.3f, -1457.6f, -413.8f,
                -1452.2f, -438.2f, -1445.3f, -462.2f, -1437.4f, -485.9f, -1428.8f, -509.4f, -1420.3f, -532.9f, -1411.6f, -556.4f,
                -1402.1f, -579.5f, -1392.0f, -602.4f, -1381.5f, -625.1f, -1371.5f, -648.0f, -1363.5f, -671.6f, -1360.2f, -696.4f,
                -1362.0f, -721.3f, -1366.6f, -745.8f, -1370.0f, -770.6f, -1369.7f, -795.6f, -1358.6f, -816.9f, -1336.1f, -827.6f,
                -1330.0f, -830.0f,
            }),
            new Run("Ridge Line", false, 80, -1, new[]
            {
                -1720.0f, 320.0f, -1739.5f, 304.3f, -1758.6f, 288.2f, -1777.1f, 271.5f, -1795.2f, 254.2f, -1812.9f, 236.5f,
                -1829.9f, 218.2f, -1845.2f, 198.5f, -1857.4f, 176.7f, -1865.2f, 153.0f, -1866.9f, 128.1f, -1863.7f, 103.4f,
                -1857.7f, 79.1f, -1850.4f, 55.2f, -1843.0f, 31.3f, -1836.1f, 7.3f, -1829.5f, -16.8f, -1822.0f, -40.6f,
                -1813.1f, -64.0f, -1802.4f, -86.6f, -1789.4f, -107.9f, -1773.1f, -126.7f, -1753.0f, -141.5f, -1729.8f, -150.6f,
                -1705.2f, -154.7f, -1680.2f, -153.5f, -1655.8f, -148.4f, -1631.6f, -141.9f, -1607.3f, -136.0f, -1582.7f, -132.1f,
                -1557.7f, -130.9f, -1532.8f, -132.7f, -1508.5f, -138.5f, -1485.2f, -147.3f, -1462.4f, -157.6f, -1439.6f, -168.0f,
                -1416.6f, -177.7f, -1393.1f, -186.2f, -1369.0f, -192.9f, -1344.4f, -197.1f, -1319.4f, -198.7f, -1294.4f, -199.0f,
                -1269.5f, -199.4f, -1250.0f, -200.0f, -1261.0f, -222.4f, -1269.8f, -245.8f, -1272.0f, -270.6f, -1265.0f, -294.4f,
                -1251.4f, -315.2f, -1235.4f, -334.4f, -1219.2f, -353.5f, -1203.8f, -373.2f, -1189.0f, -393.4f, -1175.0f, -414.0f,
                -1161.4f, -435.0f, -1148.3f, -456.3f, -1135.7f, -477.9f, -1123.6f, -499.8f, -1112.5f, -522.2f, -1103.1f, -545.3f,
                -1096.9f, -569.5f, -1096.6f, -594.4f, -1102.7f, -618.5f, -1113.1f, -641.2f, -1125.6f, -662.9f, -1139.4f, -683.7f,
                -1154.8f, -703.4f, -1172.3f, -721.2f, -1192.2f, -736.3f, -1214.3f, -747.8f, -1238.1f, -755.4f, -1260.0f, -760.0f,
            }),
            new Run("Village Way", true, 160, -1, new[]
            {
                -200.0f, -280.0f, -182.6f, -262.1f, -166.0f, -243.4f, -151.1f, -223.3f, -138.8f, -201.6f, -128.1f, -179.0f,
                -117.2f, -156.5f, -106.1f, -134.1f, -95.7f, -111.4f, -87.4f, -87.8f, -81.3f, -63.6f, -74.0f, -39.7f,
                -62.3f, -17.6f, -46.7f, 1.8f, -28.7f, 19.1f, -9.9f, 35.6f, 8.4f, 52.6f, 25.5f, 70.9f,
                40.8f, 90.6f, 54.4f, 111.6f, 66.4f, 133.5f, 78.5f, 155.4f, 93.4f, 175.4f, 113.7f, 189.4f,
                138.3f, 192.0f, 162.0f, 184.4f, 183.5f, 171.8f, 203.4f, 156.6f, 224.2f, 142.9f, 248.6f, 139.8f,
                272.3f, 147.5f, 293.6f, 160.4f, 314.0f, 174.9f, 336.3f, 185.7f, 361.2f, 184.2f, 386.2f, 183.7f,
                410.1f, 177.1f, 428.6f, 160.6f, 439.6f, 138.4f, 443.6f, 113.8f, 442.0f, 88.9f, 435.9f, 64.7f,
                426.9f, 41.4f, 418.8f, 17.8f, 411.8f, -6.2f, 408.5f, -30.9f, 408.6f, -55.7f, 402.6f, -80.0f,
                399.4f, -104.6f, 409.7f, -127.0f, 429.8f, -140.7f, 453.5f, -148.4f, 471.2f, -165.7f, 483.7f, -187.3f,
                496.7f, -208.6f, 511.7f, -228.6f, 525.5f, -249.4f, 530.4f, -273.4f, 522.0f, -296.8f, 510.9f, -319.2f,
                504.4f, -343.2f, 506.7f, -367.9f, 519.0f, -389.2f, 539.1f, -403.9f, 560.8f, -416.2f, 573.3f, -437.0f,
                573.2f, -461.8f, 568.3f, -486.3f, 561.0f, -510.2f, 551.9f, -533.5f, 541.8f, -556.3f, 531.2f, -579.0f,
                520.9f, -601.8f, 511.1f, -624.8f, 502.0f, -648.1f, 494.0f, -671.7f, 487.3f, -695.8f, 482.9f, -720.4f,
                481.5f, -745.3f, 483.0f, -770.3f, 485.9f, -795.1f, 488.3f, -820.0f, 489.4f, -845.0f, 488.4f, -869.9f,
                483.9f, -894.5f, 476.4f, -918.3f, 467.5f, -941.7f, 460.0f, -960.0f,
            }),
            new Run("Aspen Run", false, 120, -1, new[]
            {
                -300.0f, -300.0f, -277.6f, -310.9f, -260.3f, -328.7f, -249.6f, -351.2f, -243.2f, -375.4f, -239.1f, -400.0f,
                -238.2f, -425.0f, -243.0f, -449.4f, -253.5f, -472.1f, -261.1f, -495.6f, -251.4f, -518.2f, -237.4f, -538.9f,
                -221.2f, -557.9f, -200.4f, -571.7f, -177.7f, -582.0f, -154.5f, -591.3f, -131.4f, -601.0f, -111.3f, -615.5f,
                -98.3f, -636.7f, -86.9f, -658.9f, -71.6f, -678.6f, -53.3f, -695.7f, -34.3f, -711.8f, -15.2f, -728.0f,
                3.3f, -744.8f, 21.3f, -762.2f, 38.8f, -780.1f, 55.8f, -798.3f, 72.3f, -817.1f, 86.8f, -837.4f,
                96.0f, -860.6f, 99.2f, -885.3f, 100.0f, -900.0f, 123.4f, -907.7f, 148.4f, -907.1f, 173.4f, -907.6f,
                197.9f, -912.2f, 221.7f, -920.0f, 244.8f, -929.5f, 267.6f, -939.8f, 290.1f, -950.5f, 312.6f, -961.5f,
                335.0f, -972.6f, 357.4f, -983.7f, 379.8f, -994.9f, 402.1f, -1006.1f, 424.5f, -1017.2f, 446.8f, -1028.4f,
            }),
            new Run("Crossover", true, 100, -1, new[]
            {
                -280.0f, -300.0f, -300.7f, -314.0f, -320.7f, -329.0f, -339.1f, -345.8f, -355.5f, -364.8f, -368.5f, -386.1f,
                -378.2f, -409.1f, -385.5f, -433.0f, -390.0f, -457.5f, -390.9f, -482.5f, -388.4f, -507.3f, -385.2f, -532.1f,
                -384.8f, -557.1f, -389.1f, -581.6f, -397.0f, -605.4f, -407.9f, -627.8f, -421.7f, -648.6f, -439.3f, -666.3f,
                -459.3f, -681.3f, -480.0f, -695.4f, -497.1f, -713.3f, -501.9f, -737.6f, -498.2f, -762.3f, -492.2f, -786.6f,
                -487.2f, -811.0f, -485.6f, -835.9f, -488.9f, -860.7f, -495.3f, -884.8f, -500.0f, -900.0f, -477.9f, -911.5f,
                -461.6f, -930.2f, -453.5f, -953.7f, -450.8f, -978.6f, -450.2f, -1003.6f, -450.5f, -1028.5f, -452.9f, -1053.4f,
                -457.7f, -1078.0f, -463.0f, -1102.4f, -466.9f, -1127.1f, -467.9f, -1152.0f, -465.7f, -1176.9f, -465.0f, -1201.9f,
                -467.5f, -1226.7f, -471.0f, -1251.5f, -472.5f, -1276.4f, -466.9f, -1300.7f, -457.8f, -1323.9f, -452.0f, -1348.2f,
                -449.5f, -1373.1f, -445.5f, -1397.7f, -438.7f, -1421.7f, -429.6f, -1445.1f, -419.7f, -1468.0f, -409.3f, -1490.7f,
                -398.8f, -1513.4f, -388.7f, -1536.3f, -378.9f, -1559.3f, -369.3f, -1582.4f, -359.6f, -1605.4f, -349.6f, -1628.3f,
                -340.0f, -1650.0f,
            }),
            new Run("Couloir", false, 150, -1, new[]
            {
                -720.0f, 860.0f, -710.4f, 836.9f, -700.7f, 813.9f, -690.9f, 790.9f, -681.4f, 767.7f, -672.0f, 744.6f,
                -662.4f, 721.5f, -652.6f, 698.5f, -642.6f, 675.6f, -632.6f, 652.7f, -622.7f, 629.7f, -612.9f, 606.7f,
                -602.9f, 583.8f, -592.2f, 561.2f, -580.5f, 539.1f, -567.5f, 517.8f, -553.0f, 497.4f, -537.7f, 477.7f,
                -521.8f, 458.4f, -505.6f, 439.3f, -489.2f, 420.5f, -472.8f, 401.6f, -456.6f, 382.5f, -440.6f, 363.4f,
                -423.8f, 344.8f, -406.0f, 327.2f, -387.7f, 310.3f, -369.4f, 293.3f, -351.8f, 275.5f, -335.6f, 256.4f,
                -320.6f, 236.4f, -307.0f, 215.4f, -294.3f, 193.9f, -282.3f, 172.0f, -270.8f, 149.8f, -259.9f, 127.3f,
                -249.9f, 104.4f, -241.1f, 81.0f, -234.8f, 56.8f, -230.9f, 32.1f, -227.7f, 7.4f, -223.9f, -17.3f,
                -221.6f, -42.2f, -222.5f, -67.2f, -226.3f, -91.9f, -230.0f, -110.0f,
            }),
            new Run("Elk Ridge", true, 120, -1, new[]
            {
                -650.0f, 880.0f, -655.5f, 855.6f, -659.6f, 831.0f, -661.6f, 806.1f, -660.5f, 781.1f, -655.9f, 756.6f,
                -647.7f, 733.0f, -636.6f, 710.6f, -624.3f, 688.8f, -612.6f, 666.8f, -602.0f, 644.1f, -591.9f, 621.3f,
                -580.1f, 599.2f, -566.2f, 578.5f, -551.7f, 558.1f, -537.4f, 537.6f, -523.2f, 517.0f, -508.8f, 496.6f,
                -494.2f, 476.2f, -479.9f, 455.8f, -466.0f, 435.0f, -452.5f, 413.9f, -439.1f, 392.8f, -425.0f, 372.2f,
                -409.6f, 352.5f, -392.4f, 334.4f, -373.4f, 318.1f, -353.7f, 302.8f, -329.3f, 286.0f, -309.5f, 270.8f,
                -291.3f, 253.7f, -275.0f, 234.7f, -260.4f, 214.4f, -247.0f, 193.3f, -234.3f, 171.8f, -222.2f, 149.9f,
                -211.0f, 127.6f, -201.2f, 104.6f, -193.5f, 80.8f, -189.0f, 56.2f, -186.6f, 31.4f, -183.7f, 6.5f,
                -179.1f, -18.0f, -173.8f, -42.5f, -169.4f, -67.1f, -167.6f, -92.0f, -168.8f, -116.9f, -170.0f, -130.0f,
            }),
            new Run("Big Meadow", false, 200, 29, new[]
            {
                -760.0f, 860.0f, -778.8f, 843.6f, -798.2f, 827.8f, -818.9f, 813.7f, -841.0f, 802.2f, -864.1f, 792.6f,
                -887.0f, 782.5f, -907.4f, 768.2f, -920.7f, 747.3f, -921.4f, 722.8f, -904.8f, 705.1f, -883.8f, 691.6f,
                -865.3f, 674.8f, -849.1f, 655.8f, -835.0f, 635.2f, -822.8f, 613.4f, -812.5f, 590.6f, -803.8f, 567.2f,
                -795.1f, 543.8f, -785.1f, 520.8f, -773.9f, 498.5f, -762.0f, 476.5f, -749.6f, 454.8f, -736.9f, 433.3f,
                -724.0f, 411.8f, -711.1f, 390.4f, -697.9f, 369.2f, -684.6f, 348.0f, -671.2f, 326.9f, -658.1f, 305.7f,
                -645.3f, 284.1f, -633.0f, 262.4f, -621.1f, 240.4f, -609.5f, 218.3f, -600.0f, 200.0f, -588.8f, 177.6f,
                -577.6f, 155.3f, -566.4f, 133.0f, -555.0f, 110.7f, -543.5f, 88.5f, -531.9f, 66.3f, -520.3f, 44.2f,
                -509.0f, 21.9f, -498.2f, -0.6f, -487.7f, -23.3f, -477.5f, -46.1f, -466.6f, -68.7f, -454.3f, -90.4f,
                -439.1f, -110.2f, -420.8f, -127.2f, -400.3f, -141.4f, -379.5f, -155.3f, -358.3f, -168.5f, -334.1f, -174.0f,
                -309.1f, -174.0f, -284.1f, -174.5f, -259.2f, -177.2f, -240.0f, -180.0f,
            }),
            new Run("Easy Street", true, 140, -1, new[]
            {
                540.0f, 470.0f, 554.5f, 449.7f, 564.0f, 426.7f, 565.9f, 401.8f, 563.2f, 377.0f, 559.9f, 352.2f,
                558.8f, 327.3f, 563.3f, 302.7f, 569.0f, 278.4f, 575.6f, 254.3f, 584.1f, 230.8f, 595.1f, 208.4f,
                607.6f, 186.7f, 619.4f, 164.7f, 631.2f, 142.6f, 642.7f, 120.4f, 653.0f, 97.7f, 660.7f, 73.9f,
                668.8f, 50.3f, 681.1f, 28.5f, 692.3f, 6.2f, 706.5f, -14.3f, 724.8f, -31.4f, 741.1f, -50.2f,
                755.1f, -70.9f, 772.0f, -89.3f, 790.8f, -105.6f, 811.1f, -120.3f, 831.7f, -134.4f, 852.1f, -148.8f,
                871.8f, -164.3f, 890.2f, -181.1f, 907.5f, -199.2f, 925.3f, -216.7f, 947.5f, -227.7f, 972.3f, -230.3f,
                997.3f, -231.8f, 1022.3f, -232.2f, 1047.1f, -230.1f, 1071.7f, -225.2f, 1095.9f, -219.0f, 1120.3f, -213.5f,
                1145.1f, -211.0f, 1170.0f, -212.3f, 1194.6f, -216.6f, 1210.0f, -220.0f,
            }),
            new Run("Lupine", false, 100, -1, new[]
            {
                460.0f, 460.0f, 477.3f, 442.0f, 496.9f, 426.6f, 518.2f, 413.4f, 540.1f, 401.4f, 562.2f, 389.8f,
                584.0f, 377.5f, 604.7f, 363.5f, 623.5f, 347.0f, 640.3f, 328.5f, 655.4f, 308.6f, 669.1f, 287.7f,
                681.7f, 266.1f, 693.5f, 244.1f, 705.0f, 221.9f, 716.4f, 199.7f, 727.8f, 177.4f, 739.2f, 155.2f,
                750.7f, 132.9f, 762.5f, 110.9f, 775.2f, 89.4f, 789.6f, 68.9f, 805.8f, 50.0f, 824.7f, 33.6f,
                845.5f, 19.9f, 868.2f, 9.5f, 892.1f, 2.1f, 900.0f, 0.0f, 898.2f, -24.9f, 899.9f, -49.8f,
                906.0f, -74.1f, 913.1f, -98.0f, 917.9f, -122.5f, 921.6f, -147.2f, 931.9f, -169.8f, 949.8f, -187.1f,
                970.9f, -200.4f, 993.3f, -211.4f, 1016.6f, -220.7f, 1040.3f, -228.6f, 1064.5f, -234.7f, 1089.2f, -238.4f,
                1114.0f, -241.6f, 1138.4f, -247.0f, 1162.0f, -255.1f, 1185.2f, -264.5f, 1208.1f, -274.6f, 1220.0f, -280.0f,
            }),
            new Run("Steep and Deep", false, 60, -1, new[]
            {
                550.0f, 520.0f, 572.2f, 508.5f, 594.3f, 496.8f, 616.0f, 484.4f, 637.2f, 471.1f, 657.4f, 456.5f,
                676.7f, 440.6f, 694.9f, 423.4f, 712.2f, 405.4f, 728.8f, 386.7f, 745.4f, 368.0f, 762.1f, 349.4f,
                779.1f, 331.1f, 796.3f, 312.9f, 813.7f, 295.0f, 831.3f, 277.3f, 849.0f, 259.5f, 866.2f, 241.5f,
                882.8f, 222.8f, 898.8f, 203.5f, 914.5f, 184.1f, 930.3f, 164.7f, 946.7f, 145.8f, 963.6f, 127.4f,
                980.9f, 109.4f, 998.4f, 91.5f, 1015.8f, 73.5f, 1032.7f, 55.1f, 1049.2f, 36.4f, 1065.7f, 17.6f,
                1082.5f, -0.9f, 1099.7f, -19.1f, 1117.2f, -37.0f, 1134.8f, -54.7f, 1152.3f, -72.5f, 1169.5f, -90.6f,
                1186.1f, -109.4f, 1201.9f, -128.8f, 1217.4f, -148.4f, 1233.1f, -167.8f, 1249.3f, -186.8f, 1266.3f, -205.2f,
                1283.5f, -223.3f, 1290.0f, -230.0f,
            }),
            new Run("Northern Lights", true, 120, -1, new[]
            {
                840.0f, 1210.0f, 847.6f, 1186.3f, 863.3f, 1167.1f, 884.3f, 1153.8f, 907.5f, 1144.3f, 930.6f, 1134.8f,
                951.3f, 1121.0f, 967.1f, 1101.7f, 978.0f, 1079.3f, 989.6f, 1057.3f, 1008.7f, 1041.5f, 1031.7f, 1031.6f,
                1055.7f, 1025.0f, 1080.2f, 1019.8f, 1104.2f, 1012.7f, 1127.0f, 1002.6f, 1148.9f, 990.6f, 1170.3f, 977.6f,
                1190.6f, 963.1f, 1209.8f, 947.1f, 1227.7f, 929.6f, 1244.5f, 911.1f, 1261.0f, 892.4f, 1277.8f, 873.9f,
                1295.0f, 855.7f, 1312.8f, 838.2f, 1331.3f, 821.4f, 1350.5f, 805.3f, 1369.8f, 789.4f, 1388.3f, 772.6f,
                1405.7f, 754.7f, 1421.7f, 735.5f, 1436.7f, 715.5f, 1451.1f, 695.1f, 1465.4f, 674.5f, 1479.7f, 654.0f,
                1494.0f, 633.5f, 1507.3f, 612.4f, 1518.2f, 589.9f, 1526.6f, 566.4f, 1535.0f, 542.8f, 1544.8f, 519.8f,
                1556.3f, 497.6f, 1568.5f, 475.8f, 1580.9f, 454.1f, 1593.4f, 432.4f, 1606.1f, 410.9f, 1619.1f, 389.6f,
                1632.7f, 368.6f, 1646.8f, 347.9f, 1661.4f, 327.7f, 1676.8f, 308.0f, 1692.7f, 288.7f, 1700.0f, 280.0f,
            }),
            new Run("Tower Face", false, 100, -1, new[]
            {
                760.0f, 1200.0f, 777.7f, 1182.3f, 795.4f, 1164.6f, 813.0f, 1147.0f, 830.7f, 1129.3f, 848.4f, 1111.6f,
                865.8f, 1093.7f, 882.7f, 1075.2f, 898.3f, 1055.7f, 912.4f, 1035.1f, 925.4f, 1013.7f, 937.6f, 991.9f,
                949.4f, 969.9f, 961.1f, 947.8f, 972.8f, 925.7f, 984.8f, 903.8f, 997.3f, 882.1f, 1010.4f, 860.9f,
                1024.4f, 840.1f, 1039.1f, 819.9f, 1054.6f, 800.3f, 1070.9f, 781.3f, 1087.9f, 763.0f, 1105.3f, 745.0f,
                1122.8f, 727.2f, 1140.5f, 709.5f, 1158.2f, 691.8f, 1175.8f, 674.2f, 1193.5f, 656.4f, 1210.8f, 638.5f,
                1227.3f, 619.7f, 1242.5f, 599.8f, 1256.3f, 579.0f, 1269.0f, 557.5f, 1281.1f, 535.6f, 1292.9f, 513.5f,
                1300.0f, 500.0f, 1314.0f, 479.3f, 1329.2f, 459.5f, 1346.4f, 441.4f, 1365.6f, 425.4f, 1386.2f, 411.2f,
                1407.7f, 398.4f, 1429.6f, 386.3f, 1451.7f, 374.7f, 1473.9f, 363.2f, 1496.1f, 351.8f, 1518.3f, 340.1f,
                1540.1f, 328.0f, 1561.7f, 315.3f, 1582.8f, 302.0f, 1603.8f, 288.4f, 1624.8f, 274.9f, 1646.1f, 261.8f,
                1667.6f, 249.1f, 1689.4f, 236.8f, 1711.3f, 224.7f, 1720.0f, 220.0f,
            }),
            new Run("Sunny Side", true, 180, 42, new[]
            {
                860.0f, 1260.0f, 843.0f, 1241.7f, 828.2f, 1221.6f, 814.6f, 1200.6f, 802.0f, 1179.0f, 789.7f, 1157.2f,
                777.5f, 1135.4f, 766.0f, 1113.2f, 756.5f, 1090.1f, 750.5f, 1065.9f, 744.2f, 1041.7f, 734.0f, 1018.9f,
                723.0f, 996.5f, 715.2f, 972.8f, 706.0f, 949.6f, 708.8f, 925.5f, 727.6f, 909.2f, 729.9f, 885.8f,
                717.3f, 864.3f, 702.0f, 844.6f, 687.0f, 824.6f, 673.6f, 803.5f, 662.1f, 781.3f, 656.6f, 757.2f,
                665.2f, 734.1f, 681.0f, 714.8f, 696.2f, 695.0f, 708.1f, 673.1f, 728.4f, 659.6f, 753.2f, 659.2f,
                777.7f, 664.1f, 801.8f, 670.6f, 825.8f, 677.6f, 849.8f, 684.9f, 873.7f, 691.9f, 898.0f, 698.1f,
                922.0f, 705.1f, 945.1f, 714.6f, 967.7f, 725.3f, 990.6f, 735.1f, 1014.6f, 742.2f, 1039.3f, 745.9f,
                1064.2f, 747.3f, 1089.1f, 749.5f, 1113.7f, 754.0f, 1138.3f, 758.7f, 1163.1f, 761.0f, 1188.1f, 759.7f,
                1212.4f, 753.9f, 1235.8f, 745.4f, 1258.9f, 735.8f, 1282.5f, 727.6f, 1307.0f, 722.5f, 1331.9f, 720.6f,
                1356.9f, 719.9f, 1381.8f, 718.5f, 1406.5f, 714.5f, 1431.0f, 709.4f, 1455.6f, 704.9f, 1480.4f, 701.9f,
                1500.0f, 700.0f, 1494.4f, 675.8f, 1502.2f, 652.4f, 1507.3f, 628.0f, 1511.8f, 603.4f, 1519.6f, 579.7f,
                1535.3f, 561.9f, 1559.9f, 565.2f, 1584.4f, 570.3f, 1608.9f, 568.0f, 1621.3f, 547.9f, 1621.4f, 522.9f,
                1621.8f, 497.9f, 1622.2f, 472.9f, 1624.4f, 448.0f, 1629.5f, 423.6f, 1638.3f, 400.2f, 1651.7f, 379.2f,
                1668.0f, 360.2f, 1685.2f, 342.1f, 1702.6f, 324.1f, 1720.5f, 306.6f, 1740.2f, 291.3f, 1762.1f, 279.4f,
                1785.7f, 271.3f,
            }),
            new Run("West Cat Track", true, 40, -1, new[]
            {
                -220.0f, -240.0f, -237.8f, -257.5f, -255.2f, -275.5f, -253.8f, -291.3f, -229.7f, -285.2f, -207.1f, -274.5f,
                -186.7f, -260.1f, -169.1f, -242.5f, -155.0f, -221.9f, -144.5f, -199.2f, -135.0f, -176.1f, -123.8f, -153.8f,
                -110.8f, -132.4f, -98.4f, -110.7f, -88.5f, -87.8f, -82.3f, -63.6f, -76.9f, -39.2f, -67.1f, -16.3f,
                -52.2f, 3.7f, -34.5f, 21.4f, -15.6f, 37.8f, 3.5f, 53.9f, 22.7f, 69.9f, 40.6f, 87.3f,
                55.9f, 107.0f, 68.1f, 128.8f, 76.5f, 152.3f, 83.1f, 176.5f, 92.5f, 199.5f, 114.7f, 206.9f,
                138.7f, 200.1f, 161.4f, 189.8f, 183.8f, 178.5f, 207.0f, 169.5f, 231.7f, 169.7f, 252.6f, 183.0f,
                269.1f, 201.8f, 285.7f, 220.5f, 304.5f, 236.9f, 324.3f, 252.2f, 342.9f, 268.8f, 358.6f, 288.2f,
                371.9f, 309.3f, 383.8f, 331.3f, 395.5f, 353.5f, 408.2f, 375.0f, 423.0f, 395.0f, 441.5f, 411.8f,
                463.7f, 423.0f, 488.1f, 428.2f, 513.0f, 429.1f, 538.0f, 427.5f, 562.9f, 425.4f, 587.8f, 423.5f,
                612.3f, 418.7f, 635.2f, 408.8f, 655.9f, 394.9f, 674.8f, 378.6f, 693.5f, 361.9f, 712.6f, 345.8f,
                731.9f, 329.9f, 750.6f, 313.4f, 770.1f, 297.7f, 791.2f, 284.4f, 801.2f, 262.2f, 820.4f, 254.6f,
                838.2f, 271.8f, 850.7f, 293.4f, 860.3f, 316.5f, 867.9f, 340.3f, 875.1f, 364.3f, 883.7f, 387.7f,
                895.4f, 409.7f, 912.1f, 428.2f, 934.5f, 438.7f, 959.3f, 440.2f, 984.2f, 437.9f, 1009.2f, 437.8f,
                1033.2f, 444.5f, 1054.2f, 457.9f, 1071.9f, 475.4f, 1087.4f, 495.1f, 1103.0f, 514.6f, 1121.1f, 531.8f,
                1142.9f, 543.8f, 1166.6f, 551.8f, 1190.5f, 558.9f, 1214.9f, 564.1f, 1239.7f, 562.2f, 1263.6f, 554.8f,
                1287.0f, 546.2f, 1310.0f, 536.2f, 1331.4f, 523.5f, 1332.2f, 502.1f, 1328.3f, 477.5f, 1320.0f, 454.1f,
                1301.7f, 438.9f, 1302.1f, 417.9f, 1313.8f, 395.8f, 1324.7f, 373.8f, 1332.0f, 354.2f, 1351.7f, 339.4f,
                1364.1f, 317.7f, 1380.6f, 299.0f, 1391.4f, 277.3f, 1388.0f, 252.7f, 1374.4f, 232.1f, 1355.0f, 216.3f,
                1341.6f, 195.5f, 1332.8f, 172.1f, 1322.6f, 149.3f, 1313.0f, 126.2f, 1305.9f, 102.3f, 1303.5f, 77.5f,
                1304.0f, 52.5f, 1304.7f, 27.5f, 1306.1f, 2.5f, 1306.0f, -22.5f, 1302.1f, -47.1f, 1294.5f, -70.9f,
                1281.6f, -92.1f, 1265.2f, -111.0f, 1251.3f, -131.8f, 1240.1f, -154.1f, 1230.8f, -177.3f, 1223.8f, -201.3f,
                1220.3f, -226.0f, 1219.8f, -251.0f, 1220.0f, -260.0f,
            }),
            new Run("East Cat Track", true, 40, -1, new[]
            {
                540.0f, 520.0f, 563.7f, 512.1f, 587.8f, 505.6f, 612.5f, 501.3f, 637.2f, 497.8f, 661.8f, 493.4f,
                685.3f, 485.0f, 708.5f, 476.6f, 731.4f, 485.4f, 750.0f, 502.0f, 766.4f, 520.9f, 782.4f, 540.1f,
                799.7f, 558.1f, 819.0f, 573.9f, 840.5f, 586.7f, 863.5f, 596.3f, 888.0f, 600.7f, 910.0f, 590.4f,
                925.6f, 571.0f, 934.9f, 547.9f, 950.3f, 529.2f, 969.9f, 513.8f, 983.6f, 493.5f, 969.8f, 474.8f,
                947.6f, 463.5f, 949.4f, 448.3f, 973.3f, 441.3f, 998.0f, 437.9f, 1022.8f, 440.6f, 1045.3f, 451.3f,
                1064.4f, 467.3f, 1080.6f, 486.3f, 1095.9f, 506.1f, 1112.6f, 524.6f, 1132.8f, 539.1f, 1156.0f, 548.4f,
                1179.9f, 555.8f, 1204.0f, 562.4f, 1228.8f, 564.2f, 1253.1f, 558.3f, 1276.7f, 550.2f, 1299.9f, 540.8f,
                1322.2f, 529.7f, 1337.2f, 512.0f, 1330.1f, 488.4f, 1322.4f, 464.9f, 1311.7f, 443.6f, 1296.7f, 427.6f,
                1308.6f, 405.6f, 1321.5f, 384.2f, 1323.0f, 360.3f, 1344.1f, 347.3f, 1358.8f, 327.4f, 1373.8f, 307.8f,
                1397.2f, 300.0f, 1422.1f, 297.8f, 1447.1f, 296.7f, 1472.1f, 296.3f, 1497.0f, 298.3f, 1521.5f, 303.2f,
                1546.2f, 306.8f, 1571.2f, 307.5f, 1596.2f, 306.1f, 1621.1f, 304.1f, 1646.0f, 302.1f, 1670.9f, 299.8f,
                1695.3f, 294.6f, 1716.9f, 282.4f,
            }),
        };

        /// <summary>
        /// Builds the resort into <paramref name="history"/>: the lifts, then the trails, each a built command whose ground
        /// is <paramref name="fold"/>'s rebuild of the design with it (trails, then lifts, TL3), accepted once the command
        /// is. <paramref name="source"/> and <paramref name="lattice"/> are the mountain's natural ground (the fold's own),
        /// <paramref name="frame"/> its local frame; <paramref name="types"/> gives a catalog id's lift type (the game's
        /// measured types; <see cref="LiftType.Of"/> by default). Returns a report, one line per item.
        /// </summary>
        public static Report Build(DesignHistory history, DesignEarthwork fold, IHeightSource source, EditLattice lattice, LocalFrame frame,
                                   Func<string, LiftType> types = null)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));
            if (fold == null) throw new ArgumentNullException(nameof(fold));
            types ??= LiftType.Of;
            var report = new Report();
            foreach (var line in Lifts) report.Add(BuildLift(history, fold, source, lattice, frame, types(line.CatalogId), line));
            foreach (var run in Trails) report.Add(BuildTrail(history, fold, run));
            return report;
        }

        /// <summary>What a build made: one line per item, and how many went in.</summary>
        public sealed class Report
        {
            readonly StringBuilder _text = new StringBuilder();
            public int Accepted { get; private set; }
            public int Rejected { get; private set; }
            public string Text => _text.ToString();
            internal void Add((bool ok, string line) item)
            {
                if (item.ok) Accepted++; else Rejected++;
                _text.AppendLine(item.line);
            }
        }

        static (bool, string) BuildLift(DesignHistory history, DesignEarthwork fold, IHeightSource source, EditLattice lattice, LocalFrame frame,
                                        LiftType type, LiftLine line)
        {
            var b = frame.ToAlbers(line.BottomX, line.BottomZ);
            var t = frame.ToAlbers(line.TopX, line.TopZ);
            var ground = new EditedHeights(source, lattice, history.Terrain);
            // As a player builds one: Automatic towers, then the levels the review suggests (LT34), a few rounds.
            var built = LiftBuild.Build(type, ground, b.X, b.Y, t.X, t.Y, TowerStance.Leaned);
            double handB = 0, handT = 0;
            for (int round = 0; round < 8 && (built.SuggestedBottomMetres != 0 || built.SuggestedTopMetres != 0); round++)
            {
                handB += built.SuggestedBottomMetres;
                handT += built.SuggestedTopMetres;
                built = LiftBuild.Build(type, ground, b.X, b.Y, t.X, t.Y, TowerStance.Leaned, null, handB, handT);
            }
            var cmd = new AddLift(history.Revision, line.Name, line.CatalogId, PointMm.FromMetres(line.BottomX, line.BottomZ),
                                  PointMm.FromMetres(line.TopX, line.TopZ), TowerMode.Automatic, TowerStance.Leaned, built.Towers,
                                  WorkStage.Built, built.Levels);
            var r = Commit(history, fold, cmd);
            return (r.Accepted, string.Format(CultureInfo.InvariantCulture, "lift {0} ({1}): {2}, {3} towers, levels {4:+0;-0;0} / {5:+0;-0;0} ft, {6}",
                line.Name, line.CatalogId, r.Accepted ? "built" : "REJECTED " + r, built.Towers.Count, handB / 0.3048, handT / 0.3048,
                built.Ok ? (built.Clean ? "clean" : built.Problems.Count + " warnings") : "blocked"));
        }

        static (bool, string) BuildTrail(DesignHistory history, DesignEarthwork fold, Run run)
        {
            var r = Commit(history, fold, Command(history.Revision, run));
            return (r.Accepted, string.Format(CultureInfo.InvariantCulture, "trail {0} ({1}, {2} ft): {3}",
                run.Name, run.Graded ? "graded" : "natural", run.WidthFeet, r.Accepted ? "built" : "REJECTED " + r));
        }

        /// <summary>The built AddTrail for a run: one stroke of its width along the centreline, then its island erased.</summary>
        public static AddTrail Command(long revision, Run run)
        {
            var points = new PointMm[run.Centreline.Length / 2];
            for (int i = 0; i < points.Length; i++) points[i] = PointMm.FromMetres(run.Centreline[2 * i], run.Centreline[2 * i + 1]);
            var strokes = new List<Stroke> { new Stroke(DesignUnits.FeetToMm(run.WidthFeet), false, points) };
            if (run.IslandAt >= 0) strokes.Add(new Stroke(DesignUnits.FeetToMm(IslandFeet), true, new[] { points[run.IslandAt] }));
            return new AddTrail(revision, run.Name, run.Graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun,
                                TrailShape.FromStrokes(null, strokes), points[0], points[points.Length - 1], WorkStage.Built);
        }

        /// <summary>A command in as Break ground puts it: the fold's ground for the design with it, accepted with the command.</summary>
        public static CommandResult Commit(DesignHistory history, DesignEarthwork fold, DesignCommand cmd)
        {
            var after = cmd.Apply(history.Document);
            if (!after.Accepted) return after;
            var rebuilt = fold.Rebuild(after.Document, history.Terrain);
            var r = history.Submit(cmd, rebuilt.Edits);
            if (r.Accepted) fold.Accept(rebuilt);
            return r;
        }
    }
}
