namespace GraphBridge.IntegrationTests;

/// <summary>Opt-in live tests are visibly skipped in the normal test run.</summary>
public sealed class Neo4jIntegrationFactAttribute : FactAttribute
{
    public Neo4jIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1")
        {
            Skip = "Live disposable Neo4j fixture required; set GRAPHBRIDGE_INTEGRATION=1.";
        }
    }
}
