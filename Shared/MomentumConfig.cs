using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

public class MomentumConfig
{
    public int CashBufferYearsHighHealth { get; set; } = 3;
    public int CashBufferYearsLowHealth { get; set; } = 1;
    public double CashBufferHealthThreshold { get; set; } = 1.20;
}
