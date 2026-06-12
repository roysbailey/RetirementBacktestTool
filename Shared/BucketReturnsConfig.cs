using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

public enum ReturnSource
{
    Equity,
    Bonds,
    MMF,
    ETF6040,
    CGT,
    BlendedSleeve
}

public class BucketReturnsConfig
{
    public ReturnSource Bucket2Fund { get; set; }
    public ReturnSource Bucket3Fund { get; set; }
    public ReturnSource SingleFund { get; set; }
}
