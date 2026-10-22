namespace Symphonist.Host.Tests;

/// <summary>Tests that set the process-global SYMPHONIST_HOME run in this collection, never in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SymphonistHomeCollection
{
    public const string Name = "SYMPHONIST_HOME";
}
