using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

public class GuardrailConfig
{
    public double Tier4Threshold { get; set; } = 0.70;
    public double Tier3Threshold { get; set; } = 0.80;
    public double Tier2Threshold { get; set; } = 0.90;
    public double Tier1Threshold { get; set; } = 1.00;
}
