
using SippBucketDrawdown.Shared;
using System.Reflection.Emit;

namespace SippBucketDrawdown.Engines;

public class Hybrid3BucketEngine : EngineBase, ISimulationEngine
{
    private readonly IRefillStrategy _strategy = new MomentumRefillStrategy();
    private const string EngineVersion = "5.0.0";
    private const string EngineDescription =
        "Hybrid 3 Bucket Engine (B1 time-based, B2/B3 allocation-based, Momentum guardrails + bonus)";

    public SimulationResult Run(
        List<PlanRow> plan,
        List<(int Year, double etf6040_realReturn, double GlobalBonds_realReturn, double GlobalTracker_realReturn)> marketReturns,
        Config config,
        bool verbose,
        double csvS1Total,
        double csvS2Total,
        double csvO1Total,
        double csvO2Total,
        double csvO3Total,
        bool printSummary)
    {
        // ============================================================
        // ALIGN RETURNS TO SIMULATION START YEAR
        // ============================================================

        int startIndex = marketReturns.FindIndex(r => r.Year == config.SimulationStartYear);
        if (startIndex < 0)
            throw new Exception($"Start year {config.SimulationStartYear} not found in return series.");

        marketReturns = marketReturns.Skip(startIndex).ToList();



        // ============================================================
        // INITIALISATION — BUCKETS, TARGETS, ACCUMULATORS
        // ============================================================

        // --- Bucket balances ---
        double s1B1 = 0, s1B2 = 0, s1B3 = 0;
        double s2B1 = 0, s2B2 = 0, s2B3 = 0;
        double liabRate = config.LiabilityDiscountRate;

        // --- Totals for planned vs actual ---
        double s1PlannedTotal = 0, s1ActualTotal = 0;
        double s2PlannedTotal = 0, s2ActualTotal = 0;
        double PlannedTotal30 = 0, ActualPlusLegacyTotal30 = 0;

        // --- Objective accounting ---
        double o1PlannedTotalIncExt = 0, o2PlannedTotalIncExt = 0, o3PlannedTotalIncExt = 0;
        double o1ExternalTotal = 0, o2ExternalTotal = 0, o3ExternalTotal = 0;
        double o1SippPlannedTotal = 0, o2SippPlannedTotal = 0, o3SippPlannedTotal = 0;
        double o1SippActualTotal = 0, o2SippActualTotal = 0, o3SippActualTotal = 0;

        // --- Missed SIPP funding ---
        double s1O1D = 0, s1O2D = 0, s1O3D = 0;
        double s2O1D = 0, s2O2D = 0, s2O3D = 0;

        // --- Bonus + base withdrawals ---
        double totalBonus = 0;
        double sippWithdrawalsBaseTotal = 0;

        // --- Early cuts (first 10 years) ---
        double earlyO2Cuts = 0, earlyO3Cuts = 0;

        // --- Depletion ---
        int? depletionYearIndex = null;

        // ============================================================
        // VERBOSE DEBUG HEADER
        // ============================================================

        if (verbose)
        {
            Console.WriteLine($"\n=== Hybrid3BucketEngine DEBUG - v{EngineVersion} - {EngineDescription} ===");
            Console.WriteLine(
                "Year | rBond | rEquity | " +
                "S1PotStYr | S1FR | " +
                "S1BaseLineLower | S1BaseLine | S1BaseLineUpper | " +
                "S1B1StYr | S1B2StYr | S1B2Health | S1B3StYr | S1B3Health | " +
                "S1_GuardRails |" +
                "S1_O1P | S1_O1A | S1_O2P | S1_O2A | S1_O3P | S1_O3A | S1_Bonus | " +
                "S1_WdrawB1 | S1_WdrawB2 | S1_WdrawB3 |" +
                "S1_B1RefillFromB2 | S1_B1RefillFromB3|" +
                "S1_B2Allocation | S1_B2RebalanceFromB3 | S1_B3Allocation | S1_B3RebalanceFromB2 ||" +

                "S2Pot | S2FR | " +
                "S2BaseLineLower | S2BaseLine | S2BaseLineUpper | " +
                "S2B1StYr | S2B2StYr | S2B2Health | S2B3StYr | S2B3Health | " +
                "S2_GuardRails |" +
                "S2_O1P | S2_O1A | S2_O2P | S2_O2A | S2_O3P | S2_O3A | " +
                "S2_Bonus |" +
                "S2_WdrawB1 | S2_WdrawB2 | S2_WdrawB3 |" +
                "S2_B1RefillFromB2 | S2_B1RefillFromB3 |" +
                "S2_B2Allocation | S2_B2RebalanceFromB3 | S2_B3Allocation | S2_B3RebalanceFromB2 | |" +

                "Year| LowerGuard | BaseLine | ActualValue | UpperGuard | " +
                "Notes");
            Console.WriteLine(new string('-', 260));
        }

        // ============================================================
        // INITIAL BUCKET ALLOCATION (Year 0)
        // ============================================================
        List<(double planned, double actual)> yearlyTotalPaymentsBySimYear = new();
        List<(double baseline, double balance)> annualTotalBalanceBySimYear = new();
        InitialiseHybridBuckets(
            config,
            plan,
            out s1B1, out s1B2, out s1B3,
            out s2B1, out s2B2, out s2B3);

        var s1PV = ComputePVFromYearIndex(1, plan, 0, liabRate);
        var s2PV = ComputePVFromYearIndex(2, plan, 0, liabRate);
        annualTotalBalanceBySimYear.Add((s1PV + s2PV, s1B1 + s1B2 + s1B3 + s2B1 + s2B2 + s2B3));

        // ============================================================
        // MAIN YEAR LOOP
        // ============================================================
        int simYears = Math.Min(plan.Count, marketReturns.Count);
        for (int yearIndex = 0; yearIndex < simYears; yearIndex++)
        {
            var notes = new List<string>();
            double s1Withdrawn = 0, s2Withdrawn = 0;
            double s1Remain = 0, s2Remain = 0;

            // Per‑year debug metrics (per SIPP, per objective)
            double s1O1PlannedYear = 0, s1O2PlannedYear = 0, s1O3PlannedYear = 0;
            double s2O1PlannedYear = 0, s2O2PlannedYear = 0, s2O3PlannedYear = 0;

            double s1O1ActualYear = 0, s1O2ActualYear = 0, s1O3ActualYear = 0;
            double s2O1ActualYear = 0, s2O2ActualYear = 0, s2O3ActualYear = 0;

            // Start of year bucket values
            double s1B1StartYr = s1B1, s1B2StartYr = s1B2, s1B3StartYr = s1B3;
            double s2B1StartYr = s2B1, s2B2StartYr = s2B2, s2B3StartYr = s2B3;

            double s1BonusYear = 0, s2BonusYear = 0;

            // Baseline (RL) for debug
            double s1Baseline = 0, s2Baseline = 0;

            // --------------------------------------------------------
            // BLOCK A — BASELINE, HEALTH, TARGETS
            // --------------------------------------------------------

            // === Extract plan row ===
            var p = plan[yearIndex];
            double s1Spend = p.S1Spend;
            double s2Spend = p.S2Spend;

            // === Extract market returns ===
            double rBond = marketReturns[yearIndex].GlobalBonds_realReturn;
            double rEquity = marketReturns[yearIndex].GlobalTracker_realReturn;

            // === Shortcuts to config ===
            var hcfg = config.Hybrid;

            // ------------------------------------------------------------
            // S1 BASELINE + HEALTH + TARGETS
            // ------------------------------------------------------------
            double s1Pot = s1B1 + s1B2 + s1B3;

            // --- Remaining liability RL ---
            double s1RL = ComputePVFromYearIndex(1, plan, yearIndex, liabRate);
            s1Baseline = s1RL;

            // --- Overall health ---
            double s1H = (s1RL > 0) ? (s1Pot / s1RL) : 1.0;

            // --- B1 target (rolling 3 years) ---
            double s1B1Target = 0;
            for (int k = yearIndex; k <= yearIndex + 2 && k < plan.Count; k++)
                s1B1Target += plan[k].S1Spend;

            // --- Risk liability (years t+3 onward) ---
            double s1RiskLiab = 0;
            for (int k = yearIndex + 3; k < plan.Count; k++)
            {
                double yrs = k - yearIndex;
                s1RiskLiab += plan[k].S1Spend / Math.Pow(1 + liabRate, yrs);
            }

            // --- TRUE B2/B3 liability targets (per‑year discounted) ---
            double s1B2Liab = 0;
            double s1B3Liab = 0;

            for (int k = yearIndex + 3; k < plan.Count; k++)
            {
                double yrs = k - yearIndex;
                double discounted = plan[k].S1Spend / Math.Pow(1 + liabRate, yrs);

                s1B2Liab += hcfg.AllocationB2 * discounted;
                s1B3Liab += hcfg.AllocationB3 * discounted;
            }

            // --- Apply health discount adjustments ---
            s1B2Liab /= (1 + hcfg.B2HealthDiscountAdjustment);
            s1B3Liab /= (1 + hcfg.B3HealthDiscountAdjustment);

            // --- B2/B3 health ---
            double s1B2Health = (s1B2Liab > 0) ? (s1B2 / s1B2Liab) : 1.0;
            double s1B3Health = (s1B3Liab > 0) ? (s1B3 / s1B3Liab) : 1.0;

            // --- Allocation targets for rebalancing ---
            double s1RiskPot = s1B2 + s1B3;
            double s1TargetB2 = hcfg.AllocationB2 * s1RiskPot;
            double s1TargetB3 = hcfg.AllocationB3 * s1RiskPot;


            // ------------------------------------------------------------
            // S2 BASELINE + HEALTH + TARGETS
            // ------------------------------------------------------------
            double s2Pot = s2B1 + s2B2 + s2B3;

            // --- Remaining liability RL ---
            double s2RL = ComputePVFromYearIndex(2, plan, yearIndex, liabRate);
            s2Baseline = s2RL;

            // --- Overall health ---
            double s2H = (s2RL > 0) ? (s2Pot / s2RL) : 1.0;

            // --- B1 target (rolling 3 years) ---
            double s2B1Target = 0;
            for (int k = yearIndex; k <= yearIndex + 2 && k < plan.Count; k++)
                s2B1Target += plan[k].S2Spend;

            // --- Risk liability (years t+3 onward) ---
            double s2RiskLiab = 0;
            for (int k = yearIndex + 3; k < plan.Count; k++)
            {
                double yrs = k - yearIndex;
                s2RiskLiab += plan[k].S2Spend / Math.Pow(1 + liabRate, yrs);
            }

            // --- TRUE B2/B3 liability targets (per‑year discounted) ---
            double s2B2Liab = 0;
            double s2B3Liab = 0;

            for (int k = yearIndex + 3; k < plan.Count; k++)
            {
                double yrs = k - yearIndex;
                double discounted = plan[k].S2Spend / Math.Pow(1 + liabRate, yrs);

                s2B2Liab += hcfg.AllocationB2 * discounted;
                s2B3Liab += hcfg.AllocationB3 * discounted;
            }

            // --- Apply health discount adjustments ---
            s2B2Liab /= (1 + hcfg.B2HealthDiscountAdjustment);
            s2B3Liab /= (1 + hcfg.B3HealthDiscountAdjustment);

            // --- B2/B3 health ---
            double s2B2Health = (s2B2Liab > 0) ? (s2B2 / s2B2Liab) : 1.0;
            double s2B3Health = (s2B3Liab > 0) ? (s2B3 / s2B3Liab) : 1.0;

            // --- Allocation targets for rebalancing ---
            double s2RiskPot = s2B2 + s2B3;
            double s2TargetB2 = hcfg.AllocationB2 * s2RiskPot;
            double s2TargetB3 = hcfg.AllocationB3 * s2RiskPot;


            // ------------------------------------------------------------
            // DEBUG NOTES
            // ------------------------------------------------------------
            if (verbose)
            {
                notes.Add($"S1:RL={s1RL:F0},H={s1H:F2},H2={s1B2Health:F2},H3={s1B3Health:F2}");
                notes.Add($"S2:RL={s2RL:F0},H={s2H:F2},H2={s2B2Health:F2},H3={s2B3Health:F2}");
            }


            // --------------------------------------------------------
            // BLOCK B — OBJECTIVE ALLOCATION (external vs SIPP)
            // --------------------------------------------------------
            double s1Target = 0, s2Target = 0;

            AllocateHybridObjectiveFunding(
                p.O1, p.O2, p.O3,
                p.S1Spend, p.S2Spend,
                ref o1PlannedTotalIncExt, ref o2PlannedTotalIncExt, ref o3PlannedTotalIncExt,
                ref o1ExternalTotal, ref o2ExternalTotal, ref o3ExternalTotal,
                ref o1SippPlannedTotal, ref o2SippPlannedTotal, ref o3SippPlannedTotal,
                ref s1PlannedTotal, ref s2PlannedTotal,
                out double o1S1, out double o2S1, out double o3S1,
                out double o1S2, out double o2S2, out double o3S2,
                out s1Target, out s2Target,
                notes);
            
            if (yearIndex == 29) PlannedTotal30 = s1PlannedTotal + s2PlannedTotal;

            // Per‑year planned (per SIPP, per objective) for debug
            s1O1PlannedYear = o1S1;
            s1O2PlannedYear = o2S1;
            s1O3PlannedYear = o3S1;

            s2O1PlannedYear = o1S2;
            s2O2PlannedYear = o2S2;
            s2O3PlannedYear = o3S2;


            // --------------------------------------------------------
            // BLOCK C — GUARDRAILS (Momentum-style, shared with Momentum engine)
            // --------------------------------------------------------

            // --- S1 portions (from Block B) ---
            double s1O1Portion = o1S1;
            double s1O2Portion = o2S1;
            double s1O3Portion = o3S1;

            // --- S2 portions ---
            double s2O1Portion = o1S2;
            double s2O2Portion = o2S2;
            double s2O3Portion = o3S2;

            // --- Previous return (Momentum uses equity return) ---
            double prevReturn = (yearIndex > 0)
                ? marketReturns[yearIndex - 1].GlobalTracker_realReturn
                : 0;

            // per‑year cuts
            double s1CutO1 = 0, s1CutO2 = 0, s1CutO3 = 0;
            double s2CutO1 = 0, s2CutO2 = 0, s2CutO3 = 0;

            // S1
            ApplyFundingRatioBasedGuardrailsAndDebt(
                s1H,
                s1B1,
                s1O1Portion,
                s1O2Portion,
                s1O3Portion,
                s1Target,
                ref s1Target,
                ref s1CutO1,
                ref s1CutO2,
                ref s1CutO3,
                notes,
                "S1",
                prevReturn,
                yearIndex);

            var S1_GuardRails = s1CutO1 + s1CutO2 + s1CutO3;

            // S2
            ApplyFundingRatioBasedGuardrailsAndDebt(
                s2H,
                s2B1,
                s2O1Portion,
                s2O2Portion,
                s2O3Portion,
                s2Target,
                ref s2Target,
                ref s2CutO1,
                ref s2CutO2,
                ref s2CutO3,
                notes,
                "S2",
                prevReturn,
                yearIndex);

            var S2_GuardRails = s2CutO1 + s2CutO2 + s2CutO3;

            // accumulate missed SIPP funding (totals)
            s1O1D += s1CutO1; s1O2D += s1CutO2; s1O3D += s1CutO3;
            s2O1D += s2CutO1; s2O2D += s2CutO2; s2O3D += s2CutO3;

            // early‑cuts (first 10 years) – same pattern as other engines
            if (yearIndex < 10)
            {
                earlyO2Cuts += (s1CutO2 + s2CutO2);
                earlyO3Cuts += (s1CutO3 + s2CutO3);
            }


            // --------------------------------------------------------
            // BLOCK D — BONUS (Momentum-style, explicit + separate)
            // --------------------------------------------------------

            // Base SIPP targets after guardrails, before bonus
            double s1Base = s1Target;
            double s2Base = s2Target;

            // Apply bonus (mutates s1Target / s2Target by ref)
            s1BonusYear = ApplyHybridBonus(
                s1H,
                s1B1 + s1B2 + s1B3,   // pot value
                s1Pot - s1RL,         // surplus
                ref s1Target,
                "S1",
                notes);

            s2BonusYear = ApplyHybridBonus(
                s2H,
                s2B1 + s2B2 + s2B3,
                s2Pot - s2RL,
                ref s2Target,
                "S2",
                notes);

            // Global accounting (same pattern as Momentum / TimeBuckets)
            totalBonus += (s1BonusYear + s2BonusYear);
            sippWithdrawalsBaseTotal += (s1Base + s2Base);

            // --------------------------------------------------------
            // BLOCK E — WITHDRAWALS (Hybrid A/B/C/D routing)
            // --------------------------------------------------------
            (double totalWithdrawal, double B1Withdrawn, double B2Withdrawn, double B3Withdrawn) s1WithdrawDetails = (0, 0, 0, 0);
            (double totalWithdrawal, double B1Withdrawn, double B2Withdrawn, double B3Withdrawn) s2WithdrawDetails = (0, 0, 0, 0);
            double s1Unmet = 0, s2Unmet = 0;
            s1WithdrawDetails = ApplyHybridWithdrawals(
                ref s1B1, ref s1B2, ref s1B3,
                s1Target,
                s1B2Health, s1B3Health,
                config,
                "S1",
                notes,
                out s1Withdrawn,
                out s1Unmet
            );
            s1Remain = s1Pot - s1Withdrawn;
            s1ActualTotal += s1Withdrawn;

            s2WithdrawDetails= ApplyHybridWithdrawals(
                ref s2B1, ref s2B2, ref s2B3,
                s2Target,
                s2B2Health, s2B3Health,
                config,
                "S2",
                notes,
                out s2Withdrawn,
                out s2Unmet
            );
            s2Remain = s2Pot - s2Withdrawn;
            s2ActualTotal += s2Withdrawn;

            if (yearIndex == 29) ActualPlusLegacyTotal30 = s1ActualTotal + s2ActualTotal + s1B1 + s1B2 + s1B3 + s2B1 + s2B2 + s2B3;

            // --------------------------------------------------------
            // OBJECTIVE-LEVEL SIPP ACTUALS (planned minus cuts)
            // --------------------------------------------------------

            // Per-SIPP, per-objective actuals this year (SIPP-funded only)
            s1O1ActualYear = o1S1 - s1CutO1;
            s1O2ActualYear = o2S1 - s1CutO2;
            s1O3ActualYear = o3S1 - s1CutO3;

            s2O1ActualYear = o1S2 - s2CutO1;
            s2O2ActualYear = o2S2 - s2CutO2;
            s2O3ActualYear = o3S2 - s2CutO3;

            // Accumulate SIPP objective actual totals (same pattern as other engines)
            o1SippActualTotal += (s1O1ActualYear + s2O1ActualYear);
            o2SippActualTotal += (s1O2ActualYear + s2O2ActualYear);
            o3SippActualTotal += (s1O3ActualYear + s2O3ActualYear);

            // --------------------------------------------------------
            // PER-YEAR OBJECTIVE PLANNED / ACTUAL / PERCENT (for debug)
            // --------------------------------------------------------
            double o1PlannedYear = p.O1;
            double o2PlannedYear = p.O2;
            double o3PlannedYear = p.O3;

            // SIPP-funded portions this year
            double o1SippYear = o1S1 + o1S2;
            double o2SippYear = o2S1 + o2S2;
            double o3SippYear = o3S1 + o3S2;

            // External portions this year
            double o1ExternalYear = o1PlannedYear - o1SippYear;
            double o2ExternalYear = o2PlannedYear - o2SippYear;
            double o3ExternalYear = o3PlannedYear - o3SippYear;

            // Actual delivered this year (external + SIPP actuals)
            // SIPP actuals are planned minus cuts, not withdrawal-proportional
            double o1SippActualYear = s1O1ActualYear + s2O1ActualYear;
            double o2SippActualYear = s1O2ActualYear + s2O2ActualYear;
            double o3SippActualYear = s1O3ActualYear + s2O3ActualYear;

            double o1ActualYear = o1ExternalYear + o1SippActualYear;
            double o2ActualYear = o2ExternalYear + o2SippActualYear;
            double o3ActualYear = o3ExternalYear + o3SippActualYear;

            // Percentages (for debug only)
            double o1PctYear = (o1PlannedYear > 0.01) ? (o1ActualYear / o1PlannedYear) : 1.0;
            double o2PctYear = (o2PlannedYear > 0.01) ? (o2ActualYear / o2PlannedYear) : 1.0;
            double o3PctYear = (o3PlannedYear > 0.01) ? (o3ActualYear / o3PlannedYear) : 1.0;

            // --------------------------------------------------------
            // RESCUE LOGIC (S2 → S1 only)
            // --------------------------------------------------------
            if (s2Unmet > 0.01)
            {
                // S1 can rescue S2 using remaining assets
                double s1RescueCapacity = s1B1 + s1B2 + s1B3;

                if (s1RescueCapacity > 0.01)
                {
                    ApplyHybridWithdrawals(
                        ref s1B1, ref s1B2, ref s1B3,
                        s2Unmet,                    // rescue exactly what S2 lacks
                        s1B2Health,
                        s1B3Health,
                        config,
                        "S1Rescue",
                        notes,
                        out double rescued,
                        out double rescueUnmet
                    );

                    // Rescue counts as S1 spending and reduces S2 unmet.
                    s1Withdrawn += rescued;
                    s1ActualTotal += rescued;
                    s2Unmet -= rescued;

                    notes.Add($"S2:RescueFromS1={rescued:F0}");
                }
            }

            // Final unmet tracking
            if (s1Unmet > 0.01)
                notes.Add($"S1:Unmet={s1Unmet:F0}");

            if (s2Unmet > 0.01)
                notes.Add($"S2:Unmet={s2Unmet:F0}");

            // --------------------------------------------------------
            // BLOCK F — REFILL B1
            //         - We are refilling to match spend for X years from next year onwards.
            // --------------------------------------------------------

            // --- Momentum-based refill decision (shared logic with Momentum engine, but different input metric: blended market return) ---
            (double S1B1_RefillFromB2, double S1B1_RefillFromB3) S1_Refill = (0, 0);
            (double S2B1_RefillFromB2, double S2B1_RefillFromB3) S2_Refill = (0, 0);
            double latestAppliedGlobalBondReturn = (yearIndex == 0) ? 0.02 : marketReturns[yearIndex-1].GlobalBonds_realReturn;
            double latestAppliedGlobalTrackerReturn = (yearIndex == 0) ? 0.01 : marketReturns[yearIndex-1].GlobalTracker_realReturn;
            if (_strategy.ShouldRefill(latestAppliedGlobalBondReturn * config.Hybrid.AllocationB2 + latestAppliedGlobalTrackerReturn * config.Hybrid.AllocationB3))
            {
                // --- S1: compute exactly what to refill ---
                var healthForRefillS1 = (yearIndex == 0) ? config.Momentum.CashBufferHealthThreshold + 0.01 : s1H;
                int s1RefillYears = _strategy.GetRefillYears(healthForRefillS1, config);
                double s1TargetCash = plan.Skip(yearIndex + 1).Take(s1RefillYears).Sum(x => x.S1Spend);
                double s1Refill = Math.Max(0, s1TargetCash - s1B1);
                
                // --- S1: decide where to refill from ---
                if (s1Refill > 0.0)
                {
                    if (latestAppliedGlobalTrackerReturn > latestAppliedGlobalBondReturn)
                    {
                        // Equities up relative to bonds, so refill from B3.
                        S1_Refill.S1B1_RefillFromB3 = Math.Min(s1Refill, s1B3);
                        if (S1_Refill.S1B1_RefillFromB3 > 0)
                        {
                            s1B3 -= S1_Refill.S1B1_RefillFromB3;
                            s1B1 += S1_Refill.S1B1_RefillFromB3;
                            notes.Add($"S1:Refill{s1RefillYears}Yr_B3_{S1_Refill.S1B1_RefillFromB3:F0}");
                        }
                    }
                    else
                    {
                        // Bonds up relative to equities, so refill from B2.
                        S1_Refill.S1B1_RefillFromB2 = Math.Min(s1Refill, s1B2);
                        if (S1_Refill.S1B1_RefillFromB2 > 0)
                        {
                            s1B2 -= S1_Refill.S1B1_RefillFromB2;
                            s1B1 += S1_Refill.S1B1_RefillFromB2;
                            notes.Add($"S1:Refill{s1RefillYears}Yr_B2_{S1_Refill.S1B1_RefillFromB2:F0}");
                        }
                    }
                }
                else                
                {
                    notes.Add($"S1:Refill{s1RefillYears}Yr_NoRefill_CashSufficient");
                }

                // --- S2: compute exactly what to refill ---
                var healthForRefillS2 = (yearIndex == 0) ? config.Momentum.CashBufferHealthThreshold + 0.01 : s2H;
                int s2RefillYears = _strategy.GetRefillYears(healthForRefillS2, config);
                double s2TargetCash = plan.Skip(yearIndex + 1).Take(s2RefillYears).Sum(x => x.S2Spend);
                double s2B1Refill = Math.Max(0, s2TargetCash - s2B1);

                // --- S2: decide where to refill from ---
                if (s2B1Refill > 0.0)
                {
                    if (latestAppliedGlobalTrackerReturn > latestAppliedGlobalBondReturn)
                    {
                        // Equities up relative to bonds, so refill from B3.
                        S2_Refill.S2B1_RefillFromB3 = Math.Min(s2B1Refill, s2B3);
                        if (S2_Refill.S2B1_RefillFromB3 > 0)
                        {
                            s2B3 -= S2_Refill.S2B1_RefillFromB3;
                            s2B1 += S2_Refill.S2B1_RefillFromB3;
                            notes.Add($"S2:Refill{s2RefillYears}Yr_B3_{S2_Refill.S2B1_RefillFromB3:F0}");
                        }
                    }
                    else
                    {
                        // Bonds up relative to equities, so refill from B2.
                        S2_Refill.S2B1_RefillFromB2 = Math.Min(s2B1Refill, s2B2);
                        if (S2_Refill.S2B1_RefillFromB2 > 0)
                        {
                            s2B2 -= S2_Refill.S2B1_RefillFromB2;
                            s2B1 += S2_Refill.S2B1_RefillFromB2;
                            notes.Add($"S2:Refill{s2RefillYears}Yr_B2_{S2_Refill.S2B1_RefillFromB2:F0}");
                        }
                    }
                }
                else
                {
                    notes.Add($"S2:Refill{s2RefillYears}Yr_NoRefill_CashSufficient");
                }
            }
            else
            {
                notes.Add($"No B1 cash Refill: PrevMktReturn=Bond{latestAppliedGlobalBondReturn:P2}-Equity{latestAppliedGlobalTrackerReturn:P2} below threshold");
            }


            // --------------------------------------------------------
            // BLOCK F — Rebalance B2/B3 to targets (Hybrid)
            // --------------------------------------------------------

            // Rebalance S1 B2/B3
            var s1ReblanceResult = ApplyHybridRebalance(ref s1B2, ref s1B3, s1B2Health, s1B3Health, notes, "S1");

            // Rebalance S2 B2/B3
            var s2ReblanceResult = ApplyHybridRebalance(ref s2B2, ref s2B3, s2B2Health, s2B3Health, notes, "S2");


            // --------------------------------------------------------
            // BLOCK G — APPLY RETURNS
            // --------------------------------------------------------

            // S1 returns
            double s1B2_before = s1B2;
            double s1B3_before = s1B3;

            s1B2 *= (1.0 + rBond);
            s1B3 *= (1.0 + rEquity);

            notes.Add($"S1:Returns_B2={s1B2_before:F0}->{s1B2:F0}");
            notes.Add($"S1:Returns_B3={s1B3_before:F0}->{s1B3:F0}");

            // S2 returns
            double s2B2_before = s2B2;
            double s2B3_before = s2B3;

            s2B2 *= (1.0 + rBond);
            s2B3 *= (1.0 + rEquity);

            notes.Add($"S2:Returns_B2={s2B2_before:F0}->{s2B2:F0}");
            notes.Add($"S2:Returns_B3={s2B3_before:F0}->{s2B3:F0}");

            // --------------------------------------------------------
            // DEBUG OUTPUT
            // --------------------------------------------------------
            if (verbose)
            {
                int year = config.SimulationStartYear + yearIndex;
                var displayYear = $"{yearIndex + 1} ({year})";

                notes.Add($"S1Cuts(O1,O2,O3)=({s1O1D:F0},{s1O2D:F0},{s1O3D:F0})");
                notes.Add($"S2Cuts(O1,O2,O3)=({s2O1D:F0},{s2O2D:F0},{s2O3D:F0})");
                notes.Add($"S1Cash={s1B1:F0}");
                notes.Add($"S2Cash={s2B1:F0}");
                notes.Add($"prevReturn={prevReturn:F3}");

                // Make bucket health output as decimal!!!!!
                string row =
                    $"{displayYear} |" +
                    $"{rBond,6:P2} | {rEquity,6:P2} | " +
                    $"{s1Pot,8:N0} | {s1H,4:F2} | " +
                    $"{s1Baseline * 0.8,8:N0} | {s1Baseline,8:N0} | {s1Baseline * 1.2,8:N0} | " +
                    $"{s1B1StartYr,8:N0} | {s1B2StartYr,8:N0} | {s1B2Health:F2} | {s1B3StartYr,8:N0} | {s1B3Health:F2} |" +
                    $"{S1_GuardRails,8:N0} | " +
                    $"{s1O1PlannedYear,8:N0} | {s1O1ActualYear,8:N0} | " +
                    $"{s1O2PlannedYear,8:N0} | {s1O2ActualYear,8:N0} | " +
                    $"{s1O3PlannedYear,8:N0} | {s1O3ActualYear,8:N0} | " +
                    $"{s1BonusYear,8:N0} | " +
                    $"{s1WithdrawDetails.B1Withdrawn,6:N0} | {s1WithdrawDetails.B2Withdrawn,6:N0} | {s1WithdrawDetails.B3Withdrawn,6:N0} |" +
                    $"{S1_Refill.S1B1_RefillFromB2,8:N0} | {S1_Refill.S1B1_RefillFromB3,8:N0} |" +
                    $"{s1ReblanceResult.B2PercentAllocation,6:N0} | {s1ReblanceResult.RebalanceB2FromB3Total,6:N0} | {s1ReblanceResult.B3PercentAllocation,6:N0} | {s1ReblanceResult.RebalanceB3FromB2Total,6:N0} ||" +

                    $"{s2Pot,8:N0} | {s2H,4:F2} | " +
                    $"{s2Baseline * 0.8,8:N0} | {s2Baseline,8:N0} | {s2Baseline * 1.2,8:N0} | " +
                    $"{s2B1StartYr,8:N0} | {s2B2StartYr,8:N0} | {s2B2Health:F2} | {s2B3StartYr,8:N0} | {s2B3Health:F2} | " +
                    $"{S2_GuardRails,8:N0} | " +
                    $"{s2O1PlannedYear,8:N0} | {s2O1ActualYear,8:N0} | " +
                    $"{s2O2PlannedYear,8:N0} | {s2O2ActualYear,8:N0} | " +
                    $"{s2O3PlannedYear,8:N0} | {s2O3ActualYear,8:N0} | " +
                    $"{s2BonusYear,8:N0} | " +
                    $"{s2WithdrawDetails.B1Withdrawn,6:N0} | {s2WithdrawDetails.B2Withdrawn,6:N0} | {s2WithdrawDetails.B3Withdrawn,6:N0} |" +
                    $"{S2_Refill.S2B1_RefillFromB2,8:N0} | {S2_Refill.S2B1_RefillFromB3,8:N0} |" +
                    $"{s2ReblanceResult.B2PercentAllocation,6:N0} | {s2ReblanceResult.RebalanceB2FromB3Total,6:N0} | {s2ReblanceResult.B3PercentAllocation,6:N0} | {s2ReblanceResult.RebalanceB3FromB2Total,6:N0} | |" +

                    $"{displayYear} | {(s1Baseline + s2Baseline) * 0.8,8:N0} | {(s1Baseline + s2Baseline),8:N0} | {s1Pot + s2Pot,8:N0} | {(s1Baseline + s2Baseline) * 1.2,8:N0} | " +
                    $"{string.Join(" | ", notes)}";

                Console.WriteLine(row);
            }
            // === DEPLETION CHECK ===
            double totalPot = s1B1 + s1B2 + s1B3 + s2B1 + s2B2 + s2B3;
            if (depletionYearIndex == null && totalPot <= 1.0)
                depletionYearIndex = yearIndex + 1;

            // Track total balance by simulation year (for debug / analysis)
            double comingYearS1PV = ComputePVFromYearIndex(1, plan, yearIndex + 1, liabRate);
            double comingYearS2PV = ComputePVFromYearIndex(2, plan, yearIndex + 1, liabRate);
            annualTotalBalanceBySimYear.Add((comingYearS1PV + comingYearS2PV, totalPot));

            //var plannedAnnualSpend = o1PlannedYear + o2PlannedYear + o3PlannedYear;
            //var actualAnnualSpend = o1ActualYear + o2ActualYear + o3ActualYear + s1BonusYear + s2BonusYear;
            var totalPlannedSpend = s1O1PlannedYear + s1O2PlannedYear + s1O3PlannedYear + s2O1PlannedYear + s2O2PlannedYear + s2O3PlannedYear;
            var totalActualSpend = s1O1ActualYear + s1O2ActualYear + s1O3ActualYear + s2O1ActualYear + s2O2ActualYear + s2O3ActualYear + s1BonusYear + s2BonusYear;
            yearlyTotalPaymentsBySimYear.Add((totalPlannedSpend, totalActualSpend));
        }

        // ============================================================
        // END OF SIMULATION — SUMMARY + RESULT
        // ============================================================

        double s1End = s1B1 + s1B2 + s1B3;
        double s2End = s2B1 + s2B2 + s2B3;
        double legacy = s1End + s2End;

        double totalPlanned = s1PlannedTotal + s2PlannedTotal;
        double totalActualPlusLegacy = s1ActualTotal + s2ActualTotal + legacy;

        var result = new SimulationResult
        {
            StartYear = config.SimulationStartYear,
            SimYears = simYears,

            S1Planned = s1PlannedTotal,
            S2Planned = s2PlannedTotal,
            S1Actual = s1ActualTotal,
            S2Actual = s2ActualTotal,

            TotalPlanned30 = PlannedTotal30,
            TotalActualPlusLegacy30 = ActualPlusLegacyTotal30,

            Legacy = legacy,
            TotalPlanned = totalPlanned,
            TotalActualPlusLegacy = totalActualPlusLegacy,

            O1PlannedIncExt = o1PlannedTotalIncExt,
            O2PlannedIncExt = o2PlannedTotalIncExt,
            O3PlannedIncExt = o3PlannedTotalIncExt,

            O1DeliveredIncExt = o1ExternalTotal + o1SippActualTotal,
            O2DeliveredIncExt = o2ExternalTotal + o2SippActualTotal,
            O3DeliveredIncExt = o3ExternalTotal + o3SippActualTotal,

            O1SippMissed = s1O1D + s2O1D,
            O2SippMissed = s1O2D + s2O2D,
            O3SippMissed = s1O3D + s2O3D,

            Bonus = totalBonus,
            Survived = depletionYearIndex == null,
            DepletionYearIndex = depletionYearIndex,

            EarlyO2Cuts = earlyO2Cuts,
            EarlyO3Cuts = earlyO3Cuts,

            YearlyTotalBalanceBySimYear = annualTotalBalanceBySimYear,
            YearlyTotalPaymentsBySimYear = yearlyTotalPaymentsBySimYear
        };

        if (printSummary)
        {
            PrintSummary(
                s1PlannedTotal, s1ActualTotal,
                s2PlannedTotal, s2ActualTotal,
                s1End, s2End,
                csvO1Total, csvO2Total, csvO3Total,
                o1PlannedTotalIncExt, o2PlannedTotalIncExt, o3PlannedTotalIncExt,
                o1ExternalTotal, o2ExternalTotal, o3ExternalTotal,
                o1SippPlannedTotal, o2SippPlannedTotal, o3SippPlannedTotal,
                o1SippActualTotal, o2SippActualTotal, o3SippActualTotal,
                totalBonus,
                sippWithdrawalsBaseTotal,
                result.EndState
            );
        }

        return result;
    }

    private void InitialiseHybridBuckets(
    Config config,
    List<PlanRow> plan,
    out double s1B1, out double s1B2, out double s1B3,
    out double s2B1, out double s2B2, out double s2B3)
    {
        var hcfg = config.Hybrid;
        double liabRate = config.LiabilityDiscountRate;

        // --- S1 ---
        double s1Pot = config.Sipp1StartingBalance;

        // B1 = next 3 years of spend
        double s1B1Target = 0;
        for (int k = 0; k <= 2 && k < plan.Count; k++)
            s1B1Target += plan[k].S1Spend;

        // Risk liability = years 3+
        double s1RiskLiab = 0;
        for (int k = 3; k < plan.Count; k++)
            s1RiskLiab += plan[k].S1Spend / Math.Pow(1 + liabRate, k-3);

        // B2/B3 targets
        double s1B2Target = hcfg.AllocationB2 * (s1Pot - s1B1Target);
        double s1B3Target = hcfg.AllocationB3 * (s1Pot - s1B1Target);

        s1B1 = Math.Min(s1B1Target, s1Pot);
        double remaining1 = s1Pot - s1B1;

        s1B2 = Math.Max(0, Math.Min(s1B2Target, remaining1));
        s1B3 = Math.Max(0, remaining1 - s1B2);

        // --- S2 ---
        double s2Pot = config.Sipp2StartingBalance;

        double s2B1Target = 0;
        for (int k = 0; k <= 2 && k < plan.Count; k++)
            s2B1Target += plan[k].S2Spend;

        double s2RiskLiab = 0;
        for (int k = 3; k < plan.Count; k++)
            s2RiskLiab += plan[k].S2Spend / Math.Pow(1 + liabRate, k-3);

        double s2B2Target = hcfg.AllocationB2 * (s2Pot - s2B1Target);
        double s2B3Target = hcfg.AllocationB3 * (s2Pot - s2B1Target);

        s2B1 = Math.Min(s2B1Target, s2Pot);
        double remaining2 = s2Pot - s2B1;

        s2B2 = Math.Max(0, Math.Min(s2B2Target, remaining2));
        s2B3 = Math.Max(0, remaining2 - s2B2);
    }

    private void AllocateHybridObjectiveFunding(
    double O1, double O2, double O3,
    double S1Spend, double S2Spend,
    ref double o1PlannedTotalIncExt, ref double o2PlannedTotalIncExt, ref double o3PlannedTotalIncExt,
    ref double o1ExternalTotal, ref double o2ExternalTotal, ref double o3ExternalTotal,
    ref double o1SippPlannedTotal, ref double o2SippPlannedTotal, ref double o3SippPlannedTotal,
    ref double s1PlannedTotal, ref double s2PlannedTotal,
    out double o1S1, out double o2S1, out double o3S1,
    out double o1S2, out double o2S2, out double o3S2,
    out double s1Target, out double s2Target,
    List<string> notes)
    {
        // 1. Planned totals
        o1PlannedTotalIncExt += O1;
        o2PlannedTotalIncExt += O2;
        o3PlannedTotalIncExt += O3;

        // 2. Compute total external funding and waterfall it O1 -> O2 -> O3
        double totalObjectives = O1 + O2 + O3;
        double totalSippSpend = S1Spend + S2Spend;

        double external = Math.Max(0.0, totalObjectives - totalSippSpend);

        double extO1 = Math.Min(O1, external);
        external -= extO1;

        double extO2 = Math.Min(O2, external);
        external -= extO2;

        double extO3 = Math.Min(O3, external);
        external -= extO3;

        // External totals per objective
        o1ExternalTotal += extO1;
        o2ExternalTotal += extO2;
        o3ExternalTotal += extO3;

        // Remaining amounts that must be funded by SIPP
        double o1SippFunded = O1 - extO1;
        double o2SippFunded = O2 - extO2;
        double o3SippFunded = O3 - extO3;

        // 3. SIPP-funded totals (objective-level)
        o1SippPlannedTotal += o1SippFunded;
        o2SippPlannedTotal += o2SippFunded;
        o3SippPlannedTotal += o3SippFunded;

        // 4. Split SIPP funding between S1 and S2 proportionally to their spend
        double sippTotal = totalSippSpend;
        double s1Share = (sippTotal > 0.0) ? (S1Spend / sippTotal) : 0.0;
        double s2Share = (sippTotal > 0.0) ? (S2Spend / sippTotal) : 0.0;

        o1S1 = o1SippFunded * s1Share;
        o1S2 = o1SippFunded * s2Share;

        o2S1 = o2SippFunded * s1Share;
        o2S2 = o2SippFunded * s2Share;

        o3S1 = o3SippFunded * s1Share;
        o3S2 = o3SippFunded * s2Share;

        // 5. Per-SIPP targets (what guardrails/bonus will work from)
        s1Target = o1S1 + o2S1 + o3S1;
        s2Target = o1S2 + o2S2 + o3S2;

        s1PlannedTotal += s1Target;
        s2PlannedTotal += s2Target;
    }
    private double ApplyHybridBonus(
        double health,
        double potValue,
        double annualSurplus,
        ref double target,
        string label,
        List<string> notes)
    {
        var b = CurrentConfig.Bonus;

        // Bonus only applies when health is GOOD
        if (health <= b.StartThreshold)
            return 0.0;

        // Determine bonus percentage tier
        double pct =
            health < b.Tier2Threshold ? b.PctTier1 :
            health < b.Tier3Threshold ? b.PctTier2 :
            health < b.Tier4Threshold ? b.PctTier3 :
            health < b.Tier5Threshold ? b.PctTier4 :
            b.PctTier5;

        // Raw bonus based on pot value
        double bonusRaw = potValue * pct;

        // Cap bonus by annual surplus (cannot exceed surplus)
        double cap = Math.Max(annualSurplus, 0.0);
        double bonus = Math.Min(bonusRaw, cap);

        if (bonus <= 0.01)
            return 0.0;

        target += bonus;
        notes.Add($"{label}:Bonus={bonus:F0}");
        return bonus;
    }

    private (bool skipped,
             double RebalanceB2FromB3Total,
             double RebalanceB3FromB2Total,
             double B2PercentAllocation,
             double B3PercentAllocation)
    ApplyHybridRebalance(
        ref double b2,
        ref double b3,
        double B2H,
        double B3H,
        List<string> notes,
        string label)
    {
        var hcfg = CurrentConfig.Hybrid;

        double riskPot = b2 + b3;
        if (riskPot <= 0.01)
            return (true, 0, 0, 0, 0);

        double baseB2 = hcfg.AllocationB2;
        double baseB3 = hcfg.AllocationB3;
        double driftTolerance = hcfg.DriftTolerance;

        // ------------------------------------------------------------
        // 0. Determine if we are within tolerance, if we are, no rebalance needed.
        // ------------------------------------------------------------
        double B2PercentAllocation = b2 != 0 ? (b2 / riskPot) * 100 : 0;
        double B3PercentAllocation = b3 != 0 ? (b3 / riskPot) * 100 : 0;
        double drift = Math.Abs(baseB2 - (B2PercentAllocation / 100));
        if (drift < hcfg.DriftTolerance)
        {
            notes.Add($"{label}:RebalSkip_Drift_{drift:F2}");
            return (true, 0, 0, B2PercentAllocation, B3PercentAllocation);
        }

        // ------------------------------------------------------------
        // 1. Determine current imbalance direction
        // ------------------------------------------------------------
        double targetB2 = baseB2;
        double targetB3 = baseB3;

        bool moveToB2 = (b3 / riskPot) > baseB3;

        // ------------------------------------------------------------
        // 2. Determine source bucket health (key improvement)
        // ------------------------------------------------------------
        bool sourceHealthy;

        if (moveToB2)
            sourceHealthy = B3H >= 1.0;   // selling B3
        else
            sourceHealthy = B2H >= 1.0;   // selling B2

        // ------------------------------------------------------------
        // 3. Adjust target regime based on source health
        // ------------------------------------------------------------
        if (!sourceHealthy)
        {
            // relaxed regime: allow drift band away from source pressure
            if (moveToB2)
            {
                targetB2 = baseB2 + driftTolerance;
                targetB3 = baseB3 - driftTolerance;
            }
            else
            {
                targetB2 = baseB2 - driftTolerance;
                targetB3 = baseB3 + driftTolerance;
            }

            // normalise
            double sum = targetB2 + targetB3;
            targetB2 /= sum;
            targetB3 /= sum;
        }

        // ------------------------------------------------------------
        // 4. Deterministic rebalance
        // ------------------------------------------------------------
        double desiredB2 = targetB2 * riskPot;
        double desiredB3 = targetB3 * riskPot;

        double fromB2 = 0, fromB3 = 0;

        if (b2 > desiredB2)
        {
            double amt = Math.Min(b2 - desiredB2, b2);
            b2 -= amt;
            b3 += amt;
            fromB2 = amt;
        }
        else if (b3 > desiredB3)
        {
            double amt = Math.Min(b3 - desiredB3, b3);
            b3 -= amt;
            b2 += amt;
            fromB3 = amt;
        }

        double newRisk = b2 + b3;

        notes.Add($"{label}:Rebal_SourceHealthy={sourceHealthy}");

        return (
            false,
            fromB2,
            fromB3,
            (b2 / newRisk) * 100,
            (b3 / newRisk) * 100
        );
    }


    private (double totalWithdrawal, double B1Withdrawn, double B2Withdrawn, double B3Withdrawn)
    ApplyHybridWithdrawals(
        ref double b1,
        ref double b2,
        ref double b3,
        double target,
        double B2H,
        double B3H,
        Config config,
        string label,
        List<string> notes,
        out double withdrawn,
        out double unmet)
    {
        withdrawn = 0.0;
        unmet = target;
        double fromB1 = 0, fromB2 = 0, fromB3 = 0;

        // Local helper: withdraw from a single bucket
        static double Take(ref double bucket, double need)
        {
            if (need <= 0 || bucket <= 0) return 0;
            double t = Math.Min(bucket, need);
            bucket -= t;
            return t;
        }

        // 1. Take from B1 first (pure buffer consumption)
        fromB1 = Take(ref b1, unmet);
        withdrawn += fromB1;
        unmet -= fromB1;

        // 2. If still need funding, split remainder pro-rata across B2 and B3
        double riskPool = b2 + b3;

        if (unmet > 0.01 && riskPool > 0.01)
        {
            double b2Share = b2 / riskPool;
            double b3Share = b3 / riskPool;

            double needB2 = Math.Min(unmet * b2Share, b2);
            double needB3 = Math.Min(unmet * b3Share, b3);

            // First pass: proportional
            fromB2 = Take(ref b2, needB2);
            fromB3 = Take(ref b3, needB3);

            withdrawn += fromB2 + fromB3;
            unmet -= (fromB2 + fromB3);

            // Second pass: mop up residual
            if (unmet > 0.01)
            {
                double extraB2 = Take(ref b2, unmet);
                withdrawn += extraB2;
                fromB2 += extraB2;
                unmet -= extraB2;

                double extraB3 = Take(ref b3, unmet);
                withdrawn += extraB3;
                fromB3 += extraB3;
                unmet -= extraB3;
            }
        }

        notes.Add($"{label}:WdrawB1_thenProRataRisk,Unmet={unmet:F0}");

        return (withdrawn, fromB1, fromB2, fromB3);
    }


}
