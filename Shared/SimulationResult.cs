using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

public enum EndState
{
    PerfectPass,
    Pass,
    Fail,
    BadFail
}

public class SimulationResult
{
    public class YearScore
    {
        public string ScoreAudit;
        public double ScoreValue;
        public double ScoreValueUnWheighted;
    }
    public int StartYear;
    public int SimYears;

    public double S1Planned;
    public double S2Planned;
    public double S1Actual;
    public double S2Actual;
    public double Legacy;
    public double TotalPlanned;
    public double TotalActualPlusLegacy;
    public double TotalPlanned30;
    public double TotalActualPlusLegacy30;

    public double O1PlannedIncExt;
    public double O2PlannedIncExt;
    public double O3PlannedIncExt;
    public double O1DeliveredIncExt;
    public double O2DeliveredIncExt;
    public double O3DeliveredIncExt;

    public double Bonus;
    public bool Survived;
    public int? DepletionYearIndex; // 1-based within sim window

    public double O1SippMissed { get; set; }
    public double O2SippMissed { get; set; }
    public double O3SippMissed { get; set; }

    public double O1Pct => O1PlannedIncExt > 0 ? O1DeliveredIncExt / O1PlannedIncExt : 1.0;
    public double O2Pct => O2PlannedIncExt > 0 ? O2DeliveredIncExt / O2PlannedIncExt : 1.0;
    public double O3Pct => O3PlannedIncExt > 0 ? O3DeliveredIncExt / O3PlannedIncExt : 1.0;

    public double NetOutcome => TotalActualPlusLegacy - TotalPlanned;

    public double NetOutcome30 => TotalActualPlusLegacy30 - TotalPlanned30;

    // NEW: early cuts (first 10 years) in £ terms
    public double EarlyO2Cuts { get; set; }
    public double EarlyO3Cuts { get; set; }

    // NEW: track SIPP balance at end of each year (for charting and debug)
    public List<(double baseline, double currentBalance)> YearlyTotalBalanceBySimYear = new();
    public List<(double planned, double actual)> YearlyTotalPaymentsBySimYear = new();

    public YearScore Score { get; set; }

    public EndState EndState 
    {
        get
        {
            double o1Pct = O1DeliveredIncExt / O1PlannedIncExt;
            double o2Pct = O2DeliveredIncExt / O2PlannedIncExt;
            double o3Pct = O3DeliveredIncExt / O3PlannedIncExt;

            bool survived = Survived;
            bool netPositive = (TotalActualPlusLegacy - TotalPlanned) >= 0;

            // PerfectPass
            if (survived &&
                o1Pct >= 0.98 &&
                o2Pct >= 0.90 &&
                o3Pct >= 0.85 &&
                netPositive)
                return EndState.PerfectPass;

            // Pass
            if (survived &&
                o1Pct >= 0.95 &&
                o2Pct >= 0.75 &&
                o3Pct >= 0.60 &&
                netPositive)
                return EndState.Pass;

            // BadFail (depleted early)
            if (!survived && DepletionYearIndex < (0.80 * SimYears))
                return EndState.BadFail;

            // Fail (depleted late or survived but underfunded)
            if (!survived)
                return EndState.Fail;

            return EndState.Fail;
        }
    }
}