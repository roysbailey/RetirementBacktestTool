using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

public class TimeBucketConfig
{
    public int Bucket1Years { get; set; }
    public int Bucket2Years { get; set; }

    public double FundingStateExtremeBadThreshold { get; set; } = 0.80;
    public double FundingStateBadThreshold { get; set; } = 0.90;
    public double FundingStateGoodThreshold { get; set; } = 1.10;
}
