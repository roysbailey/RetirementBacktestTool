using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

// ======================================================================
//  STRATEGY INTERFACE
// ======================================================================
public interface IRefillStrategy
{
    bool ShouldRefill(double marketReturn);
    int GetRefillYears(double postWithdrawalHealth, Config config);
}