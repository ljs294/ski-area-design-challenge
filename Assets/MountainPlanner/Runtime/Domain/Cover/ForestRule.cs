#nullable enable
using System;

namespace MountainPlanner.Domain.Cover
{
    /// <summary>
    /// The forest rule adopted in D4 (data-spike report §6), and how it's scored against lidar truth.
    ///
    /// The Meta/WRI canopy map is the forest layer. It under-counts sparse, short trees and reads
    /// heights low, so: a 10 m cell is forest when any of its 1 m canopy cells reaches 3 m, and tree
    /// density and height are scaled by calibration factors (measured at Jackson Hole; refined as more
    /// lidar truth sites are added).
    /// </summary>
    public static class ForestRule
    {
        /// <summary>Canopy heights are stored in 0.25 m steps; 12 = 3 m, the height that counts as a tree.</summary>
        public const byte TreeCode = 12;
        public const double TreeMetres = 3.0;
        /// <summary>Metres per stored canopy step.</summary>
        public const double CanopyStep = 0.25;
        /// <summary>The cell size the rule is defined and scored on.</summary>
        public const int CellMetres = 10;
        /// <summary>A truth cell is forest when at least this share of it has trees (the spike's definition).</summary>
        public const int TruthForestPercent = 10;
        /// <summary>Value in a truth fixture for "no lidar here".</summary>
        public const byte NoTruth = 255;

        public static bool IsTree(byte canopy) => canopy >= TreeCode;

        /// <summary>
        /// The rule on a whole grid: each 10 m cell of a 1 m canopy grid is forest when any of its
        /// cells is a tree. The canopy grid's sides must be whole multiples of 10 m.
        /// </summary>
        public static bool[] ForestCells(byte[] canopy, int columns, int rows)
        {
            if (columns % CellMetres != 0 || rows % CellMetres != 0) throw new ArgumentException("The canopy grid must be whole 10 m cells.");
            if (canopy.Length != (long)columns * rows) throw new ArgumentException("The canopy grid's size doesn't match.");
            int nc = columns / CellMetres, nr = rows / CellMetres;
            var forest = new bool[nc * nr];
            for (int r = 0; r < rows; r++)
            {
                int row = r / CellMetres * nc;
                for (int c = 0; c < columns; c++)
                    if (canopy[(long)r * columns + c] >= TreeCode) forest[row + c / CellMetres] = true;
            }
            return forest;
        }

        /// <summary>The canopy forest share may fall this far below WorldCover's before the check fails.</summary>
        public const double MinFractionRatio = 0.35;

        /// <summary>
        /// Sanity check (0.3 §4.4 cautions): the canopy map's forest share against WorldCover's over the
        /// same cells. The canopy map is expected to read lower (Jackson Hole: 40% against 68%), but a
        /// collapse like the archive's lidar-forest failure (7.5% against 65%) means bad data. Returns a
        /// plain-words problem, or null when the shares are plausible. Sites with little forest pass.
        /// </summary>
        public static string? CheckFraction(double canopyShare, double worldCoverShare)
        {
            if (worldCoverShare < 0.10) return null;
            if (canopyShare >= worldCoverShare * MinFractionRatio) return null;
            return FormattableString.Invariant(
                $"The canopy map shows {canopyShare:P0} forest where WorldCover shows {worldCoverShare:P0}; the canopy data may be missing or broken.");
        }

        /// <summary>Accuracy of a forest prediction against lidar truth shares (percent per cell; 255 = no data).</summary>
        public static ForestScore Score(bool[] predicted, byte[] truthPercent)
        {
            if (predicted.Length != truthPercent.Length) throw new ArgumentException("Prediction and truth must be the same grid.");
            long valid = 0, correct = 0, tp = 0, fp = 0, fn = 0, truthForest = 0, predictedForest = 0;
            for (int i = 0; i < predicted.Length; i++)
            {
                byte t = truthPercent[i];
                if (t == NoTruth) continue;
                bool truth = t >= TruthForestPercent, p = predicted[i];
                valid++;
                if (truth == p) correct++;
                if (truth) truthForest++;
                if (p) predictedForest++;
                if (p && truth) tp++;
                else if (p) fp++;
                else if (truth) fn++;
            }
            double Share(long n) => valid > 0 ? (double)n / valid : 0;
            return new ForestScore(Share(correct), tp + fp > 0 ? (double)tp / (tp + fp) : 0, tp + fn > 0 ? (double)tp / (tp + fn) : 0,
                                   Share(predictedForest), Share(truthForest), valid);
        }
    }

    public readonly struct ForestScore
    {
        /// <summary>Share of cells where the prediction matches the truth.</summary>
        public readonly double Accuracy;
        /// <summary>When it says forest, how often it's right.</summary>
        public readonly double Precision;
        /// <summary>How much of the real forest it finds.</summary>
        public readonly double Recall;
        public readonly double PredictedShare;
        public readonly double TruthShare;
        public readonly long Cells;

        public ForestScore(double accuracy, double precision, double recall, double predictedShare, double truthShare, long cells)
        {
            Accuracy = accuracy;
            Precision = precision;
            Recall = recall;
            PredictedShare = predictedShare;
            TruthShare = truthShare;
            Cells = cells;
        }

        public override string ToString() =>
            FormattableString.Invariant($"accuracy {Accuracy:P1}, precision {Precision:P1}, recall {Recall:P1}, forest {PredictedShare:P1} vs truth {TruthShare:P1} over {Cells} cells");
    }

    /// <summary>
    /// Calibration of the canopy map against lidar (D4). The factors multiply the map's tree density
    /// and height. Two regions, picked by the site's longitude (<see cref="ForSite"/>): the West, measured at
    /// Jackson Hole, and the East, measured at five New England ski areas (forest-structure report §9).
    /// </summary>
    public readonly struct ForestCalibration
    {
        public readonly string Region;
        public readonly double DensityFactor;
        /// <summary>Scales the canopy height of any one 1 m cell (median tree metre: 7 m mapped, 12 m lidar).</summary>
        public readonly double HeightFactor;
        /// <summary>
        /// Scales a 10 m cell's tallest canopy value to its dominant tree height. Larger than
        /// <see cref="HeightFactor"/> because the map under-reads tall trees most (9 m mapped, 20 m lidar).
        /// </summary>
        public readonly double DominantHeightFactor;

        public ForestCalibration(string region, double densityFactor, double heightFactor, double dominantHeightFactor)
        {
            Region = region;
            DensityFactor = densityFactor;
            HeightFactor = heightFactor;
            DominantHeightFactor = dominantHeightFactor;
        }

        /// <summary>
        /// Measured at Jackson Hole 2 km (lidar truth fixture, task 07): tree cover 18.1% mapped against
        /// 26.6% by lidar (×1.47); per-metre median height 7 m against 12 m (×1.6, data-spike report);
        /// tallest tree per 10 m forest cell 9.0 m against 20.0 m (×2.2).
        /// </summary>
        public static readonly ForestCalibration Default = new ForestCalibration("Jackson Hole (WY) lidar, 2020", 1.5, 1.6, 2.2);

        /// <summary>The West: Jackson Hole's factors (<see cref="Default"/>).</summary>
        public static readonly ForestCalibration West = Default;

        /// <summary>
        /// The East: the mean of five New England ski areas against 3DEP lidar (Stowe, Loon, Killington, Sunday
        /// River, Sugarloaf; 20 patches of 200 m each). There the canopy map reads tree cover about right (×0.93)
        /// and heights only a little low (×1.26). The per-metre factor wasn't measured; it scales with the dominant one.
        /// </summary>
        public static readonly ForestCalibration East = new ForestCalibration("New England ski areas (3DEP lidar, 2013–2023)", 0.93, 1.6 * 1.26 / 2.2, 1.26);

        /// <summary>The 100th meridian, the usual line between the arid West and the humid East.</summary>
        public const double EastOfLongitude = -100;

        /// <summary>The regional calibration for a site: East of the 100th meridian uses the East's factors.</summary>
        public static ForestCalibration ForSite(double longitude) => longitude > EastOfLongitude ? East : West;

        /// <summary>A calibrated height in metres for one 1 m canopy cell.</summary>
        public double TreeHeight(byte canopy) => canopy * ForestRule.CanopyStep * HeightFactor;

        /// <summary>The calibrated dominant tree height in metres from a 10 m cell's tallest canopy value.</summary>
        public double DominantHeight(byte tallestCanopy) => tallestCanopy * ForestRule.CanopyStep * DominantHeightFactor;
    }
}
