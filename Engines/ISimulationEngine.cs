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
        List<(int Year, double MMF_RR, double EQUITY_RR, double BONDS_RR, double SythntheticCGT_RR, double Sythetic404020_RR)> marketReturns,
        Config config,
        bool verbose,
        double csvS1Total,
        double csvS2Total,
        double csvO1Total,
        double csvO2Total,
        double csvO3Total,
        bool printSummary);
}
