using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

public class BonusConfig
{
    public double StartThreshold { get; set; } = 1.10;
    public double Tier2Threshold { get; set; } = 1.20;
    public double Tier3Threshold { get; set; } = 1.30;
    public double Tier4Threshold { get; set; } = 1.40;
    public double Tier5Threshold { get; set; } = 1.50;

    public double PctTier1 { get; set; } = 0.02;
    public double PctTier2 { get; set; } = 0.03;
    public double PctTier3 { get; set; } = 0.04;
    public double PctTier4 { get; set; } = 0.05;
    public double PctTier5 { get; set; } = 0.06;
}
