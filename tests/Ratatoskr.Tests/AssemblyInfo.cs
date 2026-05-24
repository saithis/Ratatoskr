using TUnit.Core.Interfaces;

[assembly: ParallelLimiter<Ratatoskr.Tests.ProcessorCountLimit>]

namespace Ratatoskr.Tests;

public class ProcessorCountLimit : IParallelLimit
{
    public int Limit => Environment.ProcessorCount;
}
