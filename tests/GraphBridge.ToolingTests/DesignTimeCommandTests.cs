using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Tooling.DesignTime;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.ToolingTests;

public sealed class DesignTimeCommandTests
{
    [Fact]
    public async Task Catalog_and_preview_are_deterministic_offline_and_hide_parameter_values_by_default()
    {
        var plan = Plan();
        using var output = new StringWriter(); using var error = new StringWriter();
        static DbContext Never() => throw new InvalidOperationException("Offline operation created a context.");
        Assert.Equal(0, await GraphMigrationCommands.RunAsync(["list"], [plan], Never, output, error));
        using (var json = JsonDocument.Parse(output.ToString()))
        {
            Assert.False(json.RootElement.GetProperty("databaseObserved").GetBoolean());
            Assert.Equal(plan.Checksum, json.RootElement.GetProperty("migrations")[0].GetProperty("Checksum").GetString());
        }
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await GraphMigrationCommands.RunAsync(["preview", plan.Id], [plan], Never, output, error));
        var first = output.ToString();
        Assert.DoesNotContain("private-value", first);
        Assert.Contains("SET", first);
        Assert.Contains("parameterNames", first);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await GraphMigrationCommands.RunAsync(["preview", plan.Id], [plan], Never, output, error));
        Assert.Equal(first, output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await GraphMigrationCommands.RunAsync(["preview", plan.Id, "--include-values"], [plan], Never, output, error));
        Assert.Contains("private-value", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("apply", "initial")]
    [InlineData("preview", "missing")]
    [InlineData("status", "missing")]
    [InlineData("preview", "INITIAL")]
    public async Task Invalid_commands_or_unknown_exact_ids_do_not_create_a_context(string command, string id)
    {
        var calls = 0;
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await GraphMigrationCommands.RunAsync([command, id], [Plan()], () => { calls++; return null!; }, output, error));
        Assert.Equal(0, calls); Assert.Equal("", output.ToString()); Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task Duplicate_catalog_ids_and_extra_options_are_rejected_before_transport()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        static DbContext Never() => throw new InvalidOperationException("No context expected.");
        var plan = Plan();
        Assert.Equal(2, await GraphMigrationCommands.RunAsync(["list"], [plan, plan], Never, output, error));
        Assert.Equal(2, await GraphMigrationCommands.RunAsync(["status", plan.Id, "--include-values"], [plan], Never, output, error));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task Pre_cancellation_prevents_context_creation_and_factory_errors_do_not_expose_messages()
    {
        var calls = 0;
        using var output = new StringWriter(); using var error = new StringWriter();
        DbContext Fail() { calls++; throw new InvalidOperationException("private-password"); }
        Assert.Equal(130, await GraphMigrationCommands.RunAsync(["status", "initial"], [Plan()], Fail, output, error, new CancellationToken(true)));
        Assert.Equal(0, calls);
        Assert.Equal(2, await GraphMigrationCommands.RunAsync(["status", "initial"], [Plan()], Fail, output, error));
        Assert.Equal(1, calls);
        Assert.DoesNotContain("private-password", error.ToString());
        Assert.Contains("InvalidOperationException", error.ToString());
    }

    private static GraphMigrationPlan Plan() => new("initial", null,
        [GraphMigrationStep.Backfill("fill", new GraphMigrationDataScope("NODE", "DesignRecord",
            new Dictionary<string, object?>(), 10), "Name", "private-value", 0)],
        GraphMigrationRollbackPolicy.NoRollback("Explicit reviewed recovery only."));
}
