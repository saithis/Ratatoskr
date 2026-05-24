using Ratatoskr.Tests;
using TUnit.Core.Interfaces;

[assembly: ParallelLimiter<ProcessorCountLimit>]

namespace Ratatoskr.Tests;

public class ProcessorCountLimit : IParallelLimit
{
    public int Limit => Environment.ProcessorCount;
}
