using GraphBridge.DesignTime.Sample;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Tooling.DesignTime;

var catalog = new[]
{
    new GraphMigrationPlan("initial", null,
        [GraphMigrationStep.CreateConstraint("person-key", new GraphMigrationSchemaDefinition(
            "gb_design_person_key", GraphMigrationSchemaKind.UniquenessConstraint, "NODE", "GraphBridgeDesignPerson", ["Id"]))],
        GraphMigrationRollbackPolicy.NoRollback("Review graph changes explicitly; this host never applies them."))
};
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await GraphMigrationCommands.RunAsync(args, catalog, DesignContextFactory.CreateStatusContext,
    Console.Out, Console.Error, cancellation.Token);
