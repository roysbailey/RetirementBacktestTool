using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Shared;

// ======================================================================
//  MOMENTUM REFILL STRATEGY (Strategy 1)
// ======================================================================
public class MomentumRefillStrategy : IRefillStrategy
{
    public bool ShouldRefill(double marketReturn)
        => marketReturn > 0;

    public int GetRefillYears(double postWithdrawalHealth, Config config)
        => postWithdrawalHealth > config.Momentum.CashBufferHealthThreshold
            ? config.Momentum.CashBufferYearsHighHealth
            : config.Momentum.CashBufferYearsLowHealth;
}
