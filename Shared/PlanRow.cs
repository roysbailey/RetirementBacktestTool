using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

// ======================================================================
//  PLAN ROW STRUCTURE
// ======================================================================
public class PlanRow
{
    public int Year;
    public double O1, O2, O3, S1Spend, S2Spend;
    public bool IsAdjustable;
}
