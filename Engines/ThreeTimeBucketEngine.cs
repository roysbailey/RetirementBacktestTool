using SippBucketDrawdown.Shared;
using System.Reflection.Metadata.Ecma335;

namespace SippBucketDrawdown.Engines;

public enum FundingState
{
    ExtremeBad,
    Bad,
    Normal,
    Good
}

public class ThreeTimeBucketEngine : EngineBase, ISimulationEngine
{
    private const string EngineVersion = "4.0.0";
    private const string EngineDescription =
        "A+B+C+E+D+F (Momentum-aligned guardrails + missed SIPP funding accounting + bonus + state-aware refill + configurable thresholds)";

    public SimulationResult Run(
        List<PlanRow> plan,
        List<(int Year, double MMF_RR, double EQUITY_RR, double BONDS_RR, double SythntheticCGT_RR, double Sythetic404020_RR)> marketReturns,
        Config config,
        bool verbose,
        double csvS1Total,
        double csvS2Total,
        double csvO1Total,
        double csvO2Total,
        double csvO3Total,
        bool printSummary)
    {
        // === ALIGN RETURNS TO SimulationStartYear ===
        int startIndex = marketReturns.FindIndex(r => r.Year == config.SimulationStartYear);
        if (startIndex < 0)
        {
            Console.WriteLine($"ERROR: SimulationStartYear {config.SimulationStartYear} not found in market-returns file.");
            Environment.Exit(1);
        }

        var usableReturns = marketReturns.Skip(startIndex).ToList();
        int simYears = Math.Min(plan.Count, usableReturns.Count);
        if (simYears == 0)
        {
            return new SimulationResult
            {
                StartYear = config.SimulationStartYear,
                SimYears = 0,
                Survived = true
            };
        }

        // ---------------------------------------------------------
        // YEAR 1 INITIALISATION — REAL SIPP BALANCES
        // ---------------------------------------------------------
        double s1Pot = config.Sipp1StartingBalance;
        double s2Pot = config.Sipp2StartingBalance;


        // PV based for B2 not for B1 (as cash with no growth)
        var initS1Targets = ComputeBucketTargetsForSipp(plan, 0, config, 1);
        var initS2Targets = ComputeBucketTargetsForSipp(plan, 0, config, 2);

        double s1B1 = initS1Targets.B1Target;
        double s1B2 = initS1Targets.B2Target;
        double s1B3 = Math.Max(0, s1Pot - (s1B1 + s1B2));

        double s2B1 = initS2Targets.B1Target;
        double s2B2 = initS2Targets.B2Target;
        double s2B3 = Math.Max(0, s2Pot - (s2B1 + s2B2));

        // ---------------------------------------------------------
        // OBJECTIVE ACCOUNTING ACCUMULATORS
        // ---------------------------------------------------------
        double s1PlannedTotal = 0.0, s1ActualTotal = 0.0;
        double s2PlannedTotal = 0.0, s2ActualTotal = 0.0;
        double PlannedTotal30 = 0, ActualPlusLegacyTotal30 = 0;

        double o1PlannedTotal = 0.0, o2PlannedTotal = 0.0, o3PlannedTotal = 0.0;
        double o1DeliveredTotal = 0.0, o2DeliveredTotal = 0.0, o3DeliveredTotal = 0.0;

        double s1O1D = 0, s1O2D = 0, s1O3D = 0;
        double s2O1D = 0, s2O2D = 0, s2O3D = 0;

        double totalBonus = 0.0;
        int? depletionYearIndex = null;
        double sippWithdrawalsBaseTotal = 0.0;

        double o1ExternalTotal = 0.0, o2ExternalTotal = 0.0, o3ExternalTotal = 0.0;
        double o1SippPlannedTotal = 0.0, o2SippPlannedTotal = 0.0, o3SippPlannedTotal = 0.0;
        double o1SippActualTotal = 0.0, o2SippActualTotal = 0.0, o3SippActualTotal = 0.0;

        // ★ NEW — early cuts accumulators
        double earlyO2Cuts = 0.0;
        double earlyO3Cuts = 0.0;

        if (verbose)
        {
            Console.WriteLine($"\n=== ThreeTimeBucketEngine DEBUG - v{EngineVersion} - {EngineDescription} ===");

            Console.WriteLine(
                "Year | rBond | rEqty | " +
                "S1Pot | S1FR | S1State | " +
                "S1BaseLineLower | S1BaseLine | S1BaseLineUpper | " +
                "S1B1 | S1B2 | S1B3 | " +
                "S1_GuardRails |" +
                "S1_O1_Planned | S1_O1_Actual | " +
                "S1_O2_Planned | S1_O2_Actual | " +
                "S1_O3_Planned | S1_O3_Actual | " +
                "S1_Bonus | " +
                "S1_WdrawB1 | S1_WdrawB2 | S1_WdrawB3 |" +
                "S1_RefillB1 | S1_RefillB2 ||" +

                "S2Pot | S2FR | S2State | " +
                "S2BaseLineLower | S2BaseLine | S2BaseLineUpper | " +
                "S2B1 | S2B2 | S2B3 | " +
                "S2_GuardRails |" +
                "S2_O1_Planned | S2_O1_Actual | " +
                "S2_O2_Planned | S2_O2_Actual | " +
                "S2_O3_Planned | S2_O3_Actual | " +
                "S2_Bonus |" +
                "S2_WdrawB1 | S2_WdrawB2 | S2_WdrawB3 |" +
                "S2_RefillB1 | S2_RefillB2 | |" +

                "Year| LowerGuard | BaseLine | ActualValue | UpperGuard | " +
                "Notes"
            );


            Console.WriteLine(new string('-', 330));
        }

        // Calulate initial baselines and total balances for debug charting
        double liabRate = config.LiabilityDiscountRate;
        List<(double baseline, double balance)> annualTotalBalanceBySimYear = new();
        List<(double planned, double actual)> yearlyTotalPaymentsBySimYear = new();
        var s1PV = ComputePVFromYearIndex(1, plan, 0, liabRate);
        var s2PV = ComputePVFromYearIndex(2, plan, 0, liabRate);
        annualTotalBalanceBySimYear.Add((s1PV + s2PV, config.Sipp1StartingBalance + config.Sipp2StartingBalance));

        for (int yearIndex = 0; yearIndex < simYears; yearIndex++)
        {
            var notes = new List<string>();

            var row = plan[yearIndex];
            double rBond = GetBucket2FundReturn(usableReturns[yearIndex]);
            double rEquity = GetBucket3FundReturn(usableReturns[yearIndex]);

            // Capture the bucket balances at start of year (for debug)
            double s1B1StartOfYear = s1B1;
            double s1B2StartOfYear = s1B2;
            double s1B3StartOfYear = s1B3;
            double s2B1StartOfYear = s2B1;
            double s2B2StartOfYear = s2B2;
            double s2B3StartOfYear = s2B3;

            // ---------------------------------------------------------
            // BLOCK A — BASELINE + FUNDINGRATIO + STATE +
            // Baseline means the PV value for S1/S2 spend for that year, discounted back to today using the config discount rate.
            //  So we have the current value of the sipp s1Total and also the value we expect it to be based on future planned spend - the discount
            // ---------------------------------------------------------

            double s1Baseline = ComputeBaselineForSipp(plan, yearIndex, config, 1);
            double s2Baseline = ComputeBaselineForSipp(plan, yearIndex, config, 2);

            double s1Total = s1B1 + s1B2 + s1B3;
            double s2Total = s2B1 + s2B2 + s2B3;

            double fundingRatioS1 = ComputeFundingRatio(s1Total, s1Baseline);
            double fundingRatioS2 = ComputeFundingRatio(s2Total, s2Baseline);

            FundingState stateS1 = GetFundingState(fundingRatioS1, config);
            FundingState stateS2 = GetFundingState(fundingRatioS2, config);

            // ---------------------------------------------------------
            // BLOCK B — EXTERNAL FUNDING + SIPP ALLOCATION
            // ---------------------------------------------------------

            o1PlannedTotal += row.O1;
            o2PlannedTotal += row.O2;
            o3PlannedTotal += row.O3;

            double o1Sipp, o2Sipp, o3Sipp;
            double o1S1, o2S1, o3S1;
            double o1S2, o2S2, o3S2;

            AllocateSippFunding(
                row.O1, row.O2, row.O3,
                row.S1Spend, row.S2Spend,
                out o1Sipp, out o2Sipp, out o3Sipp,
                out o1S1, out o2S1, out o3S1,
                out o1S2, out o2S2, out o3S2);

            o1SippPlannedTotal += o1Sipp;
            o2SippPlannedTotal += o2Sipp;
            o3SippPlannedTotal += o3Sipp;

            double o1External = row.O1 - o1Sipp;
            double o2External = row.O2 - o2Sipp;
            double o3External = row.O3 - o3Sipp;

            o1ExternalTotal += o1External;
            o2ExternalTotal += o2External;
            o3ExternalTotal += o3External;

            o1DeliveredTotal += row.O1;
            o2DeliveredTotal += row.O2;
            o3DeliveredTotal += row.O3;

            s1PlannedTotal += row.S1Spend;
            s2PlannedTotal += row.S2Spend;

            if (yearIndex == 29) PlannedTotal30 = s1PlannedTotal + s2PlannedTotal;

            double s1SippNeed = o1S1 + o2S1 + o3S1;
            double s2SippNeed = o1S2 + o2S2 + o3S2;

            // ---------------------------------------------------------
            // BLOCK F — GUARDRAILS
            // ---------------------------------------------------------

            double s1CutO1 = 0, s1CutO2 = 0, s1CutO3 = 0;
            double s2CutO1 = 0, s2CutO2 = 0, s2CutO3 = 0;

            double s1Target = s1SippNeed;
            double s2Target = s2SippNeed;

            if (s1Total > 0)
            {
                ApplyThreeBucketGuardrails(
                    s1B1, s1B2, s1B3,
                    fundingRatioS1,
                    o1S1, o2S1, o3S1,
                    ref s1Target,
                    ref s1CutO1, ref s1CutO2, ref s1CutO3,
                    config,
                    notes,
                    1,
                    yearIndex,
                    plan
                );
            }
            var S1_GuardRails = s1CutO1 + s1CutO2 + s1CutO3;

            if (s2Total > 0)
            {
                ApplyThreeBucketGuardrails(
                    s2B1, s2B2, s2B3,
                    fundingRatioS2,
                    o1S2, o2S2, o3S2,
                    ref s2Target,
                    ref s2CutO1, ref s2CutO2, ref s2CutO3,
                    config,
                    notes,
                    2,
                    yearIndex,
                    plan
                );
            }
            var S2_GuardRails = s2CutO1 + s2CutO2 + s2CutO3;

            // ★ NEW — accumulate early cuts (first 10 years only)
            if (yearIndex < 10)
            {
                earlyO2Cuts += (s1CutO2 + s2CutO2);
                earlyO3Cuts += (s1CutO3 + s2CutO3);
            }

            double s1Bonus = 0.0;
            double s2Bonus = 0.0;

            var bonusCfg = config.Bonus;

            if (s1Total > 0)
            {

                // -------------------------
                // S1 BONUS
                // -------------------------
                if (fundingRatioS1 > bonusCfg.StartThreshold)
                {
                    double pct;
                    int tier;

                    if (fundingRatioS1 < bonusCfg.Tier2Threshold) { pct = bonusCfg.PctTier1; tier = 1; }
                    else if (fundingRatioS1 < bonusCfg.Tier3Threshold) { pct = bonusCfg.PctTier2; tier = 2; }
                    else if (fundingRatioS1 < bonusCfg.Tier4Threshold) { pct = bonusCfg.PctTier3; tier = 3; }
                    else if (fundingRatioS1 < bonusCfg.Tier5Threshold) { pct = bonusCfg.PctTier4; tier = 4; }
                    else { pct = bonusCfg.PctTier5; tier = 5; }

                    double pot1 = s1B1 + s1B2 + s1B3;
                    double surplus1 = pot1 - s1Baseline;

                    double rawBonus = pot1 * pct;
                    s1Bonus = Math.Max(0, Math.Min(rawBonus, surplus1));

                    // Add note
                    notes.Add($"S1:Bonus_Tier{tier}_Pct={Math.Round(pct, 2)}_Raw={Math.Round(rawBonus, 2)}_Capped={Math.Round(s1Bonus, 2)}");


                    s1Target += s1Bonus;
                    totalBonus += s1Bonus;
                }
            }

            if (s2Total > 0)
            {
                // -------------------------
                // S2 BONUS
                // -------------------------
                if (fundingRatioS2 > bonusCfg.StartThreshold)
                {
                    double pct;
                    int tier;

                    if (fundingRatioS2 < bonusCfg.Tier2Threshold) { pct = bonusCfg.PctTier1; tier = 1; }
                    else if (fundingRatioS2 < bonusCfg.Tier3Threshold) { pct = bonusCfg.PctTier2; tier = 2; }
                    else if (fundingRatioS2 < bonusCfg.Tier4Threshold) { pct = bonusCfg.PctTier3; tier = 3; }
                    else if (fundingRatioS2 < bonusCfg.Tier5Threshold) { pct = bonusCfg.PctTier4; tier = 4; }
                    else { pct = bonusCfg.PctTier5; tier = 5; }

                    double pot2 = s2B1 + s2B2 + s2B3;
                    double surplus2 = pot2 - s2Baseline;

                    double rawBonus = pot2 * pct;
                    s2Bonus = Math.Max(0, Math.Min(rawBonus, surplus2));

                    // Add note
                    notes.Add($"S2:Bonus_Tier{tier}_Pct={Math.Round(pct, 2)}_Raw={Math.Round(rawBonus, 2)}_Capped={Math.Round(s2Bonus, 2)}");

                    s2Target += s2Bonus;
                    totalBonus += s2Bonus;
                }
            }

            // missed SIPP funding
            s1O1D += s1CutO1; s1O2D += s1CutO2; s1O3D += s1CutO3;
            s2O1D += s2CutO1; s2O2D += s2CutO2; s2O3D += s2CutO3;

            // Adjust SIPP needs after guardrails + bonus
            s1SippNeed = s1Target;
            s2SippNeed = s2Target;


            // ---------------------------------------------------------
            // BLOCK C — WITHDRAWALS
            // ---------------------------------------------------------
            (double totalWithdrawal, double B1Withdrawn, double B2Withdrawn, double B3Withdrawn) s1WithdrawDetails = (0, 0, 0, 0);
            (double totalWithdrawal, double B1Withdrawn, double B2Withdrawn, double B3Withdrawn) s2WithdrawDetails = (0, 0, 0, 0);
            double s1Withdrawn = 0, s2Withdrawn = 0, s1Unmet = 0, s2Unmet = 0;
            if (s1Total > 0)
            {
                s1WithdrawDetails = WithdrawFromBuckets(ref s1B1, ref s1B2, ref s1B3, s1SippNeed, out s1Unmet);
                s1Withdrawn = s1WithdrawDetails.totalWithdrawal;
                notes.Add($"S1:Withdraw={Math.Round(s1Withdrawn, 2)}_Unmet={Math.Round(s1Unmet, 2)}");
            }

            if (s2Total > 0)
            {
                s2WithdrawDetails = WithdrawFromBuckets(ref s2B1, ref s2B2, ref s2B3, s2SippNeed, out s2Unmet);
                s2Withdrawn = s2WithdrawDetails.totalWithdrawal;
                notes.Add($"S2:Withdraw={Math.Round(s2Withdrawn, 2)}_Unmet={Math.Round(s2Unmet, 2)}");
            }

            double s1Base = s1Withdrawn - s1Bonus;
            double s2Base = s2Withdrawn - s2Bonus;
            sippWithdrawalsBaseTotal += s1Base + s2Base;

            // -------------------------
            // RESCUE LOGIC
            // -------------------------
            if (s2Unmet > 0.01)
            {
                notes.Add($"S1->S2_Rescue_S2Unmet={Math.Round(s2Unmet, 2)}");

                double s1RescueCapacity = s1B1 + s1B2 + s1B3;
                notes.Add($"S1->S2_Rescue_S1Capacity={Math.Round(s1RescueCapacity, 2)}");

                double rescue = Math.Min(s2Unmet, s1RescueCapacity);

                if (rescue > 0.01)
                {
                    double rescueTaken = WithdrawFromBuckets(ref s1B1, ref s1B2, ref s1B3, rescue, out double rescueUnmet).B1Withdrawn;

                    notes.Add($"S1->S2 Rescue_Taken={Math.Round(rescueTaken, 2)}_Unmet={Math.Round(rescueUnmet, 2)}");

                    s2Withdrawn += rescueTaken;
                    s2Unmet -= rescueTaken;
                }
                else
                {
                    notes.Add("S1->S2_Rescue_None");
                }
            }

            s1ActualTotal += s1Withdrawn;
            s2ActualTotal += s2Withdrawn;

            if (yearIndex == 29) ActualPlusLegacyTotal30 = s1ActualTotal + s2ActualTotal + s1B1 + s1B2 + s1B3 + s2B1 + s2B2 + s2B3;

            double o1SippActual = o1Sipp - (s1CutO1 + s2CutO1);
            double o2SippActual = o2Sipp - (s1CutO2 + s2CutO2);
            double o3SippActual = o3Sipp - (s1CutO3 + s2CutO3);

            o1SippActualTotal += o1SippActual;
            o2SippActualTotal += o2SippActual;
            o3SippActualTotal += o3SippActual;


            // ---------------------------------------------------------
            // BLOCK E — REFILL BUCKETS
            // ---------------------------------------------------------
            (double S1B1_Refill, double S1B2_Refill) S1_Refill = (0, 0);
            (double S2B1_Refill, double S2B2_Refill) S2_Refill = (0, 0);
            if (yearIndex + 1 < simYears)
            {
                if (s1Total > 0)
                {
                    S1_Refill = RefillBuckets(
                        plan, yearIndex, config,
                        ref s1B1, ref s1B2, ref s1B3,
                        stateS1,
                        rBond, rEquity, 1, notes);
                }
                if (s1Total > 0)
                {

                    S2_Refill = RefillBuckets(
                        plan, yearIndex, config,
                        ref s2B1, ref s2B2, ref s2B3,
                        stateS2,
                        rBond, rEquity, 2, notes);
                }
            }

            // ---------------------------------------------------------
            // BLOCK D — APPLY RETURNS
            // ---------------------------------------------------------

            s1B2 *= (1.0 + rBond);
            s2B2 *= (1.0 + rBond);

            s1B3 *= (1.0 + rEquity);
            s2B3 *= (1.0 + rEquity);

            // ---------------------------------------------------------
            // DEBUG OUTPUT
            // ---------------------------------------------------------
            if (verbose)
            {
                double s1Remain = s1B1 + s1B2 + s1B3;
                double s2Remain = s2B1 + s2B2 + s2B3;

                string notesJoined = string.Join(";", notes);   // ★ NEW
                var displayYear = $"{row.Year,4} ({usableReturns[yearIndex].Year})";

                Console.WriteLine(
                    $"{displayYear} | {rBond,6:P2} | {rEquity,6:P2} | " +
                    $"{s1Total,8:N0} | {fundingRatioS1,5:F2} | {stateS1,10} | " +
                    $"{s1Baseline * 0.8,8:N0} | {s1Baseline,8:N0} | {s1Baseline * 1.2,8:N0} | " +
                    $"{s1B1StartOfYear,8:N0} | {s1B2StartOfYear,8:N0} | {s1B3StartOfYear,8:N0} | " +

                    $"{S1_GuardRails,8:N0} | " +

                    $"{o1S1,6:N0} | {o1S1 - s1CutO1,6:N0} | " +
                    $"{o2S1,6:N0} | {o2S1 - s1CutO2,6:N0} | " +
                    $"{o3S1,6:N0} | {o3S1 - s1CutO3,6:N0} | " +

                    $"{s1Bonus,8:N0} |" + 
                    $"{s1WithdrawDetails.B1Withdrawn,6:N0} | {s1WithdrawDetails.B2Withdrawn,6:N0} | {s1WithdrawDetails.B3Withdrawn,6:N0} |" +
                    $"{S1_Refill.S1B1_Refill,8:N0} | {S1_Refill.S1B2_Refill,8:N0} ||" +

                    $"{s2Total,8:N0} | {fundingRatioS2,5:F2} | {stateS2,10} | " +
                    $"{s2Baseline * 0.8,8:N0} | {s2Baseline,8:N0} | {s2Baseline * 1.2,8:N0} | " +
                    $"{s2B1StartOfYear,8:N0} | {s2B2StartOfYear,8:N0} | {s2B3StartOfYear,8:N0} | " +

                    $"{S2_GuardRails,8:N0} | " +

                    $"{o1S2,6:N0} | {o1S2 - s2CutO1,6:N0} | " +
                    $"{o2S2,6:N0} | {o2S2 - s2CutO2,6:N0} | " +
                    $"{o3S2,6:N0} | {o3S2 - s2CutO3,6:N0} | " +

                    $"{s2Bonus,8:N0} |" +
                    $"{s2WithdrawDetails.B1Withdrawn,6:N0} | {s2WithdrawDetails.B2Withdrawn,6:N0} | {s2WithdrawDetails.B3Withdrawn,6:N0} |" +
                    $"{S2_Refill.S2B1_Refill,8:N0} | {S2_Refill.S2B2_Refill,8:N0} | |" +

                    $"{displayYear} | {(s1Baseline + s2Baseline) * 0.8,8:N0} | {(s1Baseline + s2Baseline),8:N0} | {s1Total + s2Total,8:N0} | {(s1Baseline + s2Baseline) * 1.2,8:N0} | " +
                    $"{notesJoined}"
                );
            }

            double totalPot = s1B1 + s1B2 + s1B3 + s2B1 + s2B2 + s2B3;
            if (depletionYearIndex == null && totalPot <= 1.0)
                depletionYearIndex = yearIndex + 1;

            // Track total balance by simulation year (for debug / analysis)
            double comingYearS1PV = ComputePVFromYearIndex(1, plan, yearIndex + 1, liabRate);
            double comingYearS2PV = ComputePVFromYearIndex(2, plan, yearIndex + 1, liabRate);
            annualTotalBalanceBySimYear.Add((comingYearS1PV + comingYearS2PV, totalPot));
            var totalPlannedSpend = o1S2 + o2S2 + o3S2 + o1S1 + o2S1 + o3S1;
            var totalActualSpend = o1SippActual + o2SippActual + o3SippActual + s1Bonus + s2Bonus;
            yearlyTotalPaymentsBySimYear.Add((totalPlannedSpend, totalActualSpend));
        }

        // ---------------------------------------------------------
        // END OF SIMULATION
        // ---------------------------------------------------------

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
        Legacy = legacy,
        TotalPlanned = totalPlanned,
        TotalActualPlusLegacy = totalActualPlusLegacy,

        TotalPlanned30 = PlannedTotal30,
        TotalActualPlusLegacy30 = ActualPlusLegacyTotal30,

        O1PlannedIncExt = o1PlannedTotal,
        O2PlannedIncExt = o2PlannedTotal,
        O3PlannedIncExt = o3PlannedTotal,

        O1DeliveredIncExt = o1ExternalTotal + o1SippActualTotal,
        O2DeliveredIncExt = o2ExternalTotal + o2SippActualTotal,
        O3DeliveredIncExt = o3ExternalTotal + o3SippActualTotal,

        O1SippMissed = s1O1D + s2O1D,
        O2SippMissed = s1O2D + s2O2D,
        O3SippMissed = s1O3D + s2O3D,

        Bonus = totalBonus,
        Survived = depletionYearIndex == null,
        DepletionYearIndex = depletionYearIndex,

        // ★ NEW — early cuts added to result
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
            o1PlannedTotal, o2PlannedTotal, o3PlannedTotal,
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


    // -------------------------------------------------------------
    // HELPERS
    // -------------------------------------------------------------

    // NOTE -> FUTURE IMPROVEMENT.  The algorithm correctly detects when a saving is needed and the tier of saving
    //  However, sometiems the action does not align to the funding that year.  E.g. cut 100% of O3, but there is NO O3 value that year
    // This means that the cut has no effect that year, and the SIPP need is not reduced, which can cause a depletion later on.  This is more likely in early years when planned spend is higher, and can cause some distortion in the results.
    protected void ApplyThreeBucketGuardrails(
    double b1, double b2, double b3,
    double fundingRatio,
    double o1Portion, double o2Portion, double o3Portion,
    ref double newTarget,
    ref double cutO1, ref double cutO2, ref double cutO3,
    Config config,
    List<string> notes,
    int sippIndex,
    int yearIndex,
    List<PlanRow> plan)
    {
        double[] plannedSpend = sippIndex == 1 ? plan.Select(p => p.S1Spend).ToArray()
                                               : plan.Select(p => p.S2Spend).ToArray();

        // Trim down the planned spend so it does not contain multiple "zero years"
        int lastIndex = Array.FindLastIndex(plannedSpend, x => x != 0);
        plannedSpend = plannedSpend.Take(lastIndex+1).ToArray();
        var yearsToFund = plannedSpend.Count() - yearIndex;
        if (yearsToFund <= 0) return;
        var onlyNeedB1 = yearsToFund <= config.TimeBuckets.Bucket1Years;
        var onlyNeedB1AndB2 = yearsToFund <= config.TimeBuckets.Bucket1Years + config.TimeBuckets.Bucket2Years;

        // ---------------------------------------------------------
        // 1. Compute B1Target and B2Target from planned spend
        // ---------------------------------------------------------
        double b1Target = 0;
        double b2Target = 0;

        var Bucket1Years = config.TimeBuckets.Bucket1Years;
        var Bucket2Years = config.TimeBuckets.Bucket2Years;

        // B1 - calculate target for B1 based on planned spend for years in that bucket (no PV needed for bucket 1)
        for (int i = 0; i < Bucket1Years; i++)
        {
            int idx = yearIndex + i;
            if (idx < plannedSpend.Length)
                b1Target += plannedSpend[idx];
        }

        // B2 - PV-adjusted target (money still has time to grow while in B2)
        b2Target = ComputePVWindow(
            plan,
            yearIndex + Bucket1Years,
            Math.Min(plannedSpend.Length, yearIndex + Bucket1Years + Bucket2Years),
            config, 
            sippIndex
        );

        // ---------------------------------------------------------
        // 2. Compute B1Health
        // ---------------------------------------------------------
        int frTier = 0;
        int bhTier = 0;

        double b1Health = (b1Target > 0) ? b1 / b1Target : 1.0;
        b1Health = Math.Round(b1Health, 4);

        // Toward the end of planned spending for a SIPP the required runway reduces
        // this can lead to a position where we populate b2 from b3 for the last year needed and then have bad returns which reduce b2
        // we compare b2 to need, and it is now lower (due to poor returns) so it thinks 
        if (onlyNeedB1 || onlyNeedB1AndB2)
        {
            if (fundingRatio >= 1)
            {
                notes.Add($"S{sippIndex}:Guardrail_NoCut_OnlyNeedB1orB1B2_Health={Math.Round(fundingRatio, 2)}");
                return;
            }
            else if (b1 + b2 + b3 > b1Target + b2Target) 
            {
                notes.Add($"S{sippIndex}:Guardrail_NoCut_sippGTliab_OnlyNeedB1orB1B2");
                return;
            }
            bhTier = 1;
            notes.Add($"S{sippIndex}:Guardrail_BHTier={bhTier}_OnlyNeedB1orB1B2_sipplTliab");
        }
        else
        {
            // ---------------------------------------------------------
            // 3. Compute SafeRunway using rolling planned spend
            // ---------------------------------------------------------

            double safePot = b1 + b2;
            double safeRunway = 0.0;

            int yr = yearIndex;
            while (yr < plannedSpend.Length && safePot > 0)
            {
                safePot -= plannedSpend[yr];
                safeRunway += 1.0;
                yr++;
            }

            safeRunway = Math.Round(safeRunway, 4);

            // ---------------------------------------------------------
            // 4. Compute BHTier
            // ---------------------------------------------------------

            //if (safeRunway >= 9 && b1Health >= 0.80) bhTier = 0;
            //else if (safeRunway >= 7 && b1Health >= 0.50) bhTier = 1;
            //else if (safeRunway >= 5 && b1Health >= 0.30) bhTier = 2;
            //else if (safeRunway >= 3) bhTier = 3;   // ★ corrected rule
            //else bhTier = 4;
            if (safeRunway >= 6 && b1Health >= 0.60) bhTier = 0;
            else if (safeRunway >= 3 && b1Health >= 0.60) bhTier = 1;
            else if (safeRunway >= 2 && b1Health >= 0.60) bhTier = 2;
            else if (safeRunway >= 2) bhTier = 3;
            else bhTier = 4;

            notes.Add($"S{sippIndex}:Guardrail_BHTier={bhTier}_SafeRunway={Math.Round(safeRunway, 2)}_B1Health={Math.Round(b1Health, 2)}");

            // ---------------------------------------------------------
            // 5. Compute FRTier
            // ---------------------------------------------------------

            if (fundingRatio >= 0.80) frTier = 0;
            else if (fundingRatio >= 0.75) frTier = 1;
            else if (fundingRatio >= 0.70) frTier = 2;
            else if (fundingRatio >= 0.65) frTier = 3;
            else frTier = 4;

            notes.Add($"S{sippIndex}:Guardrail_FRTier={frTier}_FR={Math.Round(fundingRatio, 2)}");

            // ---------------------------------------------------------
            // 6. FinalTier = max(BHTier, FRTier)
            // ---------------------------------------------------------
        }

        int finalTier = Math.Max(bhTier, frTier);
        notes.Add($"S{sippIndex}:Guardrail_FinalTier={finalTier}");

        if (frTier > bhTier)
            notes.Add($"S{sippIndex}:Guardrail_Reason=FROverride");
        else
            notes.Add($"S{sippIndex}:Guardrail_Reason=BucketDriven");

        // ---------------------------------------------------------
        // 7. Apply Momentum Tier Actions (exact match)
        // ---------------------------------------------------------

        double keepO1 = o1Portion;
        double keepO2 = o2Portion;
        double keepO3 = o3Portion;

        cutO1 = 0; cutO2 = 0; cutO3 = 0;

        switch (finalTier)
        {
            case 0:
                notes.Add($"S{sippIndex}:Range0_NoCuts");
                break;

            case 1:
                cutO3 = 0.5 * o3Portion;
                keepO3 = 0.5 * o3Portion;
                notes.Add($"S{sippIndex}:Range1_O3_50%Cut");
                break;

            case 2:
                cutO3 = o3Portion;
                keepO3 = 0;
                notes.Add($"S{sippIndex}:Range2_O3_100%Cut");
                break;

            case 3:
                cutO3 = o3Portion; keepO3 = 0;
                cutO2 = 0.75 * o2Portion; keepO2 = 0.25 * o2Portion;
                notes.Add($"S{sippIndex}:Range3_O3_100%_O2_75%Cut");
                break;

            case 4:
                cutO3 = o3Portion; keepO3 = 0;
                cutO2 = o2Portion; keepO2 = 0;
                cutO1 = 0.10 * o1Portion; keepO1 = 0.90 * o1Portion;
                notes.Add($"S{sippIndex}:CRITICAL_O1_10%_O2O3_100%Cut");
                break;
        }

        // ---------------------------------------------------------
        // 8. Compute new target after cuts
        // ---------------------------------------------------------

        newTarget = keepO1 + keepO2 + keepO3;
        newTarget = Math.Round(newTarget, 2);

        notes.Add($"S{sippIndex}:Guardrail_NewTarget={newTarget}");
    }


    private double ComputeBaselineForSipp(List<PlanRow> plan, int yearIndex, Config config, int sippIndex)
    {
        double baseline = ComputePVWindow(
            plan,
            yearIndex,
            plan.Count,
            config,
            sippIndex);

        return baseline;
    }

    private double ComputeFundingRatio(double pot, double baseline)
    {
        if (baseline <= 0.0) return double.PositiveInfinity;
        return pot / baseline;
    }

    // FundingState is a pure “health for refill” concept, thresholds from ThreeBucket config:
    // If FR < ExtremeBadThreshold => ExtremeBad
    // Elif FR < BadThreshold      => Bad
    // Elif FR > GoodThreshold     => Good
    // Else                        => Normal
    private FundingState GetFundingState(double fundingRatio, Config config)
    {
        var tb = config.TimeBuckets;

        if (fundingRatio < tb.FundingStateExtremeBadThreshold) return FundingState.ExtremeBad;
        if (fundingRatio < tb.FundingStateBadThreshold) return FundingState.Bad;
        if (fundingRatio > tb.FundingStateGoodThreshold) return FundingState.Good;
        return FundingState.Normal;
    }

    private BucketTargets ComputeBucketTargetsForSipp(List<PlanRow> plan, int yearIndex, Config config, int sippIndex)
    {
        int b1Years = config.TimeBuckets.Bucket1Years;
        int b2Years = config.TimeBuckets.Bucket2Years;

        double b1Target = 0.0;
        double b2Target = 0.0;

        for (int i = yearIndex; i < Math.Min(plan.Count, yearIndex + b1Years); i++)
            b1Target += (sippIndex == 1) ? plan[i].S1Spend : plan[i].S2Spend;

        b2Target = ComputePVWindow(
            plan,
            yearIndex + b1Years,
            Math.Min(plan.Count, yearIndex + b1Years + b2Years),
            config,
            sippIndex);

        double baseline = ComputeBaselineForSipp(plan, yearIndex, config, sippIndex);
        double b3Floor = Math.Max(baseline - (b1Target + b2Target), 0.0);

        return new BucketTargets
        {
            B1Target = b1Target,
            B2Target = b2Target,
            B3Floor = b3Floor
        };
    }

    private (double totalWithdrawal, double B1Withdrawn, double B2Withdrawn, double B3Withdrawn) 
        WithdrawFromBuckets(ref double b1, ref double b2, ref double b3, double need, out double unmet)
    {
        double originalNeed = need;

        double fromB1 = Math.Min(need, b1);
        b1 -= fromB1;
        need -= fromB1;

        double fromB2 = Math.Min(need, b2);
        b2 -= fromB2;
        need -= fromB2;

        double fromB3 = Math.Min(need, b3);
        b3 -= fromB3;
        need -= fromB3;

        unmet = need;
        return (originalNeed - unmet, fromB1, fromB2, fromB3);
    }


    private (double B1_RefillFromB2, double B2_Refill) RefillBuckets(
        List<PlanRow> plan,int yearIndex,Config config,
        ref double b1, ref double b2, ref double b3,
        FundingState state,
        double rBond, double rEquity, int sippNumber,
        List<string> notes)
    {
        // Work out target levels for b1 and b2 based on funding state.  If we are in bad states we want to reduce the amount of bonds we sell to refill b1 and equity we selll to refill b2.
        double b1Target = 0, b2Target = 0;
        if (state == FundingState.Bad || state == FundingState.ExtremeBad)
        {
            int b1TargetYears = state == FundingState.Bad ? 2 : 1;
            int b2TargetYears = state == FundingState.Bad ? 2 : 1;

            b2Target = ComputePVWindow(
                plan,
                yearIndex + config.TimeBuckets.Bucket1Years,
                Math.Min(plan.Count, yearIndex + config.TimeBuckets.Bucket1Years + b2TargetYears),
                config,
                sippNumber);

            b1Target = plan.OrderBy(r => r.Year).Skip(yearIndex + 1).Take(b1TargetYears).Sum(r => sippNumber == 1 ? r.S1Spend : r.S2Spend);
        }
        else
        {
            var nextTargetsS1 = ComputeBucketTargetsForSipp(plan, yearIndex + 1, config, sippNumber);
            b1Target = nextTargetsS1.B1Target;
            b2Target = nextTargetsS1.B2Target;
        }

        // --- B1 refill ---
        double needB1 = Math.Max(b1Target - b1, 0.0);
        double B1_RefillFromB2 = 0.0;

        if (needB1 > 0.0)
        {
            double transfer = Math.Min(needB1, b2);
            B1_RefillFromB2 = transfer;
            b1 += transfer;
            b2 -= transfer;
            notes.Add($"S{sippNumber}:Refill_B1_FromB2_Normal_Transfer={Math.Round(transfer, 2)}");
            //if (state == FundingState.Normal || state == FundingState.Good)
            //{
            //    double transfer = Math.Min(needB1, b2);
            //    B1_RefillFromB2 = transfer;
            //    b1 += transfer;
            //    b2 -= transfer;
            //    notes.Add($"S{sippNumber}:Refill_B1_FromB2_Normal_Transfer={Math.Round(transfer, 2)}");
            //}
        }
        else
        {
            notes.Add("S{sippNumber}:Refill_B1_NotNeeded");
        }

        // --- B2 refill ---
        double needB2 = Math.Max(b2Target - b2, 0.0);
        double B2_RefillFromB3 = 0.0;
        if (needB2 > 0.0)
        {
            double transfer = Math.Min(needB2, b3);
            B2_RefillFromB3 = transfer;
            b2 += transfer;
            b3 -= transfer;

            notes.Add($"S{sippNumber}:Refill_B2_FromB3_Normal_Transfer={Math.Round(transfer, 2)}");
        }
        else
        {
            notes.Add("S{sippNumber}:Refill_B2_NotNeeded");
        }

        return (B1_RefillFromB2, B2_RefillFromB3);
    }

    private struct BucketTargets
    {
        public double B1Target;
        public double B2Target;
        public double B3Floor;
    }
}
