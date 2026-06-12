using SippBucketDrawdown.Engines;
using SippBucketDrawdown.Shared;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static System.Formats.Asn1.AsnWriter;

namespace SippBucketDrawdown;

class Program
{
    // Unified scoring for combined backtest (used to determine "best in year" and overall winner in option 7)
    // All engines outputting good quality output for vebose.
    // Adjustments to overall scoring and bug fix to not returning sim.Years in Hybrid distorting wheighted scores
    // Hybrid now winning in all cases for front loaded Bob and Brenda plan.  When using flatter spending profile same, Momentum wins.. that is good nto knwo that the scoring is working and picking up the differences in outcomes.
    // Added monte-carlo simluation output on options 4 to 6.
    // fixed so hybrd payments are sipp payment not full planned and actual INCLUDING external
    // refactored to remove fixed constant values for largestO2EarlyCuts.  Now scores on second pass through data, so all values known before scoring.
    // combined back test output and scoring improved to fix NetOutcome from short duration issues and improved "weighted scoring".
    const string Version = "V5.0.4";

    static void Main()
    {
        // === LOAD CONFIG ===
        var config = LoadConfig("config.json");
        EngineBase.SetConfig(config);

        var plan = LoadPlan(config.SpendPlanFile);
        var returns = LoadReturns(config.MarketReturnsFile);

        double csvS1Total = plan.Sum(x => x.S1Spend);
        double csvS2Total = plan.Sum(x => x.S2Spend);
        double csvO1Total = plan.Sum(x => x.O1);
        double csvO2Total = plan.Sum(x => x.O2);
        double csvO3Total = plan.Sum(x => x.O3);

        Console.WriteLine("=== DATA SANITY CHECK (READING FROM CSV) ===");
        Console.WriteLine($"S1 Column Sum: £{csvS1Total:N0}");
        Console.WriteLine($"S2 Column Sum: £{csvS2Total:N0}");
        Console.WriteLine($"O1 Total:      £{csvO1Total:N0}");
        Console.WriteLine($"O2 Total:      £{csvO2Total:N0}");
        Console.WriteLine($"O3 Total:      £{csvO3Total:N0}");

        Console.WriteLine($"=== RETIREMENT STRESS TEST ({Version}) ===");
        Console.WriteLine("\nConfig:");
        Console.WriteLine($"  LiabilityDiscountRate      : {config.LiabilityDiscountRate:P2}");
        Console.WriteLine($"  CashBufferYearsHighHealth  : {config.Momentum.CashBufferYearsHighHealth}");
        Console.WriteLine($"  CashBufferYearsLowHealth   : {config.Momentum.CashBufferYearsLowHealth}");
        Console.WriteLine($"  CashBufferHealthThreshold  : {config.Momentum.CashBufferHealthThreshold:P2}");
        Console.WriteLine($"  SimulationStartYear        : {config.SimulationStartYear}");

        Console.WriteLine("\nSelect Mode / Strategy:");
        Console.WriteLine("  [1] Momentum Rebalance (single run)");
        Console.WriteLine("  [2] Dynamic Time Buckets (single run)");
        Console.WriteLine("  [3] Hybrid (single run)");
        Console.WriteLine("  [4] Momentum Rolling Backtest (all start years)");
        Console.WriteLine("  [5] Time Buckets Rolling Backtest (all start years)");
        Console.WriteLine("  [6] Hybrid Rolling Backtest (all start years)");
        Console.WriteLine("  [7] Combined Backtest (Mom vs 3TB vs Hybrid)");
        Console.WriteLine("  [8] Hybrid with different defensive 40% (CGT, 404020, Bonds, MMF)");
        Console.WriteLine("Append 'v' for verbose, e.g. '1v' or '3v'.");

        string choice = Console.ReadLine()?.Trim().ToLower() ?? "";
        bool verbose = choice.Contains("v");
        string mode = choice.Length > 1 ? choice.Substring(1, 1) : string.Empty;

        if (choice.StartsWith("4") || choice.StartsWith("5") || choice.StartsWith("6"))
        {
            ISimulationEngine backtestEngine =
                choice.StartsWith("4")
                    ? new MomentumEngine()
                    : choice.StartsWith("5") ? new ThreeTimeBucketEngine() : new Hybrid3BucketEngine();

            RunRollingBacktest(
                plan, returns, config,
                backtestEngine,
                mode,
                csvS1Total, csvS2Total,
                csvO1Total, csvO2Total, csvO3Total);
        }
        else if (choice.StartsWith("7"))
        {
            RunCombinedBacktest(
                plan, returns, config,
                verbose,
                csvS1Total, csvS2Total,
                csvO1Total, csvO2Total, csvO3Total);
        }
        else
        {
            ISimulationEngine engine =
                choice.StartsWith("2")
                    ? new ThreeTimeBucketEngine()
                    : choice.StartsWith("1") ? new MomentumEngine() : new Hybrid3BucketEngine();

            var result = engine.Run(
                plan, returns, config,
                verbose,
                csvS1Total, csvS2Total,
                csvO1Total, csvO2Total, csvO3Total,
                printSummary: true);
        }
    }

    // ======================================================================
    //  LOAD CONFIG
    // ======================================================================
    static Config LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"ERROR: Missing config file: {path}");
            Environment.Exit(1);
        }

        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        string json = File.ReadAllText(path);

        Config cfg = JsonSerializer.Deserialize<Config>(json, options);

        if (cfg == null)
        {
            Console.WriteLine("ERROR: Failed to parse config file.");
            Environment.Exit(1);
        }

        cfg.Guardrails ??= new GuardrailConfig();
        cfg.Bonus ??= new BonusConfig();
        cfg.BucketReturns ??= new BucketReturnsConfig();
        cfg.Momentum ??= new MomentumConfig();
        cfg.TimeBuckets ??= new TimeBucketConfig();
        cfg.Hybrid ??= new HybridConfig();   // ⭐ NEW LINE

        return cfg;
    }

    // ======================================================================
    //  ROLLING BACKTEST (SINGLE ENGINE) — OPTIONS 3 & 4
    // ======================================================================
    static void RunRollingBacktest(
        List<PlanRow> plan,
        List<(int Year, double MMF_RR, double EQUITY_RR, double BONDS_RR, double SythntheticCGT_RR, double Sythetic404020_RR)> marketReturns,
        Config config,
        ISimulationEngine engine,
        string mode,
        double csvS1Total,
        double csvS2Total,
        double csvO1Total,
        double csvO2Total,
        double csvO3Total)
    {
        bool verbose = mode == "v";
        Console.WriteLine("\n=== ROLLING BACKTEST ===");

        string strategyName = engine is ThreeTimeBucketEngine
            ? "Dynamic Time Buckets"
            : engine is MomentumEngine ? "Momentum Rebalance" : "Hybrid Engine";

        Console.WriteLine("Using strategy: " + strategyName);
        Console.WriteLine("Config:");
        Console.WriteLine($"  LiabilityDiscountRate      : {config.LiabilityDiscountRate:P2}");
        Console.WriteLine($"  CashBufferYearsHighHealth  : {config.Momentum.CashBufferYearsHighHealth}");
        Console.WriteLine($"  CashBufferYearsLowHealth   : {config.Momentum.CashBufferYearsLowHealth}");
        Console.WriteLine($"  CashBufferHealthThreshold  : {config.Momentum.CashBufferHealthThreshold:P2}");
        Console.WriteLine();

        Console.WriteLine("EndState Classification Rules:");
        Console.WriteLine("  PerfectPass : Survived full horizon, O1>=98%, O2>=90%, O3>=85%, NetOutcome>=0");
        Console.WriteLine("  Pass        : Survived full horizon, O1>=95%,  O2>=75%, O3>=60%, NetOutcome>=0");
        Console.WriteLine("  Fail        : Survived (but did not meet PASS thresholds) or depleted late (>=80% horizon)\r\n");
        Console.WriteLine("  BadFail     : Depletion before 80% of horizon OR O1<70%");
        Console.WriteLine();

        int fullPlanYears = plan.Count;

        var fullRuns = new List<(SimulationResult Result, EndState State)>();
        var partialRuns = new List<(SimulationResult Result, EndState State)>();

        List<List<(double planned, double actual)>> simulationYearlyPayments = new();
        List<List<(double baseline, double currentValue)>> simulationYearlyBalances = new();
        foreach (var start in marketReturns.Select(r => r.Year))
        {
            int idx = marketReturns.FindIndex(r => r.Year == start);
            if (idx < 0) continue;

            var usable = marketReturns.Skip(idx).ToList();
            int simYears = Math.Min(plan.Count, usable.Count);

            if (simYears < 5)
                continue;

            var tempConfig = new Config
            {
                SimulationStartYear = start,
                Sipp1StartingBalance = config.Sipp1StartingBalance,
                Sipp2StartingBalance = config.Sipp2StartingBalance,

                SpendPlanFile = config.SpendPlanFile,
                MarketReturnsFile = config.MarketReturnsFile,

                LiabilityDiscountRate = config.LiabilityDiscountRate,

                Guardrails = new GuardrailConfig
                {
                    Tier4Threshold = config.Guardrails.Tier4Threshold,
                    Tier3Threshold = config.Guardrails.Tier3Threshold,
                    Tier2Threshold = config.Guardrails.Tier2Threshold,
                    Tier1Threshold = config.Guardrails.Tier1Threshold
                },

                Bonus = new BonusConfig
                {
                    StartThreshold = config.Bonus.StartThreshold,
                    Tier2Threshold = config.Bonus.Tier2Threshold,
                    Tier3Threshold = config.Bonus.Tier3Threshold,
                    Tier4Threshold = config.Bonus.Tier4Threshold,
                    Tier5Threshold = config.Bonus.Tier5Threshold,

                    PctTier1 = config.Bonus.PctTier1,
                    PctTier2 = config.Bonus.PctTier2,
                    PctTier3 = config.Bonus.PctTier3,
                    PctTier4 = config.Bonus.PctTier4,
                    PctTier5 = config.Bonus.PctTier5
                },

                Momentum = new MomentumConfig
                {
                    CashBufferYearsHighHealth = config.Momentum.CashBufferYearsHighHealth,
                    CashBufferYearsLowHealth = config.Momentum.CashBufferYearsLowHealth,
                    CashBufferHealthThreshold = config.Momentum.CashBufferHealthThreshold
                },

                TimeBuckets = new TimeBucketConfig
                {
                    Bucket1Years = config.TimeBuckets.Bucket1Years,
                    Bucket2Years = config.TimeBuckets.Bucket2Years,

                    FundingStateExtremeBadThreshold = config.TimeBuckets.FundingStateExtremeBadThreshold,
                    FundingStateBadThreshold = config.TimeBuckets.FundingStateBadThreshold,
                    FundingStateGoodThreshold = config.TimeBuckets.FundingStateGoodThreshold
                }
            };

            var result = engine.Run(
                plan, marketReturns,
                tempConfig,
                verbose,
                csvS1Total, csvS2Total,
                csvO1Total, csvO2Total, csvO3Total,
                printSummary: false);

            if (simYears == fullPlanYears)
                fullRuns.Add((result, result.EndState));
            else
                partialRuns.Add((result, result.EndState));

            simulationYearlyBalances.Add(result.YearlyTotalBalanceBySimYear);
            simulationYearlyPayments.Add(result.YearlyTotalPaymentsBySimYear);
        }

        if (mode.ToLower().Contains("b"))
        {
            // Produce a combined list of all simulation balance trajectories (for visual sanity check that they look reasonable and to spot any weird outliers)
            Console.WriteLine("\n\n=== Combined list of all simulation balance trajectories ===\n");
            var returnYears = marketReturns.Select(r => r.Year).ToList();
            var titleRow = $"PlanYear | LowerGuard | baseline | UpperGuard";
            for (int i = 0; i < simulationYearlyBalances.Count; i++)
                titleRow += $" | {returnYears[i]}";
            Console.WriteLine($"{titleRow}");
            var simYear = 0;
            for (var planYear = 1; planYear < plan.Count + 1; planYear++)
            {
                var combinedListOutput = $"{planYear}";
                var simNo = 1;
                foreach (var yearlyBalances in simulationYearlyBalances)
                {
                    if (simNo == 1)
                    {
                        combinedListOutput += $" | {yearlyBalances[simYear].baseline * 0.8:N0} | {yearlyBalances[simYear].baseline:N0} | {yearlyBalances[simYear].baseline * 1.2:N0} ";
                    }
                    if (simYear < yearlyBalances.Count)
                    {
                        combinedListOutput += $" | {yearlyBalances[simYear].currentValue:N0}";
                    }
                    else
                    {
                        combinedListOutput += " | ";
                    }
                    simNo++;
                }

                Console.WriteLine($"{combinedListOutput}");
                simYear++;
            }
        }

        if (mode.ToLower().Contains("p"))
        {
            // Produce a combined list of all simulation payments (for visual sanity check that they look reasonable and to spot any weird outliers)
            Console.WriteLine("\n\n=== Combined list of all simulation actual vs planned payments ===\n");
            var returnYears = marketReturns.Select(r => r.Year).ToList();
            var titleRow = $"PlanYear | Planned ";
            for (int i = 0; i < simulationYearlyBalances.Count; i++)
                titleRow += $" | {returnYears[i]}";
            Console.WriteLine($"{titleRow}");
            var simYear = 0;
            for (var planYear = 1; planYear < plan.Count + 1; planYear++)
            {
                var combinedListOutput = $"{planYear}";
                var simNo = 1;
                foreach (var yearlyPayments in simulationYearlyPayments)
                {
                    if (simNo == 1)
                    {
                        combinedListOutput += $" | {yearlyPayments[simYear].planned:N0} ";
                    }
                    if (simYear < yearlyPayments.Count)
                    {
                        combinedListOutput += $" | {yearlyPayments[simYear].actual:N0}";
                    }
                    else
                    {
                        combinedListOutput += " | ";
                    }
                    simNo++;
                }

                Console.WriteLine($"{combinedListOutput}");
                simYear++;
            }
        }

        Console.WriteLine("\n\n=== FULL SIMULATIONS (SimYears == full plan length) ===");
        PrintBacktestGroup(fullRuns);

        Console.WriteLine("\n\n=== PARTIAL SIMULATIONS (5 <= SimYears < full plan length) ===");
        PrintBacktestGroup(partialRuns);
    }

    static void PrintBacktestGroup(List<(SimulationResult Result, EndState State)> runs)
    {
        if (runs.Count == 0)
        {
            Console.WriteLine("(No runs in this category)");
            return;
        }

        Console.WriteLine("\n=== BACKTEST OUTCOME TABLE ===");
        Console.WriteLine("StartYear | SimYears |   O1%  |   O2%  |   O3%  |   Bonus   |   Legacy   |   NetOutcome   | EndState");
        Console.WriteLine("--------------------------------------------------------------------------------------------------------");

        int perfect = 0, pass = 0, fail = 0, bad = 0;

        foreach (var entry in runs)
        {
            var r = entry.Result;
            var s = entry.State;

            switch (s)
            {
                case EndState.PerfectPass: perfect++; break;
                case EndState.Pass: pass++; break;
                case EndState.Fail: fail++; break;
                case EndState.BadFail: bad++; break;
            }

            Console.WriteLine(
                $"{r.StartYear,9} | {r.SimYears,8} | {r.O1Pct,6:P1} | {r.O2Pct,6:P1} | {r.O3Pct,6:P1} | £{r.Bonus,9:N0} | £{r.Legacy,9:N0} | £{r.NetOutcome,12:N0} | {s}");
        }

        Console.WriteLine("\n=== BACKTEST SUMMARY BY END STATE ===");
        Console.WriteLine($"PerfectPass : {perfect}");
        Console.WriteLine($"Pass        : {pass}");
        Console.WriteLine($"Fail        : {fail}");
        Console.WriteLine($"BadFail     : {bad}");
    }

    // ======================================================================
    //  COMBINED BACKTEST (MOMENTUM VS TIME BUCKETS VS HYBRID) — OPTION 7
    // ======================================================================
    static void RunCombinedBacktest(
        List<PlanRow> plan,
        List<(int Year, double MMF_RR, double EQUITY_RR, double BONDS_RR, double SythntheticCGT_RR, double Sythetic404020_RR)> marketReturns,
        Config config,
        bool verbose,
        double csvS1Total,
        double csvS2Total,
        double csvO1Total,
        double csvO2Total,
        double csvO3Total)
    {
        Console.WriteLine("\n=== COMBINED BACKTEST (Momentum vs Time Buckets vs Hybrid) ===");

        Console.WriteLine("EndState Classification Rules:");
        Console.WriteLine("  PerfectPass : Survived full horizon, O1>=95%, O2>=80%, O3>=70%, NetOutcome>=0");
        Console.WriteLine("  Pass        : Survived full horizon, O1>=85%,  O2>=50%, O3>=30%, NetOutcome>=0");
        Console.WriteLine("  Fail        : Survived (but did not meet PASS thresholds) or depleted late (>=80% horizon)\r\n");
        Console.WriteLine("  BadFail     : Depletion before 80% of horizon OR O1<70%");
        Console.WriteLine();

        int fullPlanYears = plan.Count;

        var momentumEngine = new MomentumEngine();
        var timeEngine = new ThreeTimeBucketEngine();
        var hybridEngine = new Hybrid3BucketEngine();

        var rows = new List<(SimulationResult Mom, SimulationResult Time, SimulationResult Hyb)>();

        // Weighted aggregates for Momentum
        double momPerfect = 0, momPass = 0, momFail = 0, momBad = 0;
        double momNetOutcomeSum = 0;
        double momNegNetOutcome = 0;
        double momO1PctSum = 0, momO2PctSum = 0, momO3PctSum = 0;
        double momBonusSum = 0;

        // Weighted aggregates for TimeBuckets
        double timePerfect = 0, timePass = 0, timeFail = 0, timeBad = 0;
        double timeNetOutcomeSum = 0;
        double timeNegNetOutcome = 0;
        double timeO1PctSum = 0, timeO2PctSum = 0, timeO3PctSum = 0;
        double timeBonusSum = 0;

        // Weighted aggregates for Hybrid
        double hybPerfect = 0, hybPass = 0, hybFail = 0, hybBad = 0;
        double hybNetOutcomeSum = 0;
        double hybNegNetOutcome = 0;
        double hybO1PctSum = 0, hybO2PctSum = 0, hybO3PctSum = 0;
        double hybBonusSum = 0;

        double totalWeight = 0;
        bool printedShortHeader = false;

        Dictionary<int, SimulationResult> momByYear = new();
        Dictionary<int, SimulationResult> timeByYear = new();
        Dictionary<int, SimulationResult> hybByYear = new();

        foreach (var start in marketReturns.Select(r => r.Year))
        {
            int idx = marketReturns.FindIndex(r => r.Year == start);
            if (idx < 0) continue;

            var usable = marketReturns.Skip(idx).ToList();
            int simYears = Math.Min(plan.Count, usable.Count);

            if (simYears < 5)
                continue;

            // Weighting factor
            double weight = (double)simYears / fullPlanYears;
            totalWeight += weight;

            // Separator row when dropping below 30 years
            if (simYears < 30 && !printedShortHeader)
            {
                Console.WriteLine("\n----- SHORT SIMULATIONS BEGIN BELOW THIS POINT (WEIGHTED LESS IN SUMMARY) -----\n");
                printedShortHeader = true;
            }

            var tempConfig = new Config
            {
                SimulationStartYear = start,
                Sipp1StartingBalance = config.Sipp1StartingBalance,
                Sipp2StartingBalance = config.Sipp2StartingBalance,

                SpendPlanFile = config.SpendPlanFile,
                MarketReturnsFile = config.MarketReturnsFile,

                LiabilityDiscountRate = config.LiabilityDiscountRate,

                Guardrails = new GuardrailConfig
                {
                    Tier4Threshold = config.Guardrails.Tier4Threshold,
                    Tier3Threshold = config.Guardrails.Tier3Threshold,
                    Tier2Threshold = config.Guardrails.Tier2Threshold,
                    Tier1Threshold = config.Guardrails.Tier1Threshold
                },

                Bonus = new BonusConfig
                {
                    StartThreshold = config.Bonus.StartThreshold,
                    Tier2Threshold = config.Bonus.Tier2Threshold,
                    Tier3Threshold = config.Bonus.Tier3Threshold,
                    Tier4Threshold = config.Bonus.Tier4Threshold,
                    Tier5Threshold = config.Bonus.Tier5Threshold,

                    PctTier1 = config.Bonus.PctTier1,
                    PctTier2 = config.Bonus.PctTier2,
                    PctTier3 = config.Bonus.PctTier3,
                    PctTier4 = config.Bonus.PctTier4,
                    PctTier5 = config.Bonus.PctTier5
                },

                Momentum = new MomentumConfig
                {
                    CashBufferYearsHighHealth = config.Momentum.CashBufferYearsHighHealth,
                    CashBufferYearsLowHealth = config.Momentum.CashBufferYearsLowHealth,
                    CashBufferHealthThreshold = config.Momentum.CashBufferHealthThreshold
                },

                TimeBuckets = new TimeBucketConfig
                {
                    Bucket1Years = config.TimeBuckets.Bucket1Years,
                    Bucket2Years = config.TimeBuckets.Bucket2Years,

                    FundingStateExtremeBadThreshold = config.TimeBuckets.FundingStateExtremeBadThreshold,
                    FundingStateBadThreshold = config.TimeBuckets.FundingStateBadThreshold,
                    FundingStateGoodThreshold = config.TimeBuckets.FundingStateGoodThreshold
                },

                Hybrid = new HybridConfig
                {
                    AllocationB2 = config.Hybrid.AllocationB2,
                    AllocationB3 = config.Hybrid.AllocationB3,
                    B2HealthDiscountAdjustment = config.Hybrid.B2HealthDiscountAdjustment,
                    B3HealthDiscountAdjustment = config.Hybrid.B3HealthDiscountAdjustment,
                    B2HealthGoodThreshold = config.Hybrid.B2HealthGoodThreshold,
                    B3HealthGoodThreshold = config.Hybrid.B3HealthGoodThreshold
                }
            };

            var mom = momentumEngine.Run(
                plan, marketReturns,
                tempConfig,
                verbose: false,
                csvS1Total, csvS2Total,
                csvO1Total, csvO2Total, csvO3Total,
                printSummary: false);

            momByYear[start] = mom;

            var time = timeEngine.Run(
                plan, marketReturns,
                tempConfig,
                verbose: false,
                csvS1Total, csvS2Total,
                csvO1Total, csvO2Total, csvO3Total,
                printSummary: false);

            timeByYear[start] = time;

            var hyb = hybridEngine.Run(
                plan, marketReturns,
                tempConfig,
                verbose: false,
                csvS1Total, csvS2Total,
                csvO1Total, csvO2Total, csvO3Total,
                printSummary: false);

            hybByYear[start] = hyb;

            rows.Add((mom, time, hyb));

            // Weighted Momentum stats
            switch (mom.EndState)
            {
                case EndState.PerfectPass: momPerfect += weight; break;
                case EndState.Pass: momPass += weight; break;
                case EndState.Fail: momFail += weight; break;
                case EndState.BadFail: momBad += weight; break;
            }

            momNetOutcomeSum += mom.NetOutcome * weight;
            if (mom.NetOutcome < 0) momNegNetOutcome += weight;
            momO1PctSum += mom.O1Pct * weight;
            momO2PctSum += mom.O2Pct * weight;
            momO3PctSum += mom.O3Pct * weight;
            momBonusSum += mom.Bonus * weight;

            // Weighted Time stats
            switch (time.EndState)
            {
                case EndState.PerfectPass: timePerfect += weight; break;
                case EndState.Pass: timePass += weight; break;
                case EndState.Fail: timeFail += weight; break;
                case EndState.BadFail: timeBad += weight; break;
            }

            timeNetOutcomeSum += time.NetOutcome * weight;
            if (time.NetOutcome < 0) timeNegNetOutcome += weight;
            timeO1PctSum += time.O1Pct * weight;
            timeO2PctSum += time.O2Pct * weight;
            timeO3PctSum += time.O3Pct * weight;
            timeBonusSum += time.Bonus * weight;

            // Weighted Hybrid stats
            switch (hyb.EndState)
            {
                case EndState.PerfectPass: hybPerfect += weight; break;
                case EndState.Pass: hybPass += weight; break;
                case EndState.Fail: hybFail += weight; break;
                case EndState.BadFail: hybBad += weight; break;
            }

            hybNetOutcomeSum += hyb.NetOutcome * weight;
            if (hyb.NetOutcome < 0) hybNegNetOutcome += weight;
            hybO1PctSum += hyb.O1Pct * weight;
            hybO2PctSum += hyb.O2Pct * weight;
            hybO3PctSum += hyb.O3Pct * weight;
            hybBonusSum += hyb.Bonus * weight;
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("No valid runs for combined backtest.");
            return;
        }

        // Now we have all the calculations, we can identify the early cuts (first 10 years) across all sim years which is used in scoring
        double largestO2EarlyCuts = rows.Max(r =>
            new[] { r.Mom, r.Time, r.Hyb }.Max(x => x.EarlyO2Cuts));

        double largestO3EarlyCuts = rows.Max(r =>
            new[] { r.Mom, r.Time, r.Hyb }.Max(x => x.EarlyO3Cuts));

        double largestTotalEarlyCuts = rows.Max(r =>
            new[] { r.Mom, r.Time, r.Hyb }
                .Max(x => x.EarlyO2Cuts + x.EarlyO3Cuts));

        // === PRINT TABLE ===
        Console.WriteLine("\n=== COMBINED BACKTEST OUTCOME TABLE ===");
        Console.WriteLine("StartYear | SimYears | Mom_O1% | Time_O1% | Hyb_O1% | Mom_O2% | Time_O2% | Hyb_O2% | Mom_O3% | Time_O3% | Hyb_O3% | Mom_NetOutcome | Time_NetOutcome | Hyb_NetOutcome | Mom_Bonus  | Time_Bonus | Hyb_Bonus | Mom_EndState | Time_EndState | Hyb_EndState | BestInYear");
        Console.WriteLine("---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------");

        bool separatorPrinted2 = false;

        foreach (var row in rows.OrderByDescending(r => r.Mom.SimYears))
        {
            var m = row.Mom;
            var t = row.Time;
            var h = row.Hyb;

            var bestNet = new[] { m, t, h }.OrderByDescending(r => r.NetOutcome).First();
            m.Score = ScoreSimulationOutcome(fullPlanYears, m, bestNet);
            t.Score = ScoreSimulationOutcome(fullPlanYears, t, bestNet);
            h.Score = ScoreSimulationOutcome(fullPlanYears, h, bestNet);

            var bestInYear = new[]
{
                ("Momentum", m),
                ("TimeBuckets", t),
                ("Hybrid", h)
            }.OrderByDescending(x => x.Item2.Score.ScoreValue)
             .First().Item1;

            // Print separator exactly when we cross below 30 years
            if (!separatorPrinted2 && m.SimYears < 30)
            {
                Console.WriteLine("\n----- SHORT SIMULATIONS BEGIN BELOW THIS POINT (WEIGHTED LESS IN SUMMARY) -----\n");
                separatorPrinted2 = true;
            }

            Console.WriteLine(
                $"{m.StartYear,9} | {m.SimYears,8} | " +
                $"{m.O1Pct,6:P1} | {t.O1Pct,8:P1} | {h.O1Pct,7:P1} | " +
                $"{m.O2Pct,6:P1} | {t.O2Pct,8:P1} | {h.O2Pct,7:P1} | " +
                $"{m.O3Pct,6:P1} | {t.O3Pct,8:P1} | {h.O3Pct,7:P1} | " +
                $"£{m.NetOutcome,13:N0} | £{t.NetOutcome,14:N0} | £{h.NetOutcome,13:N0} | " +
                $"£{m.Bonus,9:N0} | £{t.Bonus,10:N0} | £{h.Bonus,9:N0} | " +
                $"{m.EndState,12} | {t.EndState,13} | {h.EndState,12} | {bestInYear}");
        }

        var MomVsHybDiffO1 = rows.Where(r => r.Mom.O1Pct != r.Hyb.O1Pct).ToList();

        //// === WEIGHTED AVERAGES ===
        //double momAvgNet = momNetOutcomeSum / totalWeight;
        //double timeAvgNet = timeNetOutcomeSum / totalWeight;
        //double hybAvgNet = hybNetOutcomeSum / totalWeight;

        //double momAvgO1 = momO1PctSum / totalWeight;
        //double momAvgO2 = momO2PctSum / totalWeight;
        //double momAvgO3 = momO3PctSum / totalWeight;

        //double timeAvgO1 = timeO1PctSum / totalWeight;
        //double timeAvgO2 = timeO2PctSum / totalWeight;
        //double timeAvgO3 = timeO3PctSum / totalWeight;

        //double hybAvgO1 = hybO1PctSum / totalWeight;
        //double hybAvgO2 = hybO2PctSum / totalWeight;
        //double hybAvgO3 = hybO3PctSum / totalWeight;

        //// === PRINT SUMMARY ===
        //Console.WriteLine("\n=== MOMENTUM SUMMARY (Weighted) ===");
        //Console.WriteLine($"Weighted PerfectPass : {momPerfect:N2}");
        //Console.WriteLine($"Weighted Pass        : {momPass:N2}");
        //Console.WriteLine($"Weighted Fail        : {momFail:N2}");
        //Console.WriteLine($"Weighted BadFail     : {momBad:N2}");
        //Console.WriteLine($"Weighted Avg NetOutcome : £{momAvgNet:N0}");
        //Console.WriteLine($"Weighted Negative NetOutcome : {momNegNetOutcome:N2}");
        //Console.WriteLine($"Weighted Avg O1% : {momAvgO1:P1}");
        //Console.WriteLine($"Weighted Avg O2% : {momAvgO2:P1}");
        //Console.WriteLine($"Weighted Avg O3% : {momAvgO3:P1}");
        //Console.WriteLine($"Weighted Total Bonus : £{momBonusSum:N0}");

        //Console.WriteLine("\n=== TIME BUCKETS SUMMARY (Weighted) ===");
        //Console.WriteLine($"Weighted PerfectPass : {timePerfect:N2}");
        //Console.WriteLine($"Weighted Pass        : {timePass:N2}");
        //Console.WriteLine($"Weighted Fail        : {timeFail:N2}");
        //Console.WriteLine($"Weighted BadFail     : {timeBad:N2}");
        //Console.WriteLine($"Weighted Avg NetOutcome : £{timeAvgNet:N0}");
        //Console.WriteLine($"Weighted Negative NetOutcome : {timeNegNetOutcome:N2}");
        //Console.WriteLine($"Weighted Avg O1% : {timeAvgO1:P1}");
        //Console.WriteLine($"Weighted Avg O2% : {timeAvgO2:P1}");
        //Console.WriteLine($"Weighted Avg O3% : {timeAvgO3:P1}");
        //Console.WriteLine($"Weighted Total Bonus : £{timeBonusSum:N0}");

        //Console.WriteLine("\n=== HYBRID SUMMARY (Weighted) ===");
        //Console.WriteLine($"Weighted PerfectPass : {hybPerfect:N2}");
        //Console.WriteLine($"Weighted Pass        : {hybPass:N2}");
        //Console.WriteLine($"Weighted Fail        : {hybFail:N2}");
        //Console.WriteLine($"Weighted BadFail     : {hybBad:N2}");
        //Console.WriteLine($"Weighted Avg NetOutcome : £{hybAvgNet:N0}");
        //Console.WriteLine($"Weighted Negative NetOutcome : {hybNegNetOutcome:N2}");
        //Console.WriteLine($"Weighted Avg O1% : {hybAvgO1:P1}");
        //Console.WriteLine($"Weighted Avg O2% : {hybAvgO2:P1}");
        //Console.WriteLine($"Weighted Avg O3% : {hybAvgO3:P1}");
        //Console.WriteLine($"Weighted Total Bonus : £{hybBonusSum:N0}");

        // Create a comnbined list of all outcomes ordered by the average net-outcome.  
        //var regimeOrderedYears =
        //    momByYear.Keys
        //        .Where(y => timeByYear.ContainsKey(y) &&
        //                    hybByYear.ContainsKey(y))
        //        .Select(y => new
        //        {
        //            StartYear = y,
        //            AverageNetOutcome =
        //                (momByYear[y].NetOutcome +
        //                 timeByYear[y].NetOutcome +
        //                 hybByYear[y].NetOutcome) / 3.0,

        //            Momentum = momByYear[y],
        //            TimeBuckets = timeByYear[y],
        //            Hybrid = hybByYear[y]
        //        })
        //        .OrderBy(x => x.AverageNetOutcome)
        //        .ToList();

        var regimeOrderedYears30YearsMin =
            momByYear.Keys
                .Where(y =>
                    timeByYear.ContainsKey(y) &&
                    hybByYear.ContainsKey(y) &&

                    // We do not trust any sims that run for less than 30 years!
                    momByYear[y].SimYears >= 30 &&
                    timeByYear[y].SimYears >= 30 &&
                    hybByYear[y].SimYears >= 30
                )
                .Select(y => new
                {
                    StartYear = y,
                    AverageNetOutcome30 =
                        (momByYear[y].NetOutcome30 +
                         timeByYear[y].NetOutcome30 +
                         hybByYear[y].NetOutcome30) / 3.0,

                    Momentum = momByYear[y],
                    TimeBuckets = timeByYear[y],
                    Hybrid = hybByYear[y]
                })
                .OrderBy(x => x.AverageNetOutcome30)
                .ToList();


        // DEBUG List of worst years based on average of O2 and O3 early cuts (worst first) - this is used in the "Bob & Brenda" regime scoring approach.
        //1978 - best
        //1979
        //1980
        //1981
        //1982
        //1983
        //1984
        //1985
        //1986
        //1987
        //1988
        //1989
        //1990
        //1991
        //1992
        //1993
        //1994
        //1995
        //1996
        //1977
        //1966
        //1976
        //1967
        //1975
        //1968
        //1969
        //1971
        //1970
        //1972
        //1974
        //1973 - worst
        var worstFirst10OrderedYears30YearsMin =
        momByYear.Keys
            .Where(y =>
                timeByYear.ContainsKey(y) &&
                hybByYear.ContainsKey(y) &&

                // We do not trust any sims that run for less than 30 years!
                momByYear[y].SimYears >= 30 &&
                timeByYear[y].SimYears >= 30 &&
                hybByYear[y].SimYears >= 30
            )
            .Select(y => new
            {
                StartYear = y,
                AverageWorstTen30 =
                    ((momByYear[y].EarlyO2Cuts + momByYear[y].EarlyO3Cuts) +
                     (timeByYear[y].EarlyO2Cuts + timeByYear[y].EarlyO3Cuts) +
                     (hybByYear[y].EarlyO2Cuts + hybByYear[y].EarlyO3Cuts)) / 3.0,

                Momentum = momByYear[y],
                TimeBuckets = timeByYear[y],
                Hybrid = hybByYear[y]
            })
            .OrderBy(x => x.AverageWorstTen30)
            .ToList();
        var yearsString = string.Join(Environment.NewLine, worstFirst10OrderedYears30YearsMin.Select(x => x.StartYear));

        // == Now work out groups for "bad, normal and good" regime scoring.
        int regimeSize = Math.Min(3, regimeOrderedYears30YearsMin.Count / 3);

        //var badYears = regimeOrderedYears30YearsMin
        //    .Take(regimeSize)
        //    .ToList();

        //var goodYears = regimeOrderedYears30YearsMin
        //    .TakeLast(regimeSize)
        //    .ToList();

        // Prevent best and worst years being too close to each other (e.g. 1966 and 1969) which would make the regime scoring less meaningful.
        const int minYearGap = 5;

        var badYears = SelectSpacedYears(
            regimeOrderedYears30YearsMin,
            regimeSize,
            x => x.StartYear,
            minYearGap);

        var goodYears = SelectSpacedYears(
            regimeOrderedYears30YearsMin.AsEnumerable().Reverse(),
            regimeSize,
            x => x.StartYear,
            minYearGap);

        var normalYears = regimeOrderedYears30YearsMin
            .Skip((regimeOrderedYears30YearsMin.Count / 2) - (regimeSize / 2))
            .Take(regimeSize)
            .ToList();

        var allNormalYears = regimeOrderedYears30YearsMin
            .Skip(regimeSize)
            .Take(regimeOrderedYears30YearsMin.Count - (regimeSize * 2))
            .ToList();

        // Collect regime simulations
        var momBadYearSims = badYears.Select(x => x.Momentum).ToList();
        var timeBadYearSims = badYears.Select(x => x.TimeBuckets).ToList();
        var hybBadYearSims = badYears.Select(x => x.Hybrid).ToList();

        var momNormalYearSims = normalYears.Select(x => x.Momentum).ToList();
        var timeNormalYearSims = normalYears.Select(x => x.TimeBuckets).ToList();
        var hybNormalYearSims = normalYears.Select(x => x.Hybrid).ToList();

        var momGoodYearSims = goodYears.Select(x => x.Momentum).ToList();
        var timeGoodYearSims = goodYears.Select(x => x.TimeBuckets).ToList();
        var hybGoodYearSims = goodYears.Select(x => x.Hybrid).ToList();

        var momAllNormalYearSims = allNormalYears.Select(x => x.Momentum).ToList();
        var timeAllNormalYearSims = allNormalYears.Select(x => x.TimeBuckets).ToList();
        var hybAllNormalYearSims = allNormalYears.Select(x => x.Hybrid).ToList();


        double momOverall =
            WeightedAverageScore(
                momBadYearSims,
                momAllNormalYearSims,
                momGoodYearSims);

        double timeOverall =
            WeightedAverageScore(
                timeBadYearSims,
                timeAllNormalYearSims,
                timeGoodYearSims);

        double hybOverall =
            WeightedAverageScore(
                hybBadYearSims,
                hybAllNormalYearSims,
                hybGoodYearSims);

        var overallResults = new[]
        {
            ("Momentum", momOverall),
            ("TimeBuckets", timeOverall),
            ("Hybrid", hybOverall)
        }
        .OrderByDescending(x => x.Item2)
        .ToList();

        var overallWinner = overallResults.First();

        Console.WriteLine("\n=== OVERALL WINNER (Regime Weighted) ===");
        Console.WriteLine(
            $"Best Overall Strategy: {overallWinner.Item1} " +
            $"(Score: {overallWinner.Item2:F2})");

        Console.WriteLine();
        Console.WriteLine("Rank | Strategy      | Score");
        Console.WriteLine("--------------------------------");

        for (int i = 0; i < overallResults.Count; i++)
        {
            var r = overallResults[i];

            Console.WriteLine(
                $"{i + 1,4} | {r.Item1,-13} | {r.Item2,5:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("Scoring approach:");
        Console.WriteLine("  • Avg bad year score weighted x1.25");
        Console.WriteLine("  • Avg years excl. good / bad outliers weighted x1.00");
        Console.WriteLine("  • Avg good years weighted x1.1");
        Console.WriteLine("  • Based on YearScore averages within each regime");

        Console.WriteLine("\n=== MARKET REGIME SCORING (Bob & Brenda view) ===");

        // Average YearScore by regime
        // We use unwighted scores here so everyting scales back up to the "same levels of value"
        double AvgScore(List<SimulationResult> sims) =>
            sims.Average(x => x.Score.ScoreValueUnWheighted);

        // Dynamic titles
        string badTitle =
            $"BAD ({string.Join(", ", badYears.Select(x => x.StartYear))})";

        string normalTitle =
            $"NORMAL ({string.Join(", ", normalYears.Select(x => x.StartYear))})";

        string goodTitle =
            $"GOOD ({string.Join(", ", goodYears.Select(x => x.StartYear))})";

        // Simple reason helper
        string BuildReason(List<SimulationResult> sims)
        {
            double avgScore = sims.Average(x => x.Score.ScoreValue);
            double avgO1 = sims.Average(x => x.O1Pct);
            double avgO2 = sims.Average(x => x.O2Pct);
            double avgO3 = sims.Average(x => x.O3Pct);
            double avgNet = sims.Average(x => x.NetOutcome);

            return
                $"• Avg Weighted YearScore: {avgScore:F1}\n" +
                $"• Avg O1/O2/O3: {avgO1:P1}, {avgO2:P1}, {avgO3:P1}\n" +
                $"• Avg NetOutcome: £{avgNet:N0}";
        }

        // === THREE TABLES ===
        PrintRegimeTable3(
            badTitle,
            ("Momentum",
                AvgScore(momBadYearSims),
                BuildReason(momBadYearSims)),
            ("TimeBuckets",
                AvgScore(timeBadYearSims),
                BuildReason(timeBadYearSims)),
            ("Hybrid",
                AvgScore(hybBadYearSims),
                BuildReason(hybBadYearSims)));

        PrintRegimeTable3(
            normalTitle,
            ("Momentum",
                AvgScore(momNormalYearSims),
                BuildReason(momNormalYearSims)),
            ("TimeBuckets",
                AvgScore(timeNormalYearSims),
                BuildReason(timeNormalYearSims)),
            ("Hybrid",
                AvgScore(hybNormalYearSims),
                BuildReason(hybNormalYearSims)));

        PrintRegimeTable3(
            goodTitle,
            ("Momentum",
                AvgScore(momGoodYearSims),
                BuildReason(momGoodYearSims)),
            ("TimeBuckets",
                AvgScore(timeGoodYearSims),
                BuildReason(timeGoodYearSims)),
            ("Hybrid",
                AvgScore(hybGoodYearSims),
                BuildReason(hybGoodYearSims)));

        // ---- local helpers for regime printing & reasons ----

        static void PrintRegimeTable3(
            string title,
            (string Name, double Score, string Reason) e1,
            (string Name, double Score, string Reason) e2,
            (string Name, double Score, string Reason) e3)
        {
            Console.WriteLine($"\n=== {title} REGIME ===");
            Console.WriteLine("Engine       | Score (unweighted) | Reason");
            Console.WriteLine("------------------------------------------------------------");

            void printEngine((string Name, double Score, string Reason) e)
            {
                Console.WriteLine($"{e.Name,-12} | {e.Score,5:F1} |");
                foreach (var line in e.Reason.Split('\n'))
                    Console.WriteLine($"             |       | {line}");
                Console.WriteLine();
            }

            printEngine(e1);
            printEngine(e2);
            printEngine(e3);
        }

        static double Scale(double value, double inMin, double inMax, double outMin, double outMax)
        {
            if (inMax <= inMin) return outMin;
            double t = (value - inMin) / (inMax - inMin);
            t = Math.Max(0, Math.Min(1, t));
            return outMin + t * (outMax - outMin);
        }

        double WeightedAverageScore(
            List<SimulationResult> bad,
            List<SimulationResult> normal,
            List<SimulationResult> good)
        {
            const double badWeight = 1.25;
            const double normalWeight = 1.0;
            const double goodWeight = 1.1;

            double weightedTotal =
                (bad.Average(x => x.Score.ScoreValue) * badWeight) +
                (normal.Average(x => x.Score.ScoreValue) * normalWeight) +
                (good.Average(x => x.Score.ScoreValue) * goodWeight);

            double totalWeight =
                badWeight + normalWeight + goodWeight;

            return weightedTotal / totalWeight;
        }


        SimulationResult.YearScore ScoreSimulationOutcome(
            int fullPlanYears,
            SimulationResult me,
            SimulationResult bestNetOutcomeThisYear)
        {
            SimulationResult.YearScore yearScore = new();
            string scoreAudit = "";
            double score = 0;
            double ruleScore = 0;
            // ======================================================
            // Spending objectives (core priorities)
            // ======================================================

            ruleScore = me.O1Pct * 30;
            scoreAudit += $"O1: {me.O1Pct:P1} * 30 = {ruleScore:F1}\n";
            score += ruleScore;
            ruleScore = me.O2Pct * 15;
            scoreAudit += $"O2: {me.O2Pct:P1} * 15 = {ruleScore:F1}\n";
            score += ruleScore;
            ruleScore = me.O3Pct * 10;
            scoreAudit += $"O3: {me.O3Pct:P1} * 10 = {ruleScore:F1}\n";
            score += ruleScore;

            // ======================================================
            // Penalise painful early cuts in first 10 years
            // ======================================================

            double o2Rate = me.EarlyO2Cuts / Math.Min(me.SimYears, 10);
            ruleScore = Scale(
                o2Rate,
                0,
                largestO2EarlyCuts / 10.0,
                0,
                15);  // higher weight than before
            score -= ruleScore;
            scoreAudit += $"O2 Early Cuts: {me.EarlyO2Cuts} over {Math.Min(me.SimYears, 10)} years = {o2Rate:F4} rate, penalty = {ruleScore:F1}\n";

            double o3Rate = me.EarlyO3Cuts / Math.Min(me.SimYears, 10);
            ruleScore = Scale(
                o3Rate,
                0,
                //largestO3EarlyCutsLocal / 10.0,
                largestO3EarlyCuts / 7.5,  // Give a better penalty curve to O3 cuts, make it less "binary"
                0,
                10);
            score -= ruleScore;
            scoreAudit += $"O3 Early Cuts: {me.EarlyO3Cuts} over {Math.Min(me.SimYears, 10)} years = {o3Rate:F4} rate, penalty = {ruleScore:F1}\n";


            // ======================================================
            // Penalise terrible Net Outcome vs best in year (but only if materially different, and scaled by similarity)
            // ======================================================

            double bestNet = bestNetOutcomeThisYear.NetOutcome;
            double meNet = me.NetOutcome;

            double deMinimis = 30_000;
            double diff = Math.Abs(bestNet - meNet);

            // Ignore tiny differences
            if (diff > deMinimis)
            {
                double ratio;

                // --------------------------------------------------
                // Relative performance ratio (works with negatives)
                //
                // 1.0 = same as best
                // 0.5 = much worse
                // 0.0 = catastrophically worse
                // --------------------------------------------------

                if (meNet == bestNet)
                {
                    ratio = 1.0;
                }
                else
                {
                    // Scale using largest magnitude so negatives work sensibly
                    double scale = Math.Max(
                        Math.Abs(bestNet),
                        Math.Abs(meNet));

                    scale = Math.Max(scale, 1); // safety

                    ratio = 1.0 - (diff / scale);
                    ratio = Math.Clamp(ratio, 0, 1);
                }

                // --------------------------------------------------
                // Base penalty from relative performance
                // --------------------------------------------------

                if (ratio >= 0.90)
                {
                    ruleScore = 0;
                }
                else if (ratio >= 0.80)
                {
                    ruleScore = 10;
                }
                else if (ratio >= 0.60)
                {
                    ruleScore = 20;
                }
                else
                {
                    ruleScore = 30;
                }

                // --------------------------------------------------
                // Magnitude adjustment
                //
                // £30k diff ≈ very soft
                // £100k diff ≈ medium
                // £200k+ diff ≈ near full penalty
                // --------------------------------------------------

                double magnitudeWeight =
                    Math.Clamp(diff / 200_000.0, 0.25, 1.0);

                ruleScore *= magnitudeWeight;

                score -= ruleScore;

                scoreAudit +=
                    $"NetOutcome ratio {ratio:P1} vs best (£{bestNet:N0}), " +
                    $"difference £{diff:N0}, " +
                    $"magnitude {magnitudeWeight:F2}, " +
                    $"penalty = {ruleScore:F1}\n";
            }

            // ======================================================
            // Survival penalty (only differentiates failures)
            // ======================================================

            if (!me.Survived)
            {
                double depletionPct =
                    me.DepletionYearIndex.HasValue
                        ? (double)me.DepletionYearIndex.Value / me.SimYears
                        : 0;

                // Fail late (80%+ through plan)
                if (depletionPct >= 0.85)
                {
                    ruleScore = 10;
                    score -= ruleScore;
                    scoreAudit += $"Depletion at {depletionPct:P1} of plan, late failure penalty = {ruleScore:F1}\n";
                }
                // Mid failure
                else if (depletionPct >= 0.75)
                {
                    ruleScore = 20;
                    score -= ruleScore;
                    scoreAudit += $"Depletion at {depletionPct:P1} of plan, mid failure penalty = {ruleScore:F1}\n";
                }
                // Early catastrophic failure
                else
                {
                    ruleScore = 35;
                    score -= ruleScore;
                    scoreAudit += $"Depletion at {depletionPct:P1} of plan, early failure penalty = {ruleScore:F1}\n";
                }
            }

            double weight = (double)me.SimYears / (double)fullPlanYears;
            yearScore.ScoreValueUnWheighted = score;
            yearScore.ScoreValue = score * weight;
            yearScore.ScoreAudit = scoreAudit;
            return yearScore;
        }

    }
        
        // ======================================================================
        //  CSV LOADERS
        // ======================================================================

        static List<(int Year, double MMF_RR, double EQUITY_RR, double BONDS_RR, double SythntheticCGT_RR, double Sythetic404020_RR)> LoadReturns(string path) =>
        File.ReadAllLines(path)
            .Skip(1)
            .Select(l =>
            {
                var c = l.Split(',');
                int year = int.Parse(c[0]);
                double MMF_RR = double.Parse(c[1]);
                double EQUITY_RR = double.Parse(c[2]);
                double BONDS_RR = double.Parse(c[3]);
                double SythntheticCGT_RR = double.Parse(c[4]);
                double Sythetic404020_RR = double.Parse(c[5]);
                return (year, MMF_RR, EQUITY_RR, BONDS_RR, SythntheticCGT_RR, Sythetic404020_RR);
            })
            .ToList();
    static List<PlanRow> LoadPlan(string path) =>
        File.ReadAllLines(path)
            .Skip(1)
            .Select(line =>
            {
                var c = line.Split(',');
                return new PlanRow
                {
                    Year = int.Parse(c[0]),
                    O1 = double.Parse(c[1]),
                    O2 = double.Parse(c[2]),
                    O3 = double.Parse(c[3]),
                    S1Spend = double.Parse(c[4]),
                    S2Spend = double.Parse(c[5]),
                    IsAdjustable = (double.Parse(c[2]) + double.Parse(c[3])) > 0
                };
            }).ToList();

    private static List<T> SelectSpacedYears<T>(
        IEnumerable<T> orderedYears,
        int count,
        Func<T, int> yearSelector,
        int minYearGap)
    {
        var selected = new List<T>();

        foreach (var item in orderedYears)
        {
            var year = yearSelector(item);

            bool tooClose = selected.Any(x =>
                Math.Abs(yearSelector(x) - year) < minYearGap);

            if (!tooClose)
            {
                selected.Add(item);

                if (selected.Count == count)
                    break;
            }
        }

        return selected;
    }
}
