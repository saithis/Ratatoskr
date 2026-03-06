using TUnit.Core.Interfaces;

[assembly: ParallelLimiter<ProcessorCountLimit>]

public class ProcessorCountLimit : IParallelLimit
{
    public int Limit => Environment.ProcessorCount;
}
