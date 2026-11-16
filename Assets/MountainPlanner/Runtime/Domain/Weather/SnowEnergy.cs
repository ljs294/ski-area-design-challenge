using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// One cell's snowpack (SN-02 v2, WX-63): total water and held liquid in µm of water (= g/m², SI-07), the two layers'
    /// temperatures (0.01 °C, never above 0), brightness (millionths) and bulk density (g/m³: fine units, so a slow change
    /// still moves in an hour's step and the clock can't stall it). The surface layer is the top
    /// <see cref="SnowTuning.SurfaceUm"/> of ice at <see cref="SurfaceTemp"/>; the rest is the deep pack at
    /// <see cref="DeepTemp"/>.
    /// </summary>
    public struct SnowPack
    {
        public int Swe, Liquid, SurfaceTemp, Albedo, Density;
        /// <summary>The deep pack's temperature in 0.0001 °C: a metre of snow cools by a fraction of 0.01 °C in a night's hour,
        /// and rounding that away each step would lose the cold the step's length decides.</summary>
        public int DeepTemp;
        /// <summary>The deep pack's temperature in 0.01 °C, as <see cref="SurfaceTemp"/>.</summary>
        public int PackTemp => DeepTemp / 100;
        /// <summary>The surface layer's own share of <see cref="Liquid"/> (µm): its melt and the rain, until it drains down past
        /// the layer's holding capacity. Kept apart so a night refreezes the surface's water, and the deep pack's only as fast
        /// as the cold conducts down, whatever the step's length.</summary>
        public int SurfaceLiquid;
        public int Ice => Swe - Liquid;
        /// <summary>Snow depth, mm (water ÷ density).</summary>
        public int DepthMm => Swe <= 0 || Density <= 0 ? 0 : (int)((long)Swe * 1000 / Density);
        /// <summary>Brightness in ‰ and density in kg/m³, for readouts.</summary>
        public int AlbedoPermille => Albedo / 1000;
        public int DensityKg => Density / 1000;
    }

    /// <summary>
    /// One step's weather at a cell, as the snow reads it. Temperatures in 0.1 °F (the weather's), wind in 0.1 mph, cloud
    /// and shares in ‰; sun in W/m² already on the slope (beam × incidence, 0 under the skyline; diffuse × open sky, see
    /// <see cref="SlopeSun"/>); new snow and rain in µm of water; the step's length in seconds (a face hour is K real hours).
    /// </summary>
    public struct SnowInput
    {
        public int Seconds, Air, Dew, WetBulb, Feet, Wind, Cloud, Beam, Diffuse, OpenSky, Canopy, Snow, SnowDensity, Rain;
    }

    /// <summary>What the step did (WX-64): melt made, water run off, ice sublimated (negative: frost gained), water
    /// refrozen, the surface frozen, and the energy that crossed the surface (µJ/m²) for the balance test.</summary>
    public struct SnowOutput
    {
        public int Melt, Runoff, Sublimated, Refrozen;
        public bool Frozen, Thawed;
        public long EnergyIn;
        /// <summary>The step's mean fluxes into the surface (mW/m²): absorbed sun, net longwave, sensible, latent.</summary>
        public int Shortwave, Longwave, Sensible, Latent;
        /// <summary>WX-64: the skin's temperature at the step's end (0.01 °C; 0 when the top is wet): the top few millimetres in
        /// balance with the sky, the air and the layer under them, holding no heat of their own. Powder, crust and the
        /// frozen-surface flag read the skin; the surface layer's own temperature is its bulk cold content.</summary>
        public int Skin;
    }

    /// <summary>The snow energy's numbers (SN-09 v2), so Domain needs no reference to Pacing (SI-01). One set for every site.</summary>
    public readonly struct SnowTuning
    {
        /// <summary>Ice in the surface layer at an hour's step (µm; × the step's hours, up to 4), and below which the ground
        /// shows through the brightness (µm; SN-09 v4: 5 mm of water, about 2 in of new snow).</summary>
        public readonly int SurfaceUm, ThinUm;
        /// <summary>Brightness (‰): fresh, the cold floor, the wet floor, bare ground; new snow resets it in full at ResetUm.</summary>
        public readonly int AlbedoFresh, AlbedoCold, AlbedoWet, AlbedoGround, AlbedoResetUm;
        /// <summary>Brightness time constants (s): cold, melting.</summary>
        public readonly int AlbedoColdTau, AlbedoWetTau;
        /// <summary>The share of sun a full canopy stops (‰).</summary>
        public readonly int CanopyShade;
        /// <summary>Turbulent exchange: speed = WindPer10k ÷ 10,000 × wind + CalmMms (mm/s).</summary>
        public readonly int WindPer10k, CalmMms;
        /// <summary>Heat from the ground into the base (mW/m²).</summary>
        public readonly int GroundMw;
        /// <summary>SN-10's holding capacity (‰ of the water); SN-09's crumb (µm).</summary>
        public readonly int HoldPermille, CrumbUm;
        /// <summary>SN-08 v2: settling time constant (s), its speed-up when wet, and the targets (kg/m³) cold, near freezing, wet.</summary>
        public readonly int SettleTau, SettleWetFactor, DensityCold, DensityWarm, DensityWet;

        public SnowTuning(int surfaceUm, int thinUm, int albedoFresh, int albedoCold, int albedoWet, int albedoGround, int albedoResetUm,
                          int albedoColdTau, int albedoWetTau, int canopyShade, int windPer10k, int calmMms, int groundMw,
                          int holdPermille, int crumbUm, int settleTau, int settleWetFactor, int densityCold, int densityWarm, int densityWet)
        {
            SurfaceUm = surfaceUm; ThinUm = thinUm; AlbedoFresh = albedoFresh; AlbedoCold = albedoCold; AlbedoWet = albedoWet;
            AlbedoGround = albedoGround; AlbedoResetUm = albedoResetUm; AlbedoColdTau = albedoColdTau; AlbedoWetTau = albedoWetTau;
            CanopyShade = canopyShade; WindPer10k = windPer10k; CalmMms = calmMms; GroundMw = groundMw; HoldPermille = holdPermille;
            CrumbUm = crumbUm; SettleTau = settleTau; SettleWetFactor = settleWetFactor; DensityCold = densityCold;
            DensityWarm = densityWarm; DensityWet = densityWet;
        }

        /// <summary>The fitted set (task 4.07 PR A1). Pacing v8 carries the same numbers.</summary>
        public static readonly SnowTuning Default = new SnowTuning(
            surfaceUm: 25_000, thinUm: 5_000, albedoFresh: 850, albedoCold: 700, albedoWet: 550, albedoGround: 200, albedoResetUm: 5_000,
            albedoColdTau: 1_728_000, albedoWetTau: 1_209_600, canopyShade: 700, windPer10k: 15, calmMms: 1, groundMw: 2_000,
            holdPermille: 50, crumbUm: 2_540, settleTau: 2_400_000, settleWetFactor: 3, densityCold: 300, densityWarm: 380, densityWet: 450);
    }

    /// <summary>
    /// WX-63: the pack's energy each step, one integer function shared by the game and the tests (task 4.07, rules SN-09 v2,
    /// SN-10, SN-12 v2, WX-60…64). A thin surface layer and the deep pack, each with its own temperature (cold content):
    /// - into the surface: absorbed sun ((1 − brightness), canopy shade), longwave from the sky (cloud, open sky) and from
    ///   trees and terrain at the air's temperature, minus its own emission; sensible and latent exchange with the air by
    ///   wind (the latent part is SN-12's sublimation, and condensation in a warm humid wind); rain's heat; conduction from
    ///   the deep pack, whose base gets the ground's heat;
    /// - the surface temperature is solved implicitly (linearised around the last), so a dusting can't swing; at 0 °C the
    ///   surplus melts. Melt and rain go into held water, which refreezes against cold content (a cold pack warms to 0 °C
    ///   before water runs off) and runs off past SN-10's holding capacity.
    /// Integer only: µW/m², µJ/m², g/m², 0.01 °C. Every division rounds toward zero, the same on every runtime.
    /// </summary>
    public static class SnowEnergy
    {
        const long IceHeat = 21_000;        // µJ per g per 0.01 K (2.1 J/g/K)
        const long WaterHeat = 41_860;      // µJ per g per 0.01 K
        const long Fusion = 334_000_000;    // µJ per g
        const long Sublimation = 2_834_000_000, Vaporisation = 2_501_000_000;   // µJ per g

        /// <summary>0.1 °F to 0.01 °C.</summary>
        public static int CentiC(int tenthsF) => (int)WeatherMath.DivRound((tenthsF - 320) * 50L, 9);

        /// <summary>0.01 °C to 0.1 °F.</summary>
        public static int TenthsF(int centiC) => 320 + (int)WeatherMath.DivRound(centiC * 9L, 50);

        /// <summary>σT⁴ in µW/m² for a temperature in 0.01 °C.</summary>
        public static long Emission(int centiC)
        {
            uint t = (uint)(centiC - TableMin);
            return t < TableSize ? Tables.Emission[t] : EmissionOf(centiC);
        }

        static long EmissionOf(int centiC)
        {
            long k10 = (27_315 + centiC) / 10;            // 0.1 K
            long k2 = k10 * k10;
            return k2 * k2 / 1000 * 5_670_374 / 1_000_000_000;
        }

        /// <summary>4σT³ in µW/m² per 0.01 K.</summary>
        static long EmissionSlopeOf(int centiC)
        {
            long k10 = (27_315 + centiC) / 10;
            return 4 * (k10 * k10 * k10 * 5_670_374 / 100_000_000_000) / 100;
        }

        /// <summary>Saturation vapour pressure over ice below 0 °C (over water at and above), mPa.</summary>
        static long SaturationOf(int centiC)
        {
            long w = WeatherAir.SaturationMilliPa(TenthsF(centiC));
            return centiC >= 0 ? w : w * (1_000_000 + 97L * centiC) / 1_000_000;
        }

        /// <summary>
        /// The surface's terms at a temperature (0.01 °C), as the solves use them: its emission and the emission's slope (both
        /// × the snow's emissivity, 0.99), the saturation vapour pressure and its slope (mPa per 0.01 K, over ±0.5 K). Read
        /// from <see cref="Tables"/> over the range a pack reaches, worked out by the same integer formulas outside it: the
        /// same numbers either way (task 4.07 PR A2: a face hour over a whole site calls these about 15 times a cell).
        /// </summary>
        static void Surface(int centiC, out long lwOut, out long lwSlope, out long es, out long dedt)
        {
            uint t = (uint)(centiC - TableMin);
            if (t < TableSize)
            {
                lwOut = Tables.LwOut[t]; lwSlope = Tables.LwSlope[t]; es = Tables.Es[t]; dedt = Tables.Dedt[t];
                return;
            }
            SurfaceOf(centiC, out lwOut, out lwSlope, out es, out dedt);
        }

        static void SurfaceOf(int centiC, out long lwOut, out long lwSlope, out long es, out long dedt)
        {
            lwOut = EmissionOf(centiC) * 990 / 1000; lwSlope = EmissionSlopeOf(centiC) * 990 / 1000;
            es = SaturationOf(centiC); dedt = (SaturationOf(centiC + 50) - SaturationOf(centiC - 50)) / 100;
        }

        /// <summary>The tables' range: −80 °C to +2 °C in 0.01 °C.</summary>
        const int TableMin = -8000, TableMax = 200;
        const uint TableSize = TableMax - TableMin + 1;

        /// <summary>Built once, on first use, by the formulas they stand for (a test checks every entry).</summary>
        static class Tables
        {
            internal static readonly long[] Emission = new long[TableSize], LwOut = new long[TableSize], LwSlope = new long[TableSize],
                                            Es = new long[TableSize], Dedt = new long[TableSize];

            static Tables()
            {
                for (int i = 0; i < TableSize; i++)
                {
                    int c = TableMin + i;
                    Emission[i] = EmissionOf(c);
                    SurfaceOf(c, out LwOut[i], out LwSlope[i], out Es[i], out Dedt[i]);
                }
            }
        }

        /// <summary>For the tests: the table's terms and the formulas' at a temperature, which must be equal.</summary>
        internal static bool TablesMatch(int centiC)
        {
            Surface(centiC, out long a, out long b, out long c, out long d);
            SurfaceOf(centiC, out long a2, out long b2, out long c2, out long d2);
            return a == a2 && b == b2 && c == c2 && d == d2 && Emission(centiC) == EmissionOf(centiC);
        }

        /// <summary>The step (see the type's summary). The pack is changed in place; returns what happened.</summary>
        public static SnowOutput Step(ref SnowPack p, in SnowInput x, in SnowTuning k)
        {
            var o = new SnowOutput();
            long dt = x.Seconds;
            // The surface layer is what one day's cycle of sun and night sky reaches. At the game's clock a face day stands for
            // K real days but has one night, so the layer is K× deeper: it stores a face night's cold and holds its water as K
            // real nights would (a fixed layer would freeze and thaw once per face day and melt a third too much).
            int surf = (int)Math.Min(4L * k.SurfaceUm, Math.Max(k.SurfaceUm, (long)k.SurfaceUm * dt / 3600));
            int wb = CentiC(x.WetBulb);

            // ---- New snow: on top, mixing heat with the surface; what the surface can't hold passes down.
            if (x.Snow > 0)
            {
                int tn = Math.Min(0, wb);
                int rho = Math.Max(30, x.SnowDensity);
                long oldDepth = p.Swe > 0 && p.Density > 0 ? (long)p.Swe * 1_000_000 / p.Density : 0;   // µm of snow
                long newDepth = (long)x.Snow * 1000 / rho;
                int ice = p.Ice;
                if (ice <= 0) { p.SurfaceTemp = tn; p.DeepTemp = tn * 100; p.Albedo = k.AlbedoFresh * 1000; }
                int ms = Math.Min(ice, surf), md = ice - ms;
                long top = (long)ms + x.Snow;
                int mix = (int)(((long)ms * p.SurfaceTemp + (long)x.Snow * tn) / top);
                if (top > surf)
                {
                    long over = top - surf;
                    p.DeepTemp = (int)(((long)md * p.DeepTemp + over * mix * 100) / (md + over));
                }
                p.SurfaceTemp = mix;
                p.Swe += x.Snow;
                p.Density = (int)Math.Max(30_000, (long)p.Swe * 1_000_000 / Math.Max(1, oldDepth + newDepth));
                p.Albedo += (int)((k.AlbedoFresh * 1000L - p.Albedo) * Math.Min(x.Snow, k.AlbedoResetUm) / k.AlbedoResetUm);
            }

            // ---- Rain: held water, bringing its heat.
            long qRain = 0;
            if (x.Rain > 0)
            {
                if (p.Ice <= 0) { o.Runoff += x.Rain; return o; }
                p.Liquid += x.Rain; p.Swe += x.Rain; p.SurfaceLiquid += x.Rain;
                qRain = x.Rain * WaterHeat * Math.Max(0, wb);
            }
            if (p.Ice <= 0) { o.Runoff += p.Swe; Clear(ref p, ref o); return o; }

            {
                int ice = p.Ice;
                int ms = Math.Min(ice, surf), md = ice - ms;
                long cs = IceHeat * ms, cd = IceHeat * md, cd4 = IceHeat / 100 * md;   // cd4: per 0.0001 K
                int ts0 = p.SurfaceTemp, ta = CentiC(x.Air);
                long tq0 = p.DeepTemp;                                                  // 0.0001 °C

                // Conduction between the layers (Sturm 1997: k = 0.138 − 1.01ρ + 3.233ρ², ρ in g/cm³), over half the depth,
                // as the deep side's implicit conductance so a thin deep layer can't overshoot.
                long ke = 0;
                if (md > 0)
                {
                    long r = Math.Max(150, Math.Min(600, p.Density / 1000));
                    long kMw = 138 - 1010 * r / 1000 + 3233 * r * r / 1_000_000;           // mW/m/K
                    long dMm = Math.Max(50, (long)p.Swe * 1000 / (2L * Math.Max(1, p.Density)));
                    long kc = Math.Max(1, kMw * 10_000 / dMm);                              // µW/m² per 0.01 K
                    ke = kc * cd / (cd + dt * kc);
                }
                long ground = (long)k.GroundMw * 1000;   // µW/m²

                // Sun, brightness through thin snow, canopy shade.
                long albedo = p.Albedo;                                                        // millionths
                if (p.Swe < k.ThinUm) albedo = k.AlbedoGround * 1000L + (albedo - k.AlbedoGround * 1000L) * p.Swe / k.ThinUm;
                long sw = (long)(x.Beam + x.Diffuse) * 1_000_000 * (1000 - (long)k.CanopyShade * x.Canopy / 1000) / 1000 * (1_000_000 - albedo) / 1_000_000;

                // Longwave: the sky's share (open sky, not under trees) at the sky's emissivity, the rest at the air's.
                long emClear = ClearSky(Math.Min(x.Dew, x.Air), ta);
                long em = emClear * (1000 - 840L * x.Cloud / 1000) / 1000 + 840L * x.Cloud / 1000;   // ‰
                long sky = (long)x.OpenSky * (1000 - x.Canopy) / 1000;                            // ‰
                long airEm = Emission(ta);
                long lwIn = airEm * (sky * em / 1000 + (1000 - sky)) / 1000;

                // Turbulent exchange: ρ c_p × speed, sensible per 0.01 K; latent per mPa of vapour pressure difference.
                long pa = WeatherAir.PressurePa(x.Feet);
                long k10 = (27_315 + ta) / 10;
                long rhoG = pa * 1_000_000 / (28_705 * k10);                                       // g/m³
                // Warm air over cold snow is stable and mixes less: the wind's part × (1 − 5·Ri)², Ri the bulk Richardson number
                // at 2 m from the last surface temperature (Price and Dunne 1976), 0 past Ri = 0.2; the calm part stays.
                long windMms = (long)x.Wind * 44_704 / 1000;                                       // mm/s
                long stable = 1000;
                if (ta > ts0)
                {
                    long u2 = Math.Max(100L, windMms) * Math.Max(100L, windMms);
                    long riMilli = 1_962_000_000L * (ta - ts0) / (k10Of(ta) * u2);
                    long g = Math.Max(0, 1000 - 5 * Math.Min(200, riMilli));
                    stable = g * g / 1000;
                }
                long speed = (long)k.WindPer10k * windMms / 10_000 * stable / 1000 + k.CalmMms;     // mm/s
                long hs = rhoG * 1005 * speed / 100;                                               // µW/m² per 0.01 K
                long latent = ts0 < 0 ? Sublimation : Vaporisation;
                long hl = hs * 100 * (latent / 1000) * 622 / (1005L * pa * 1_000_000);            // µW/m² per mPa
                long ea = WeatherAir.SaturationMilliPa(Math.Min(x.Dew, x.Air));

                // Net into the surface, linear in its temperature around tl: A − B·Ts. Solved implicitly, then once more
                // linearised at the answer (one Newton step), so a long clear night's cooling isn't under-counted.
                long ts1 = ts0, melt = 0, a = 0, lwOut = 0, lwSlope = 0, es = 0, dedt = 0;
                int tl = ts0;
                for (int pass = 0; pass < 2; pass++)
                {
                    Surface(tl, out lwOut, out lwSlope, out es, out dedt);   // dedt: mPa per 0.01 K
                    long b = lwSlope + hs + hl * dedt + ke;
                    a = sw + lwIn - lwOut + lwSlope * tl + hs * ta + hl * (ea - es + dedt * tl) + ke * tq0 / 100 + (md == 0 ? ground : 0);
                    ts1 = (cs * ts0 + dt * a + qRain) / (cs + dt * b);
                    if (ts1 > 0) ts1 = 0;
                    tl = (int)ts1;
                }
                // A wet surface stays at 0 °C until its water has frozen (the latent heat holds it there), so a long step's
                // night loses heat at 0 °C's rate while it freezes, then cools for the rest of the step. Without this, a face
                // hour's surface would reach its cold equilibrium in minutes, stop losing heat and refreeze a fraction of
                // what an hour-by-hour night does.
                long tsEff = ts1, frozeSurface = 0;
                long wetSurface = p.SurfaceLiquid;
                if (ts1 < 0 && wetSurface > 0)
                {
                    tl = 0;
                    Surface(0, out lwOut, out lwSlope, out es, out dedt);
                    long b = lwSlope + hs + hl * dedt + ke;
                    a = sw + lwIn - lwOut + hs * ta + hl * (ea - es) + ke * tq0 / 100 + (md == 0 ? ground : 0);
                    long e0 = dt * a + qRain + cs * ts0;                       // the step's surplus held at 0 °C
                    if (e0 >= 0) ts1 = 0;
                    else
                    {
                        frozeSurface = Math.Min(wetSurface, -e0 / Fusion);
                        long rest = -e0 - frozeSurface * Fusion;                // deficit left once the water has frozen
                        long dtRest = dt * rest / -e0;
                        ts1 = -rest / (cs + b * dtRest);
                        tsEff = dtRest == 0 ? 0 : ts1 * dtRest / dt;            // flux-weighted: 0 °C while freezing
                    }
                    if (ts1 >= 0) tsEff = 0;
                }
                if (ts1 >= 0)
                {
                    long surplus = dt * a + qRain + cs * ts0;   // at Ts = 0
                    if (surplus > 0) melt = surplus / Fusion;
                    tsEff = 0;
                }

                // Latent mass at the step's surface temperature; the external energy that crossed, for the ledger.
                long le = hl * (ea - es - dedt * (tsEff - tl));
                o.Sublimated = (int)(-le * dt / latent);
                long lwNet = lwIn - lwOut - lwSlope * (tsEff - tl);
                long external = sw + lwNet + hs * (ta - tsEff) + le;
                o.EnergyIn = dt * (external + ground) + qRain;
                o.Shortwave = (int)(sw / 1000); o.Longwave = (int)(lwNet / 1000);
                o.Sensible = (int)(hs * (ta - tsEff) / 1000); o.Latent = (int)(le / 1000);
                if (frozeSurface > 0) { p.Liquid -= (int)frozeSurface; p.SurfaceLiquid -= (int)frozeSurface; o.Refrozen += (int)frozeSurface; }

                // Deep pack: the same conduction the surface took, and the ground at its base.
                long tq1 = tq0;
                long deepMelt = 0;
                if (md > 0)
                {
                    long flow = ke * (tq0 - 100 * tsEff) / 100;   // into the surface
                    long e = dt * (ground - flow);
                    tq1 = tq0 + e / cd4;
                    if (tq1 > 0) { deepMelt = tq1 * cd4 / Fusion; tq1 = 0; }
                }

                // Melt past the surface layer's ice warms then melts the deep pack.
                if (melt > ms)
                {
                    long rest = (melt - ms) * Fusion;
                    melt = ms;
                    if (md > 0)
                    {
                        long warm = cd4 * -tq1;
                        if (rest <= warm) tq1 += rest / cd4;
                        else { deepMelt += (rest - warm) / Fusion; tq1 = 0; }
                    }
                }
                deepMelt = Math.Min(deepMelt, md);
                long totalMelt = melt + deepMelt;
                o.Melt = (int)totalMelt;
                o.Thawed = melt > 0;
                p.Liquid += (int)totalMelt; p.SurfaceLiquid += (int)melt;

                // Frost or sublimation: on the surface ice.
                int sub = (int)Math.Max(-(long)surf, Math.Min(o.Sublimated, p.Ice));
                o.Sublimated = sub;
                p.Swe -= sub;

                // Refreeze held water against cold content: the surface layer its own water, then what it can't hold drains
                // into the deep pack, which refreezes its water against its own cold content.
                int iceNow = p.Ice;
                int msNow = Math.Min(iceNow, surf), mdNow = iceNow - msNow;
                if (p.SurfaceLiquid > p.Liquid) p.SurfaceLiquid = p.Liquid;
                if (ts1 < 0 && p.SurfaceLiquid > 0 && msNow > 0)
                {
                    long cap = IceHeat * msNow * -ts1 / Fusion;
                    long r = Math.Min(cap, p.SurfaceLiquid);
                    p.Liquid -= (int)r; p.SurfaceLiquid -= (int)r; o.Refrozen += (int)r;
                    ts1 += r * Fusion / (IceHeat * (msNow + r));
                }
                long surfaceHold = (long)msNow * k.HoldPermille / 1000;
                if (p.SurfaceLiquid > surfaceHold) p.SurfaceLiquid = (int)surfaceHold;
                if (tq1 < 0 && p.Liquid > p.SurfaceLiquid && mdNow > 0)
                {
                    long cap = IceHeat / 100 * mdNow * -tq1 / Fusion;
                    long r = Math.Min(cap, p.Liquid - p.SurfaceLiquid);
                    p.Liquid -= (int)r; o.Refrozen += (int)r;
                    tq1 += r * Fusion / (IceHeat / 100 * (mdNow + r));
                }

                // The surface refills from the deep pack where melt took its ice, taking the deep pack's temperature.
                int ms1 = Math.Min(p.Ice, surf);
                long kept = Math.Max(0, ms - totalMelt + o.Refrozen - Math.Max(0, sub));
                kept = Math.Min(kept, ms1);
                if (ms1 > 0 && kept < ms1) ts1 = (kept * ts1 + (ms1 - kept) * tq1 / 100) / ms1;

                p.SurfaceTemp = (int)Math.Min(0, ts1);
                p.DeepTemp = (int)Math.Min(0, tq1);

                // The skin: the same air and sky terms, against conduction from the surface layer's middle, no storage.
                {
                    long r = Math.Max(50, Math.Min(600, p.Density / 1000));
                    long kMw = Math.Max(25, 138 - 1010 * r / 1000 + 3233 * r * r / 1_000_000);
                    long halfMm = Math.Max(5, (long)Math.Min(p.Ice, surf) * 1000 / (2L * Math.Max(1, p.Density)));
                    long gk = Math.Max(1, kMw * 10_000 / halfMm);
                    int tk = p.SurfaceTemp;
                    long skin = tk;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Surface(tk, out long lo, out long ls, out long e1, out long d1);
                        long a1 = sw + lwIn - lo + ls * tk + hs * ta + hl * (ea - e1 + d1 * tk) + gk * p.SurfaceTemp;
                        skin = a1 / (ls + hs + hl * d1 + gk);
                        if (skin > 0) skin = 0;
                        tk = (int)Math.Max(-6000, skin);
                    }
                    o.Skin = (int)skin;
                }
                o.Frozen = p.SurfaceTemp < -50;
            }

            // ---- SN-10: water past the holding capacity runs off; SN-09's crumb.
            long hold = (long)p.Swe * k.HoldPermille / 1000;
            if (p.Liquid > hold) { int off = (int)(p.Liquid - hold); o.Runoff += off; p.Liquid -= off; p.Swe -= off; }
            if (p.SurfaceLiquid > p.Liquid) p.SurfaceLiquid = p.Liquid;
            // SN-09 v3: thin snow is patchy, and the bare ground between the patches melts it faster the thinner it is: while
            // it melts, a pack under the crumb loses up to the hour's melt again, more the thinner it is (melt × (crumb − pack)
            // ÷ crumb). No depth is a cliff at any melt: what's left changes at most 1 + melt ÷ crumb times as fast as what
            // was there. (v2 took the whole pack below the crumb at the first melt or drop of rain; a ÷ pack form steepened
            // without bound as the pack thinned, so a face hour's big melt still split neighbours into all or nothing.)
            if (p.Swe > 0 && p.Swe < k.CrumbUm && o.Melt > 0)
            {
                long extra = (long)o.Melt * (k.CrumbUm - p.Swe) / k.CrumbUm;
                if (extra >= p.Swe) { o.Runoff += p.Swe; o.Melt += p.Ice; Clear(ref p, ref o); return o; }
                // The patches that go take their share of the pack's water with them, so the pack thins to nothing smoothly.
                int liquid = (int)((long)p.Liquid * extra / p.Swe), ice = (int)extra - liquid;
                p.Swe -= (int)extra; p.Liquid -= liquid; o.Runoff += (int)extra; o.Melt += ice;
                if (p.SurfaceLiquid > p.Liquid) p.SurfaceLiquid = p.Liquid;
            }
            if (p.Ice <= 0) { o.Runoff += p.Swe; Clear(ref p, ref o); return o; }

            // ---- SN-08 v2: settling toward a density by wetness.
            {
                bool wet = p.Liquid > 0 || o.Melt > 0;
                long target = 1000L * (wet ? k.DensityWet : p.PackTemp > -100 ? k.DensityWarm : k.DensityCold);
                long tau = wet ? k.SettleTau / Math.Max(1, k.SettleWetFactor) : k.SettleTau;
                if (p.Density < target) p.Density += (int)((target - p.Density) * dt / (tau + dt));   // g/m³: never stalls
            }

            // ---- WX-61: brightness ages, faster when melting; only new snow resets it (above).
            // The melt season's faster ageing needs a ripe pack (USACE 1956's two curves): a sunny afternoon on a cold pack ages it as cold.
            bool ripe = p.PackTemp > -50 || p.Ice <= surf;
            // In millionths, so an hour's step still moves it (in ‰ the hourly step rounded to nothing 17 % short of the floor).
            if (o.Thawed && ripe) p.Albedo += (int)((k.AlbedoWet * 1000L - p.Albedo) * dt / (k.AlbedoWetTau + dt));
            else if (p.Albedo > k.AlbedoCold * 1000) p.Albedo += (int)((k.AlbedoCold * 1000L - p.Albedo) * dt / (k.AlbedoColdTau + dt));
            return o;
        }

        static long k10Of(int centiC) => (27_315 + centiC) / 10;   // 0.1 K

        /// <summary>
        /// WX-62: the clear sky's emissivity (‰) from the dew point (0.1 °F) and the air (0.01 °C), Brutsaert (1975): dry
        /// mountain air radiates less than humid valley air. The table is at 0 °C; (273.15 / T)^(1/7) is taken as
        /// 1 − (T − 273.15) ÷ 7T (within 0.05 % over −40…+30 °C).
        /// </summary>
        public static int ClearSky(int dewTenthsF, int airCentiC)
        {
            int[] t = SnowEnergyTables.ClearSkyPermille;
            int d = Math.Max(-800, Math.Min(1300, dewTenthsF)) + 800, i = d / 10, f = d % 10;
            long e0 = i >= t.Length - 1 ? t[t.Length - 1] : t[i] + (t[i + 1] - t[i]) * f / 10;
            long k = 27_315 + airCentiC;
            return (int)WeatherMath.Clamp(e0 * (7 * k - airCentiC) / (7 * k), 400, 1000);
        }

        /// <summary>The generator of <see cref="SnowEnergyTables.ClearSkyPermille"/> (floats; tests only).</summary>
        public static int[] BuildClearSky()
        {
            long[] es = WeatherAirTables.Es;
            var t = new int[es.Length];
            for (int i = 0; i < es.Length; i++) t[i] = (int)Math.Round(1240 * Math.Pow(es[i] / 1e5 / 273.15, 1 / 7.0));
            return t;
        }

        static void Clear(ref SnowPack p, ref SnowOutput o)
        {
            p = default;
            o.Frozen = false;
        }
    }

    /// <summary>
    /// WX-60: the sun on a slope from the integer sun (<see cref="Sky.SunVector"/>). The flat surface's clear-sky sun is
    /// 1361 W/m² × a transmittance that falls at low sun, × the hour's clearness; its diffuse share rises as the sky clouds.
    /// The beam on the slope is the flat beam × cos(incidence) ÷ sin(elevation), capped at 4 × the flat beam, and 0 when the
    /// sun is under the cell's skyline; the diffuse is × the open sky.
    /// </summary>
    public static class SlopeSun
    {
        /// <summary>Beam and diffuse W/m² on a cell with unit normal (millionths, x east, y north, z up), its open sky (‰),
        /// and whether the sun is above its skyline.</summary>
        public static (int Beam, int Diffuse) At(in Sky.SunVector sun, int clearness, int nx, int ny, int nz, int openSky, bool sunVisible)
        {
            if (sun.Up <= 0 || clearness <= 0) return (0, 0);
            long s = sun.Up;                                                       // sin(elevation), millionths
            long trans = 750L * s / (s + 50_000);                                  // ‰
            long global = 1361L * trans * s / 1000 * clearness / 1000 / Sky.SunVector.One;
            long diffuseShare = 1000 - 850L * clearness / 1000;                    // ‰
            long diffuse = global * diffuseShare / 1000;
            long beamFlat = global - diffuse;
            long beam = 0;
            if (sunVisible)
            {
                long cosInc = ((long)nx * sun.East + (long)ny * sun.North + (long)nz * sun.Up) / Sky.SunVector.One;
                if (cosInc > 0) beam = Math.Min(4 * beamFlat, beamFlat * cosInc / s);
            }
            return ((int)beam, (int)(diffuse * openSky / 1000));
        }

        /// <summary>
        /// As above with a soft skyline (task 4.07 PR A2): <paramref name="visiblePermille"/> of the beam clears the skyline (the
        /// sun fading over a few degrees as it sets behind a ridge, not switching off), 1000 the same as visible.
        /// </summary>
        public static (int Beam, int Diffuse) At(in Sky.SunVector sun, int clearness, int nx, int ny, int nz, int openSky, int visiblePermille)
        {
            var (beam, diffuse) = At(sun, clearness, nx, ny, nz, openSky, visiblePermille > 0);
            return (visiblePermille >= 1000 ? beam : (int)((long)beam * visiblePermille / 1000), diffuse);
        }
    }
}
