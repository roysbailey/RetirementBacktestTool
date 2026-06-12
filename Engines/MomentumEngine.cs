using SippBucketDrawdown.Shared;

namespace SippBucketDrawdown.Engines;

public class MomentumEngine : EngineBase, ISimulationEngine
{
    private const string EngineVersion = "4.0.0";
    private const string EngineDescription = "2 buckets - B1 = 3 year cash buffer, B2 = ETF 60/40 global equit and bonds";
    
    private readonly IRefillStrategy _strategy = new MomentumRefillStrategy();
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

        if (simYears <= 0)
        {
            Console.WriteLine("ERROR: No usable simulation years (check SimulationStartYear and market-returns.csv).");
            Environment.Exit(1);
        }

        // Truncated view of the plan for this simulation window
        var truncatedPlan = plan.Take(simYears).ToList();

        // Planned withdrawals for THIS simulation window only
        double simS1Planned = truncatedPlan.Sum(x => x.S1Spend);
        double simS2Planned = truncatedPlan.Sum(x => x.S2Spend);

        double PlannedTotal30 = truncatedPlan.Count() < 30 
                                              ? 0 : truncatedPlan.Take(30).Sum(x => x.S1Spend) + truncatedPlan.Take(30).Sum(x => x.S2Spend);

        int startCalendarYear = usableReturns.First().Year;
        int endCalendarYear = usableReturns[simYears - 1].Year;

        if (printSummary)
        {
            Console.WriteLine($"\nSimulation window: {startCalendarYear} -> {endCalendarYear}");
        }

        double s1Etf = config.Sipp1StartingBalance, s1Cash = 0;
        double s2Etf = config.Sipp2StartingBalance, s2Cash = 0;

        double s1O1D = 0, s1O2D = 0, s1O3D = 0;
        double s2O1D = 0, s2O2D = 0, s2O3D = 0;

        double s1Actual = 0, s2Actual = 0;
        double ActualPlusLegacyTotal30 = 0;

        // === OBJECTIVE ACCOUNTING ACCUMULATORS (for simulation window only) ===
        double o1TotalPlannedIncExt = 0, o2TotalPlannedIncExt = 0, o3TotalPlannedIncExt = 0;
        double o1ExternalTotal = 0, o2ExternalTotal = 0, o3ExternalTotal = 0;
        double o1PlannedFromSippTotal = 0, o2PlannedFromSippTotal = 0, o3PlannedFromSippTotal = 0;
        double o1ActualFromSippTotal = 0, o2ActualFromSippTotal = 0, o3ActualFromSippTotal = 0;

        double totalBonus = 0;
        double sippWithdrawalsBaseTotal = 0;

        double prevReturn = 0.0;
        int? depletionYearIndex = null;

        // ★ NEW — early cuts accumulators
        double earlyO2Cuts = 0.0;
        double earlyO3Cuts = 0.0;

        double s1OpeningEtf = 0, s1OpeningCash = 0, s1OpeningTotal = 0;
        double s2OpeningEtf = 0, s2OpeningCash = 0, s2OpeningTotal = 0;

        double s1PostWithdrawEtf = 0, s1PostWithdrawCash = 0, s1PostWithdrawTotal = 0;
        double s2PostWithdrawEtf = 0, s2PostWithdrawCash = 0, s2PostWithdrawTotal = 0;

        double s1PostRefillTotal = 0, s2PostRefillTotal = 0;

        if (verbose)
        {
            Console.WriteLine($"\n=== MomentumEngine DEBUG - v{EngineVersion} - {EngineDescription} ===");
            Console.WriteLine(
                "Year | rMkt | " +
                "S1PotStYr | S1H |" +
                "S1BaseLineLower | S1BaseLine | S1BaseLineUpper | " +
                "S1B1StYr | S1B2StYr | " +
                "S1_GuardRails | " +
                "S1_O1P | S1_O1A | S1_O2P | S1_O2A | S1_O3P | S1_O3A | S1_Bonus | " +
                "S_WdrawB1 | S1_WdrawB2 | S1_RefillB1 ||" +

                "S2PotStYr | S2H | " +
                "S2BaseLineLower | S2BaseLine | S2BaseLineUpper | " +
                "S2B1StYr | S2B2StYr | " +
                "S2_GuardRails | " +
                "S2_O1P | S2_O1A | S2_O2P | S2_O2A | S2_O3P | S2_O3A | S2_Bonus |  " +
                "S2_WdrawB1 | S2_WdrawB2 | S2_RefillB1 || " +

                "Year| LowerGuard | BaseLine | ActualValue | UpperGuard | " +
                "Notes");
            Console.WriteLine(new string('-', 300));
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
            var p = plan[yearIndex];
            double mkt = GetSingleFundReturn(usableReturns[yearIndex]);
            int calendarYear = usableReturns[yearIndex].Year;

            List<string> notes = new List<string>();

            // === TRACK PLANNED OBJECTIVES ===
            o1TotalPlannedIncExt += p.O1;
            o2TotalPlannedIncExt += p.O2;
            o3TotalPlannedIncExt += p.O3;

            // === YEAR 1 CASH SEEDING ===
            if (yearIndex == 0)
            {
                int seedYears = Math.Min(config.Momentum.CashBufferYearsHighHealth, simYears);
                double seed1 = plan.Take(seedYears).Sum(x => x.S1Spend);
                double seed2 = plan.Take(seedYears).Sum(x => x.S2Spend);

                s1Etf -= seed1; s1Cash += seed1;
                s2Etf -= seed2; s2Cash += seed2;
            }

            // --- SNAPSHOT: start of year (after seeding, before anything else) ---
            s1OpeningEtf = s1Etf;
            s1OpeningCash = s1Cash;
            s1OpeningTotal = s1OpeningEtf + s1OpeningCash;

            s2OpeningEtf = s2Etf;
            s2OpeningCash = s2Cash;
            s2OpeningTotal = s2OpeningEtf + s2OpeningCash;


            // === HEALTH CALCULATION ===
            // Baseline (RL) for debug
            double s1Baseline = 0, s2Baseline = 0;
            for (int k = 0; yearIndex + k < plan.Count; k++)
            {
                double disc = Math.Pow(1 + config.LiabilityDiscountRate, k);
                s1Baseline += plan[yearIndex + k].S1Spend / disc;
                s2Baseline += plan[yearIndex + k].S2Spend / disc;
            }

            double s1H = s1Baseline > 0 ? (s1Etf + s1Cash) / s1Baseline : 1.0;
            double s2H = s2Baseline > 0 ? (s2Etf + s2Cash) / s2Baseline : 1.0;

            double s1T = p.S1Spend;
            double s2T = p.S2Spend;

            // === SIPP-FUNDED PORTIONS ===
            double o1SippFunded, o2SippFunded, o3SippFunded;
            double o1S1, o2S1, o3S1, o1S2, o2S2, o3S2;

            AllocateSippFunding(
                p.O1, p.O2, p.O3,
                p.S1Spend, p.S2Spend,
                out o1SippFunded, out o2SippFunded, out o3SippFunded,
                out o1S1, out o2S1, out o3S1,
                out o1S2, out o2S2, out o3S2);

            o1PlannedFromSippTotal += o1SippFunded;
            o2PlannedFromSippTotal += o2SippFunded;
            o3PlannedFromSippTotal += o3SippFunded;

            double o1External = p.O1 - o1SippFunded;
            double o2External = p.O2 - o2SippFunded;
            double o3External = p.O3 - o3SippFunded;

            o1ExternalTotal += o1External;
            o2ExternalTotal += o2External;
            o3ExternalTotal += o3External;

            // === APPLY GUARDRAILS ===
            double s1CutO1 = 0, s1CutO2 = 0, s1CutO3 = 0;

            ApplyFundingRatioBasedGuardrailsAndDebt(
                s1H, s1Cash,
                o1S1, o2S1, o3S1,
                p.S1Spend,
                ref s1T,
                ref s1CutO1, ref s1CutO2, ref s1CutO3,
                notes, "S1",
                prevReturn,
                yearIndex);

            var S1_GuardRails = s1CutO1 + s1CutO2 + s1CutO3;

            double s2CutO1 = 0, s2CutO2 = 0, s2CutO3 = 0;
            ApplyFundingRatioBasedGuardrailsAndDebt(
                s2H, s2Cash,
                o1S2, o2S2, o3S2,
                p.S2Spend,
                ref s2T,
                ref s2CutO1, ref s2CutO2, ref s2CutO3,
                notes, "S2",
                prevReturn,
                yearIndex);

            var S2_GuardRails = s2CutO1 + s2CutO2 + s2CutO3;

            // ★ NEW — accumulate early cuts (first 10 years only)
            if (yearIndex < 10)
            {
                earlyO2Cuts += (s1CutO2 + s2CutO2);
                earlyO3Cuts += (s1CutO3 + s2CutO3);
            }

            s1O1D += s1CutO1; s1O2D += s1CutO2; s1O3D += s1CutO3;
            s2O1D += s2CutO1; s2O2D += s2CutO2; s2O3D += s2CutO3;

            // === BASE TARGETS ===
            double s1Base = s1T;
            double s2Base = s2T;

            // === BONUS ===
            double ApplyBonus(
                double health,
                double potValue,
                double annualSurplus,
                ref double target,
                string label)
            {
                var b = config.Bonus;

                if (health <= b.StartThreshold)
                    return 0;


                double pct =
                    health < b.Tier2Threshold ? b.PctTier1 :
                    health < b.Tier3Threshold ? b.PctTier2 :
                    health < b.Tier4Threshold ? b.PctTier3 :
                    health < b.Tier5Threshold ? b.PctTier4 :
                    b.PctTier5;

                double bonusRaw = potValue * pct;
                double cap = Math.Max(annualSurplus, 0.0);
                double bonus = Math.Min(bonusRaw, cap);

                if (bonus <= 0.01) return 0;

                target += bonus;
                notes.Add($"{label}:Bonus");
                return bonus;
            }

            double pot1 = s1Etf + s1Cash;
            double pot2 = s2Etf + s2Cash;

            double surplus1 = pot1 - s1Baseline;
            double surplus2 = pot2 - s2Baseline;

            var S1_Bonus = ApplyBonus(s1H, pot1, surplus1, ref s1T, "S1");
            var S2_Bonus = ApplyBonus(s2H, pot2, surplus2, ref s2T, "S2");

            double bonusThisYear = s1T - s1Base + (s2T - s2Base);
            if (bonusThisYear < 0) bonusThisYear = 0;

            totalBonus += bonusThisYear;
            sippWithdrawalsBaseTotal += s1Base + s2Base;

            // === WITHDRAWALS ===
            s1Actual += s1T;
            s2Actual += s2T;

            double s2Need = s2T;
            double s2FromCash = 0, s2FromEtf = 0;

            if (s2H < config.Momentum.CashBufferHealthThreshold || prevReturn < 0)
            {
                s2FromCash = Math.Min(s2Need, s2Cash); s2Cash -= s2FromCash; s2Need -= s2FromCash;
                s2FromEtf = Math.Min(s2Need, s2Etf); s2Etf -= s2FromEtf; s2Need -= s2FromEtf;
            }
            else
            {
                s2FromEtf = Math.Min(s2Need, s2Etf); s2Etf -= s2FromEtf; s2Need -= s2FromEtf;
                s2FromCash = Math.Min(s2Need, s2Cash); s2Cash -= s2FromCash; s2Need -= s2FromCash;
            }

            double s1Need = s1T;
            double s1FromCash = 0, s1FromEtf = 0;

            if (s1H < config.Momentum.CashBufferHealthThreshold || prevReturn < 0)
            {
                s1FromCash = Math.Min(s1Need, s1Cash); s1Cash -= s1FromCash; s1Need -= s1FromCash;
                s1FromEtf = Math.Min(s1Need, s1Etf); s1Etf -= s1FromEtf; s1Need -= s1FromEtf;
            }
            else
            {
                s1FromEtf = Math.Min(s1Need, s1Etf); s1Etf -= s1FromEtf; s1Need -= s1FromEtf;
                s1FromCash = Math.Min(s1Need, s1Cash); s1Cash -= s1FromCash; s1Need -= s1FromCash;
            }

            // === PAYMENT TAGS ===
            if (s1FromCash > 0 && s1FromEtf == 0) notes.Add("S1_Paid_Cash");
            else if (s1FromCash > 0 && s1FromEtf > 0) notes.Add("S1_Paid_Combo");
            else if (s1FromCash == 0 && s1FromEtf > 0) notes.Add("S1_Paid_Fund");

            if (s2FromCash > 0 && s2FromEtf == 0) notes.Add("S2_Paid_Cash");
            else if (s2FromCash > 0 && s2FromEtf > 0) notes.Add("S2_Paid_Combo");
            else if (s2FromCash == 0 && s2FromEtf > 0) notes.Add("S2_Paid_Fund");

            // === RESCUE ===
            if (s2Need > 0.01)
            {
                double rescue = Math.Min(s2Need, s1Cash + s1Etf);
                if (rescue > 0.01)
                {
                    s1Actual += rescue;
                    if (s1Cash >= rescue) s1Cash -= rescue;
                    else
                    {
                        double fromCash = s1Cash;
                        double fromEtf = rescue - fromCash;
                        s1Cash = 0;
                        s1Etf = Math.Max(0, s1Etf - fromEtf);
                    }
                    notes.Add($"S1->S2 Rescue_{rescue:F2}");
                }
            }

            s1PostWithdrawEtf = s1Etf;
            s1PostWithdrawCash = s1Cash;
            s1PostWithdrawTotal = s1PostWithdrawEtf + s1PostWithdrawCash;

            s2PostWithdrawEtf = s2Etf;
            s2PostWithdrawCash = s2Cash;
            s2PostWithdrawTotal = s2PostWithdrawEtf + s2PostWithdrawCash;

            if (yearIndex == 29) ActualPlusLegacyTotal30 = s1Actual + s2Actual + s1PostWithdrawTotal + s2PostWithdrawTotal;

            // === REFILL (before returns) note. in first plan year we ALWAYS refill ===
            double actualRefill1 = 0.0;
            double actualRefill2 = 0.0;
            double mktPrev = (yearIndex == 0) ? 0.01 : (usableReturns[yearIndex - 1].EQUITY_RR * .6) + (usableReturns[yearIndex - 1].BONDS_RR * .4);
            if (_strategy.ShouldRefill(mktPrev))
            {
                var healthForRefillS1 = (yearIndex == 0) ? config.Momentum.CashBufferHealthThreshold + 0.01 : s1H;
                int s1RefillYears = _strategy.GetRefillYears(healthForRefillS1, config);
                double s1TargetCash = plan.Skip(yearIndex + 1).Take(s1RefillYears).Sum(x => x.S1Spend);
                double s1Refill = Math.Max(0, s1TargetCash - s1Cash);
                actualRefill1 = Math.Min(s1Refill, s1Etf);

                if (actualRefill1 > 0)
                {
                    s1Etf -= actualRefill1;
                    s1Cash += actualRefill1;
                    notes.Add($"S1:Refill{s1RefillYears}Yr");
                }

                var healthForRefillS2 = (yearIndex == 0) ? config.Momentum.CashBufferHealthThreshold + 0.01 : s2H;
                int s2RefillYears = _strategy.GetRefillYears(healthForRefillS2, config);
                double s2TargetCash = plan.Skip(yearIndex + 1).Take(s2RefillYears).Sum(x => x.S2Spend);
                double s2Refill = Math.Max(0, s2TargetCash - s2Cash);
                actualRefill2 = Math.Min(s2Refill, s2Etf);

                if (actualRefill2 > 0)
                {
                    s2Etf -= actualRefill2;
                    s2Cash += actualRefill2;
                    notes.Add($"S2:Refill{s2RefillYears}Yr");
                }
            }

            // --- SNAPSHOT: post‑refill, pre‑returns ---
            s1PostRefillTotal = s1Etf + s1Cash;
            s2PostRefillTotal = s2Etf + s2Cash;

            // === APPLY RETURN ===
            s1Etf *= 1 + mkt;
            s2Etf *= 1 + mkt;

            // === FINAL CASH SWEEP ===
            bool hasFutureS1Spend = plan.Skip(yearIndex + 1).Any(x => x.S1Spend > 0);
            bool hasFutureS2Spend = plan.Skip(yearIndex + 1).Any(x => x.S2Spend > 0);

            if (!hasFutureS1Spend && s1Cash > 0)
            {
                s1Etf += s1Cash;
                s1Cash = 0;
                notes.Add("S1:CashToEtf_NoFutureSpend");
            }

            if (!hasFutureS2Spend && s2Cash > 0)
            {
                s2Etf += s2Cash;
                s2Cash = 0;
                notes.Add("S2:CashToEtf_NoFutureSpend");
            }

            // === OBJECTIVE ACTUAL FUNDING ===
            double o1SippActual = o1SippFunded - s1CutO1 - s2CutO1;
            double o2SippActual = o2SippFunded - s1CutO2 - s2CutO2;
            double o3SippActual = o3SippFunded - s1CutO3 - s2CutO3;

            o1ActualFromSippTotal += o1SippActual;
            o2ActualFromSippTotal += o2SippActual;
            o3ActualFromSippTotal += o3SippActual;

            double S1_O1P = o1S1;
            double S1_O1A = o1S1 - s1CutO1;
            double S1_O2P = o2S1;
            double S1_O2A = o2S1 - s1CutO2;
            double S1_O3P = o3S1;
            double S1_O3A = o3S1 - s1CutO3;

            double S2_O1P = o1S2;
            double S2_O1A = o1S2 - s2CutO1;
            double S2_O2P = o2S2;
            double S2_O2A = o2S2 - s2CutO2;
            double S2_O3P = o3S2;
            double S2_O3A = o3S2 - s2CutO3;
            if (verbose)
            {
                var displayYear = $"{yearIndex + 1} ({calendarYear}) ";

                Console.WriteLine(
                    displayYear +
                    $"{mkt,6:P2} | " +

                    // --- S1 ---
                    $"{s1OpeningTotal,10:N0} | {s1H,4:F2} | " +
                    $"{s1Baseline * 0.8,8:N0} | {s1Baseline,8:N0} | {s1Baseline * 1.2,8:N0} | " +
                    $"{s1OpeningCash,10:N0} | {s1OpeningEtf,10:N0} | " +
                    $"{S1_GuardRails,8:N0} | " +
                    $"{S1_O1P,8:N0} | {S1_O1A,8:N0} | " +
                    $"{S1_O2P,8:N0} | {S1_O2A,8:N0} | " +
                    $"{S1_O3P,8:N0} | {S1_O3A,8:N0} | " +
                    $"{S1_Bonus,8:N0} | " +
                    $"{s1FromCash,8:N0} | {s1FromEtf,8:N0} | " +
                    $"{actualRefill1,8:N0} || " +

                    // --- S2 ---
                    $"{s2OpeningTotal,10:N0} | {s2H,4:F2} | " +
                    $"{s2Baseline * 0.8,8:N0} | {s2Baseline,8:N0} | {s2Baseline * 1.2,8:N0} | " +
                    $"{s2OpeningCash,10:N0} | {s2OpeningEtf,10:N0} | " +
                    $"{S2_GuardRails,8:N0} | " +
                    $"{S2_O1P,8:N0} | {S2_O1A,8:N0} | " +
                    $"{S2_O2P,8:N0} | {S2_O2A,8:N0} | " +
                    $"{S2_O3P,8:N0} | {S2_O3A,8:N0} | " +
                    $"{S2_Bonus,8:N0} | " +
                    $"{s2FromCash,8:N0} | {s2FromEtf,8:N0} | " +
                    $"{actualRefill2,8:N0} ||" +

                    $"{displayYear} | {(s1Baseline + s2Baseline) * 0.8,8:N0} | {(s1Baseline + s2Baseline),8:N0} | {s1OpeningTotal + s2OpeningTotal,8:N0} | {(s1Baseline + s2Baseline) * 1.2,8:N0} | " +
                    $"{string.Join(" | ", notes)}"
                );
            }


            // === DEPLETION CHECK ===
            double totalPot = s1Etf + s1Cash + s2Etf + s2Cash;
            if (depletionYearIndex == null && totalPot <= 1.0)
            {
                depletionYearIndex = yearIndex + 1;
            }

            // Track total balance by simulation year (for debug / analysis)
            double comingYearS1PV = ComputePVFromYearIndex(1, plan, yearIndex + 1, liabRate);
            double comingYearS2PV = ComputePVFromYearIndex(2, plan, yearIndex + 1, liabRate);
            annualTotalBalanceBySimYear.Add((comingYearS1PV + comingYearS2PV, totalPot));

            // track total payments by simulation year (for debug / analysis)
            var plannedPaymentsThisYear = S1_O1P + S2_O1P + S1_O2P + S2_O2P + S1_O3P + S2_O3P;
            var actualPaymentsThisYear = S1_O1A + S2_O1A + S1_O2A + S2_O2A + S1_O3A + S2_O3A + S1_Bonus + S2_Bonus;
            yearlyTotalPaymentsBySimYear.Add((plannedPaymentsThisYear, actualPaymentsThisYear));

            prevReturn = mkt;
        }

        double legacy = s1Etf + s1Cash + (s2Etf + s2Cash);
        double totalPlanned = simS1Planned + simS2Planned;
        double totalActual = s1Actual + s2Actual;
        double totalActualPlusLegacy = totalActual + legacy;

        double o1DeliveredIncExt = o1ExternalTotal + o1ActualFromSippTotal;
        double o2DeliveredIncExt = o2ExternalTotal + o2ActualFromSippTotal;
        double o3DeliveredIncExt = o3ExternalTotal + o3ActualFromSippTotal;

        var result = new SimulationResult
        {
            StartYear = startCalendarYear,
            SimYears = simYears,

            S1Planned = simS1Planned,
            S2Planned = simS2Planned,
            S1Actual = s1Actual,
            S2Actual = s2Actual,

            TotalPlanned30 = PlannedTotal30,
            TotalActualPlusLegacy30 = ActualPlusLegacyTotal30,

            Legacy = legacy,
            TotalPlanned = totalPlanned,
            TotalActualPlusLegacy = totalActualPlusLegacy,

            O1PlannedIncExt = o1TotalPlannedIncExt,
            O2PlannedIncExt = o2TotalPlannedIncExt,
            O3PlannedIncExt = o3TotalPlannedIncExt,

            O1DeliveredIncExt = o1DeliveredIncExt,
            O2DeliveredIncExt = o2DeliveredIncExt,
            O3DeliveredIncExt = o3DeliveredIncExt,

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
                simS1Planned, s1Actual,
                simS2Planned, s2Actual,
                s1Etf + s1Cash, s2Etf + s2Cash,
                csvO1Total, csvO2Total, csvO3Total,   // kept for signature compatibility
                o1TotalPlannedIncExt, o2TotalPlannedIncExt, o3TotalPlannedIncExt,
                o1ExternalTotal, o2ExternalTotal, o3ExternalTotal,
                o1PlannedFromSippTotal, o2PlannedFromSippTotal, o3PlannedFromSippTotal,
                o1ActualFromSippTotal, o2ActualFromSippTotal, o3ActualFromSippTotal,
                totalBonus,
                sippWithdrawalsBaseTotal,
                result.EndState
            );
        }

        return result;
    }

}
