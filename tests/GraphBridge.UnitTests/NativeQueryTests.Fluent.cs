using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

public sealed partial class NativeQueryTests
{
    [Fact]
    public async Task Prepared_fluent_query_executes_original_command_after_captured_projection_alias_changes()
    {
        var script = new NativeScript();
        await using var db = FluentContext(script);
        var person = Graph.Node<FluentPerson>();
        var query = db.Cypher().Match(person).Where(person.Property(p => p.Id).Gt(0)).Return(() => person.Value.Id).Limit(2);
        var prepared = query.Prepare();
        var command = prepared.Command;
        Assert.Empty(script.Calls);
        person = Graph.Node<FluentPerson>();
        Assert.Throws<FluentCypherException>(() => query.ToCommand()); // The captured expression now refers to an unbound alias.
        var results = await prepared.ToListAsync();
        Assert.Equal(2, results.Count);
        Assert.Equal(command.Text, script.Command);
        Assert.Equal(0L, script.Parameters!["p0"]);
        Assert.Same(command, prepared.Command);
        Assert.Empty(db.ChangeTracker.Entries());
        script.AssertOwnership(1, 0);
    }

    [Fact]
    public async Task Prepared_query_requires_a_limit_and_honors_precancellation_without_transport()
    {
        var script = new NativeScript();
        await using var db = FluentContext(script);
        var person = Graph.Node<FluentPerson>();
        var query = db.Cypher().Match(person).Return(() => person.Value.Id);
        Assert.Throws<InvalidOperationException>(() => query.Prepare());
        var prepared = query.Limit(2).Prepare();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.ToListAsync(new(true)));
        Assert.Empty(script.Calls);
        Assert.Equal(2, prepared.MaxRows);
    }

    [Fact]
    public async Task Fluent_owned_execution_converts_buffers_and_commits_without_tracking()
    {
        var script = new NativeScript();
        await using var db = FluentContext(script); var person = Graph.Node<FluentPerson>();
        var query = db.Cypher().Match(person).Where(person.Property(p => p.Id).Gt(0)).Return(() => person.Value.Id).Limit(2);
        Assert.Equal(new[] { 1, 2 }, await query.ToListAsync());
        Assert.Equal(0L, script.Parameters!["p0"]); Assert.Equal(2L, script.Parameters["p1"]);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
        script.AssertOwnership(1, 0);
    }

    [Fact]
    public async Task Fluent_projector_failure_drains_borrowed_cursor_and_leaves_caller_transaction_owned()
    {
        var script = new NativeScript();
        await using var db = FluentContext(script); var person = Graph.Node<FluentPerson>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var query = db.Cypher().Match(person).Return(() => new FailingFluentDto(person.Value.Id)).Limit(2);
        await Assert.ThrowsAsync<FormatException>(() => query.ToListAsync());
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(1, script.Count("cursor.ConsumeAsync"));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Equal(0, script.Count("transaction.RollbackAsync"));
        await transaction.RollbackAsync();
        script.AssertOwnership(0, 1);
    }

    [Fact]
    public async Task Fluent_row_limit_protects_against_unexpected_extra_wire_rows()
    {
        var script = new NativeScript();
        await using var db = FluentContext(script); var person = Graph.Node<FluentPerson>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Cypher().Match(person).Return(() => person.Value.Id).Limit(1).ToListAsync());
        script.AssertOwnership(0, 1);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Fluent_does_not_retry_uncertain_commit_or_mask_it_as_success()
    {
        var failure = new IOException("Commit outcome unknown");
        var script = new NativeScript { CommitFailure = failure };
        await using var db = FluentContext(script); var person = Graph.Node<FluentPerson>();
        var error = await Assert.ThrowsAsync<IOException>(() => db.Cypher().Match(person).Return(() => person.Value.Id).Limit(2).ToListAsync());
        Assert.Same(failure, error);
        Assert.Equal(1, script.Count("transaction.RunAsync"));
        Assert.Equal(1, script.Count("transaction.CommitAsync"));
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static ProviderModelContext FluentContext(NativeScript script) => new(script.Driver, b =>
    {
        b.Entity<FluentPerson>().ToGraphNode("FluentPerson");
        b.Entity<FluentPerson>().Property(p => p.Id).ValueGeneratedNever();
    });
    private sealed class FluentPerson { public int Id { get; set; } }
    private sealed class FailingFluentDto
    {
        public FailingFluentDto(int id) => throw new FormatException("Intentional DTO failure " + id);
    }
}
