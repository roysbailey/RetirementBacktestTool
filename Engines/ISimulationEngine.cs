using SippBucketDrawdown.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SippBucketDrawdown.Engines;

public interface ISimulationEngine
{
    SimulationResult Run(
        List<PlanRow> plan,
        List<(int Year, double etf6040_realReturn, double GlobalBonds_realReturn, double GlobalTracker_realReturn)> marketReturns,
        Config config,
        bool verbose,
        double csvS1Total,
        double csvS2Total,
        double csvO1Total,
        double csvO2Total,
        double csvO3Total,
        bool printSummary);
}
