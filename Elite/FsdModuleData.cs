using System;
using System.Collections.Generic;

namespace Elite
{
    /// <summary>
    /// Published base statistics for every Frame Shift Drive, keyed on the Loadout "Item" name.
    ///
    /// Range and fuel both come from the same model (see EliteData.GetJumpRange):
    ///     fuel used = LinearConstant x (distance x mass / OptimalMass) ^ PowerConstant
    ///     range     = (OptimalMass / mass) x (fuel / LinearConstant) ^ (1 / PowerConstant)
    ///
    /// Why a table rather than the arithmetic we used before: PowerConstant really is
    /// 2.0 + (size - 2) x 0.15, and for the STANDARD drives the linear constant really is
    /// rating-derived (A=0.012, B=0.010, C=0.008, D=0.010, E=0.011) - every standard row below
    /// agrees with that formula exactly. The SCO ("overcharge") drives do NOT follow the rating
    /// pattern at all, and deriving their constants was wrong by up to 50%:
    ///
    ///     SCO size 5   class5(A) 0.013   class4(B) 0.012   class3(C) 0.012   class2(D) 0.012   class1(E) 0.008
    ///     rating map             0.012             0.010             0.008             0.010             0.011
    ///
    /// Displayed RANGE survived that, because MaxFuelPerJump was back-calculated from the game's
    /// own MaxJumpRange and so re-fitted the LinearConstant/MaxFuelPerJump ratio that range depends
    /// on. Fuel depends on their absolute values, so the pair we send the Spansh exact plotter was
    /// wrong for every SCO drive - the same defect class as the SCO Mk II fix, which this
    /// generalises to all 66 drives.
    ///
    /// Data source: the EDCD/coriolis-data module set, by way of macrossmerrell's Elite/FsdData.cs
    /// (upstream v4.1.0). Validated here against 137 distinct Loadout events from real journals:
    /// every row reproduces the game's own MaxJumpRange to within 0.00003 LY at
    /// mass = UnladenMass + MaxFuelPerJump.
    ///
    /// A second benefit of the table: an UNENGINEERED drive now has an OptimalMass. Previously it
    /// came only from the Loadout engineering modifier, so a stock drive got no fuel model at all,
    /// fell back to MaxJumpRange for display, and could not be plotted with at all (StartSpanshPlot
    /// showed NO SHIP).
    ///
    /// Not listed: "int_missing_hyperdrive", whose stats are all zero. A lookup HIT carrying garbage
    /// is worse than a miss, because a miss falls back cleanly.
    /// </summary>
    public static class FsdModuleData
    {
        public struct Stats
        {
            public readonly double OptimalMass;
            public readonly double MaxFuelPerJump;
            public readonly double LinearConstant;
            public readonly double PowerConstant;

            public Stats(double optimalMass, double maxFuelPerJump, double linearConstant, double powerConstant)
            {
                OptimalMass = optimalMass;
                MaxFuelPerJump = maxFuelPerJump;
                LinearConstant = linearConstant;
                PowerConstant = powerConstant;
            }
        }

        private static readonly Dictionary<string, Stats> Table = new Dictionary<string, Stats>(StringComparer.OrdinalIgnoreCase)
        {
            // ---- Standard size 2 ----
            { "int_hyperdrive_size2_class1", new Stats(48, 0.6, 0.011, 2) },
            { "int_hyperdrive_size2_class2", new Stats(54, 0.6, 0.01, 2) },
            { "int_hyperdrive_size2_class3", new Stats(60, 0.6, 0.008, 2) },
            { "int_hyperdrive_size2_class4", new Stats(75, 0.8, 0.01, 2) },
            { "int_hyperdrive_size2_class5", new Stats(90, 0.9, 0.012, 2) },

            // ---- Standard size 3 ----
            { "int_hyperdrive_size3_class1", new Stats(80, 1.2, 0.011, 2.15) },
            { "int_hyperdrive_size3_class2", new Stats(90, 1.2, 0.01, 2.15) },
            { "int_hyperdrive_size3_class3", new Stats(100, 1.2, 0.008, 2.15) },
            { "int_hyperdrive_size3_class4", new Stats(125, 1.5, 0.01, 2.15) },
            { "int_hyperdrive_size3_class5", new Stats(150, 1.8, 0.012, 2.15) },

            // ---- Standard size 4 ----
            { "int_hyperdrive_size4_class1", new Stats(280, 2, 0.011, 2.3) },
            { "int_hyperdrive_size4_class2", new Stats(315, 2, 0.01, 2.3) },
            { "int_hyperdrive_size4_class3", new Stats(350, 2, 0.008, 2.3) },
            { "int_hyperdrive_size4_class4", new Stats(437.5, 2.5, 0.01, 2.3) },
            { "int_hyperdrive_size4_class5", new Stats(525, 3, 0.012, 2.3) },

            // ---- Standard size 5 ----
            { "int_hyperdrive_size5_class1", new Stats(560, 3.3, 0.011, 2.45) },
            { "int_hyperdrive_size5_class2", new Stats(630, 3.3, 0.01, 2.45) },
            { "int_hyperdrive_size5_class3", new Stats(700, 3.3, 0.008, 2.45) },
            { "int_hyperdrive_size5_class4", new Stats(875, 4.1, 0.01, 2.45) },
            { "int_hyperdrive_size5_class5", new Stats(1050, 5, 0.012, 2.45) },

            // ---- Standard size 6 ----
            { "int_hyperdrive_size6_class1", new Stats(960, 5.3, 0.011, 2.6) },
            { "int_hyperdrive_size6_class2", new Stats(1080, 5.3, 0.01, 2.6) },
            { "int_hyperdrive_size6_class3", new Stats(1200, 5.3, 0.008, 2.6) },
            { "int_hyperdrive_size6_class4", new Stats(1500, 6.6, 0.01, 2.6) },
            { "int_hyperdrive_size6_class5", new Stats(1800, 8, 0.012, 2.6) },

            // ---- Standard size 7 ----
            { "int_hyperdrive_size7_class1", new Stats(1440, 8.5, 0.011, 2.75) },
            { "int_hyperdrive_size7_class2", new Stats(1620, 8.5, 0.01, 2.75) },
            { "int_hyperdrive_size7_class3", new Stats(1800, 8.5, 0.008, 2.75) },
            { "int_hyperdrive_size7_class4", new Stats(2250, 10.6, 0.01, 2.75) },
            { "int_hyperdrive_size7_class5", new Stats(2700, 12.8, 0.012, 2.75) },

            // ---- SCO (overcharge) size 2 ----
            { "int_hyperdrive_overcharge_size2_class1", new Stats(60, 0.6, 0.008, 2.0) },
            { "int_hyperdrive_overcharge_size2_class2", new Stats(90, 0.9, 0.012, 2.0) },
            { "int_hyperdrive_overcharge_size2_class3", new Stats(90, 0.9, 0.012, 2.0) },
            { "int_hyperdrive_overcharge_size2_class4", new Stats(90, 0.9, 0.012, 2.0) },
            { "int_hyperdrive_overcharge_size2_class5", new Stats(100, 1, 0.013, 2.0) },

            // ---- SCO (overcharge) size 3 ----
            { "int_hyperdrive_overcharge_size3_class1", new Stats(100, 1.2, 0.008, 2.15) },
            { "int_hyperdrive_overcharge_size3_class2", new Stats(150, 1.8, 0.012, 2.15) },
            { "int_hyperdrive_overcharge_size3_class3", new Stats(150, 1.8, 0.012, 2.15) },
            { "int_hyperdrive_overcharge_size3_class4", new Stats(150, 1.8, 0.012, 2.15) },
            { "int_hyperdrive_overcharge_size3_class5", new Stats(167, 1.9, 0.013, 2.15) },

            // ---- SCO (overcharge) size 4 ----
            { "int_hyperdrive_overcharge_size4_class1", new Stats(350, 2, 0.008, 2.3) },
            { "int_hyperdrive_overcharge_size4_class2", new Stats(525, 3, 0.012, 2.3) },
            { "int_hyperdrive_overcharge_size4_class3", new Stats(525, 3, 0.012, 2.3) },
            { "int_hyperdrive_overcharge_size4_class4", new Stats(525, 3, 0.012, 2.3) },
            { "int_hyperdrive_overcharge_size4_class5", new Stats(585, 3.2, 0.013, 2.3) },

            // ---- SCO (overcharge) size 5 ----
            { "int_hyperdrive_overcharge_size5_class1", new Stats(700, 3.3, 0.008, 2.45) },
            { "int_hyperdrive_overcharge_size5_class2", new Stats(1050, 5, 0.012, 2.45) },
            { "int_hyperdrive_overcharge_size5_class3", new Stats(1050, 5, 0.012, 2.45) },
            { "int_hyperdrive_overcharge_size5_class4", new Stats(1050, 5, 0.012, 2.45) },
            { "int_hyperdrive_overcharge_size5_class5", new Stats(1175, 5.2, 0.013, 2.45) },

            // ---- SCO (overcharge) size 6 ----
            { "int_hyperdrive_overcharge_size6_class1", new Stats(1200, 5.3, 0.008, 2.6) },
            { "int_hyperdrive_overcharge_size6_class2", new Stats(1800, 8, 0.012, 2.6) },
            { "int_hyperdrive_overcharge_size6_class3", new Stats(1800, 8, 0.012, 2.6) },
            { "int_hyperdrive_overcharge_size6_class4", new Stats(1800, 8, 0.012, 2.6) },
            { "int_hyperdrive_overcharge_size6_class5", new Stats(2000, 8.3, 0.013, 2.6) },

            // ---- SCO (overcharge) size 7 ----
            { "int_hyperdrive_overcharge_size7_class1", new Stats(1800, 8.5, 0.008, 2.75) },
            { "int_hyperdrive_overcharge_size7_class2", new Stats(2700, 12.8, 0.012, 2.75) },
            { "int_hyperdrive_overcharge_size7_class3", new Stats(2700, 12.8, 0.012, 2.75) },
            { "int_hyperdrive_overcharge_size7_class4", new Stats(2700, 12.8, 0.012, 2.75) },
            { "int_hyperdrive_overcharge_size7_class5", new Stats(3000, 13.1, 0.013, 2.75) },

            // ---- SCO (overcharge) size 8 ----
            { "int_hyperdrive_overcharge_size8_class1", new Stats(2800, 13.6, 0.008, 2.90) },
            { "int_hyperdrive_overcharge_size8_class2", new Stats(4200, 20.4, 0.012, 2.90) },
            { "int_hyperdrive_overcharge_size8_class3", new Stats(4200, 20.4, 0.012, 2.90) },
            { "int_hyperdrive_overcharge_size8_class4", new Stats(4200, 20.4, 0.012, 2.90) },
            { "int_hyperdrive_overcharge_size8_class5", new Stats(4670, 20.7, 0.013, 2.90) },
            // SCO Mk II. OptimalMass and MaxFuelPerJump are the published module stats, but the
            // linear and power constants below are OURS, fitted in game, not the published
            // 0.011 / 2.5025 - see ScoMkIILinearConstant and ScoMkIIPowerConstant in EliteData.
            { "int_hyperdrive_overcharge_size8_class5_overchargebooster_mkii", new Stats(4670, 6.8, 0.01107, 2.5) },

        };

        /// <summary>
        /// Base stats for an FSD by its Loadout Item name. Engineering modifiers (FSDOptimalMass,
        /// MaxFuelPerJump) override these where the Loadout supplies them; the constants never move.
        /// Returns false for a drive we have no data for, so the caller can fall back.
        /// </summary>
        public static bool TryGet(string item, out Stats stats)
        {
            if (!string.IsNullOrEmpty(item)) return Table.TryGetValue(item, out stats);
            stats = default;
            return false;
        }
    }
}
