namespace SippBucketDrawdown.Shared;

public class HybridConfig
{
    public double AllocationB2 { get; set; } = 0.40;   // defensive %
    public double AllocationB3 { get; set; } = 0.60;   // growth %

    public double B2HealthGoodThreshold { get; set; } = 1.0;
    public double B3HealthGoodThreshold { get; set; } = 1.0;

    public double B2HealthDiscountAdjustment { get; set; } = -0.01;
    public double B3HealthDiscountAdjustment { get; set; } = -0.03;

    public double DriftTolerance { get; set; } = 0.05; // 5%
    public double EquitySellThreshold { get; set; } = 0.00;
    public double BondSellThreshold { get; set; } = 0.00;
}
