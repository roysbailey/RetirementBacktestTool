using SippBucketDrawdown.Shared;

public class Config
{
    // Shared
    public string SpendPlanFile { get; set; }
    public string MarketReturnsFile { get; set; }
    public double LiabilityDiscountRate { get; set; }
    public int SimulationStartYear { get; set; }
 
    public BucketReturnsConfig BucketReturns { get; set; } = new();

    // Shared guardrail + bonus thresholds
    public GuardrailConfig Guardrails { get; set; } = new();
    public BonusConfig Bonus { get; set; } = new();

    // Engine‑specific
    public MomentumConfig Momentum { get; set; } = new();
    public TimeBucketConfig TimeBuckets { get; set; } = new();
    public HybridConfig Hybrid { get; set; } = new ();


    // Starting balances (shared)
    public double Sipp1StartingBalance { get; set; }
    public double Sipp2StartingBalance { get; set; }
}
