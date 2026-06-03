using SippBucketDrawdown.Shared;
using System;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;

namespace SippBucketDrawdown.Engines;

public class EngineBase
{
    // ============================================================
    //  GLOBAL CONFIG ACCESS (set once at startup)
    // ============================================================
    protected static Config CurrentConfig { get; private set; }

    public static void SetConfig(Config cfg)
    {
        CurrentConfig = cfg ?? throw new ArgumentNullException(nameof(cfg));
    }


    // ============================================================
    //  FINAL SUMMARY (unchanged)
    // ============================================================
    protected static void PrintSummary(
        double p1, double a1,
        double p2, double a2,
        double e1, double e2,
        double csvO1, double csvO2, double csvO3,
        double o1Planned, double o2Planned, double o3Planned,
        double o1External, double o2External, double o3External,
        double o1SippPlanned, double o2SippPlanned, double o3SippPlanned,
        double o1SippActual, double o2SippActual, double o3SippActual,
        double totalBonus,
        double sippWithdrawalsBaseTotal,
        EndState endState)
    {
        Console.WriteLine("\n=== FINAL JOINT SUMMARY (RECONCILED TO CSV) ===");
        Console.WriteLine($"S1 (Bob)    | Planned: £{p1,12:N0} | Actual: £{a1,12:N0} | End: £{e1,12:N0}");
        Console.WriteLine($"S2 (Brenda) | Planned: £{p2,12:N0} | Actual: £{a2,12:N0} | End: £{e2,12:N0}");

        double legacy = e1 + e2;
        double totalPlanned = p1 + p2;
        double totalActual = a1 + a2;

        Console.WriteLine($"\nCOMBINED    | Planned: £{totalPlanned,12:N0} | Actual+Legacy: £{(totalActual + legacy),12:N0} | Net Diff: £{(totalActual + legacy - totalPlanned),12:N0} ({((totalActual + legacy - totalPlanned) / totalPlanned):P2})");

        double o1Delivered = o1External + o1SippActual;
        double o2Delivered = o2External + o2SippActual;
        double o3Delivered = o3External + o3SippActual;

        Console.WriteLine("\n=== OBJECTIVE FULFILMENT (COMPACT TABLE) ===");
        Console.WriteLine("Objective |   Planned   |  External  | SIPP Plan  | SIPP Actual | Delivered  | %Funded");
        Console.WriteLine("-------------------------------------------------------------------------------------------");
        Console.WriteLine($"O1        | £{o1Planned,10:N0} | £{o1External,10:N0} | £{o1SippPlanned,10:N0} | £{o1SippActual,11:N0} | £{o1Delivered,10:N0} | {(o1Delivered / o1Planned):P1}");
        Console.WriteLine($"O2        | £{o2Planned,10:N0} | £{o2External,10:N0} | £{o2SippPlanned,10:N0} | £{o2SippActual,11:N0} | £{o2Delivered,10:N0} | {(o2Delivered / o2Planned):P1}");
        Console.WriteLine($"O3        | £{o3Planned,10:N0} | £{o3External,10:N0} | £{o3SippPlanned,10:N0} | £{o3SippActual,11:N0} | £{o3Delivered,10:N0} | {(o3Delivered / o3Planned):P1}");
        Console.WriteLine($"Bonus     | £{0,10:N0} | £{0,10:N0} | £{0,10:N0} | £{totalBonus,11:N0} | £{totalBonus,10:N0} |    N/A");

        double totPlanned = o1Planned + o2Planned + o3Planned;
        double totExternal = o1External + o2External + o3External;
        double totSippPlan = o1SippPlanned + o2SippPlanned + o3SippPlanned;
        double totSippActual = o1SippActual + o2SippActual + o3SippActual;
        double totDelivered = totExternal + totSippActual;

        Console.WriteLine($"TOTAL     | £{totPlanned,10:N0} | £{totExternal,10:N0} | £{totSippPlan,10:N0} | £{totSippActual,11:N0} | £{totDelivered,10:N0} | {(totDelivered / totPlanned):P1}");

        Console.WriteLine("\n=== BONUS / LEGACY ===");
        Console.WriteLine($"Total Bonus (SIPP only): £{totalBonus:N0}");
        Console.WriteLine($"Legacy (end SIPP pots):  £{legacy:N0}");

        double withdrawalsCheck = sippWithdrawalsBaseTotal + totalBonus;

        Console.WriteLine("\n=== RECONCILIATION CHECK (WITHDRAWALS ONLY) ===");
        Console.WriteLine($"Delivered (SIPP only, base) + Bonus = £{withdrawalsCheck:N0}");
        Console.WriteLine($"S1Actual + S2Actual                 = £{totalActual:N0}");
        Console.WriteLine($"Difference                          = £{(withdrawalsCheck - totalActual):N0}");
        Console.WriteLine($"Simulation Outcome                    :{endState.ToString()}");
    }

    // ======================================================================
    //  SIPP FUNDING ALLOCATION (unchanged)
    // ======================================================================
    protected static void AllocateSippFunding(
        double o1, double o2, double o3,
        double s1Spend, double s2Spend,
        out double o1SippFunded, out double o2SippFunded, out double o3SippFunded,
        out double o1S1, out double o2S1, out double o3S1,
        out double o1S2, out double o2S2, out double o3S2)
    {
        double totalObjectives = o1 + o2 + o3;
        double totalSipp = s1Spend + s2Spend;
        double nonSipp = Math.Max(0, totalObjectives - totalSipp);

        double nonO1 = Math.Min(o1, nonSipp); nonSipp -= nonO1;
        double nonO2 = Math.Min(o2, nonSipp); nonSipp -= nonO2;
        double nonO3 = Math.Min(o3, nonSipp); nonSipp -= nonO3;

        o1SippFunded = o1 - nonO1;
        o2SippFunded = o2 - nonO2;
        o3SippFunded = o3 - nonO3;

        if (totalSipp <= 0.01 || (o1SippFunded + o2SippFunded + o3SippFunded) < 0.01)
        {
            o1S1 = o2S1 = o3S1 = 0;
            o1S2 = o2S2 = o3S2 = 0;
            return;
        }

        double s1Ratio = s1Spend / totalSipp;
        double s2Ratio = s2Spend / totalSipp;

        o1S1 = o1SippFunded * s1Ratio;
        o2S1 = o2SippFunded * s1Ratio;
        o3S1 = o3SippFunded * s1Ratio;

        o1S2 = o1SippFunded * s2Ratio;
        o2S2 = o2SippFunded * s2Ratio;
        o3S2 = o3SippFunded * s2Ratio;
    }

    protected double ComputePVFromYearIndex(int sippIndex, List<PlanRow> plan, int yearIndex, double liabRate)
    {
        double s1RL = 0;
        for (int k = yearIndex; k < plan.Count; k++)
        {
            double yrs = k - yearIndex;
            double spend = (sippIndex == 1) ? plan[k].S1Spend : plan[k].S2Spend;
            s1RL += spend / Math.Pow(1 + liabRate, yrs);
        }
        return s1RL;
    }


    // ======================================================================
    //  GUARDRAILS (config‑driven, signature unchanged)
    // ======================================================================
    protected static void ApplyFundingRatioBasedGuardrailsAndDebt(
        double health,
        double cash,
        double o1Portion,
        double o2Portion,
        double o3Portion,
        double originalPlannedSipp,
        ref double sippTarget,
        ref double cutO1,
        ref double cutO2,
        ref double cutO3,
        List<string> notes,
        string label,
        double prevReturn,
        int yearIndex)
    {
        var g = CurrentConfig.Guardrails;

        double totalPortion = o1Portion + o2Portion + o3Portion;
        if (totalPortion < 0.01) return;

        if (yearIndex == 0)
            return;

        // Config‑driven guardrail tiers
        int tier =
            health < g.Tier4Threshold ? 4 :
            health < g.Tier3Threshold ? 3 :
            health < g.Tier2Threshold ? 2 :
            health < g.Tier1Threshold ? 1 : 0;

        // CashShield (Momentum rule)
        if (cash >= originalPlannedSipp &&
            prevReturn < 0 &&
            health < CurrentConfig.Momentum.CashBufferHealthThreshold)
        {
            if (tier > 0)
                notes.Add($"{label}:CashShield");

            tier = 0;
        }

        double keepO1 = o1Portion;
        double keepO2 = o2Portion;
        double keepO3 = o3Portion;

        switch (tier)
        {
            case 1:
                cutO3 = 0.5 * o3Portion;
                keepO3 = 0.5 * o3Portion;
                notes.Add($"{label}:Range1_O3_50%Cut");
                break;

            case 2:
                cutO3 = o3Portion;
                keepO3 = 0;
                notes.Add($"{label}:Range2_O3_100%Cut");
                break;

            case 3:
                cutO3 = o3Portion; keepO3 = 0;
                cutO2 = 0.75 * o2Portion; keepO2 = 0.25 * o2Portion;
                notes.Add($"{label}:Range3_O3_100%_O2_75%Cut");
                break;

            case 4:
                cutO3 = o3Portion; keepO3 = 0;
                cutO2 = o2Portion; keepO2 = 0;
                cutO1 = 0.10 * o1Portion; keepO1 = 0.90 * o1Portion;
                notes.Add($"{label}:CRITICAL_O1_10%_O2O3_100%Cut");
                break;
        }

        double oldPortion = o1Portion + o2Portion + o3Portion;
        double newPortion = keepO1 + keepO2 + keepO3;

        if (oldPortion > 0.01)
        {
            double ratio = newPortion / oldPortion;
            sippTarget *= ratio;
        }
    }

    protected double ComputePVWindow(
        List<PlanRow> plan,
        int start,
        int end,
        Config config,
        int sippIndex)
    {
        double pv = 0.0;

        for (int i = start; i < end; i++)
        {
            double spend = (sippIndex == 1) ? plan[i].S1Spend : plan[i].S2Spend;

            pv += spend / Math.Pow(1 + config.LiabilityDiscountRate, i - start);
        }

        return pv;
    }
}

