namespace GraphBridge.EntityFrameworkCore.Schema;

/// <summary>A point-in-time metadata observation, not a data-integrity or snapshot-isolation guarantee.</summary>
public sealed class GraphSchemaInspection
{
    internal GraphSchemaInspection(string databaseName, GraphSchemaManifest manifest,
        GraphSchemaCapabilities capabilities, GraphSchemaObservation observation, GraphSchemaComparison comparison)
        => (DatabaseName, Manifest, Capabilities, Observation, Comparison)
            = (databaseName, manifest, capabilities, observation, comparison);

    public string DatabaseName { get; }
    public GraphSchemaManifest Manifest { get; }
    public GraphSchemaCapabilities Capabilities { get; }
    public GraphSchemaObservation Observation { get; }
    public GraphSchemaComparison Comparison { get; }
}
