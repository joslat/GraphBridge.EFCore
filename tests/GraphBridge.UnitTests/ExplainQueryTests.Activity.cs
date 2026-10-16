using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

public sealed partial class ExplainQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explain_traces_omit_caller_text_and_plans_and_include_cleanup_failure(bool failCleanup)
    {
        using var parent = new Activity("explain-parent").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = CommandActivityTests.Listen(parent, spans);
        var error = new IOException("private-cleanup-error");
        var script = new Script { CleanupFailure = failCleanup ? error : null };
        await using var db = Context(script);
        var actual = await Record.ExceptionAsync(() => db.Database.ExplainCypherAsync("RETURN 'private-literal'", Empty));
        if (failCleanup) Assert.Same(error, actual); else Assert.Null(actual);
        var root = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.ExplainActivityName);
        var statement = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName);
        Assert.Equal(parent.SpanId, root.ParentSpanId);
        Assert.Equal(root.SpanId, statement.ParentSpanId);
        Assert.Equal(failCleanup ? ActivityStatusCode.Error : ActivityStatusCode.Unset, root.Status);
        Assert.Null(statement.GetTagItem("graphbridge.statement.fingerprint"));
        Assert.Equal(5, spans.Count);
        Assert.DoesNotContain(spans, s => s.OperationName == GraphBridgeDiagnostics.CommitActivityName);
        Assert.All(spans, s => { Assert.DoesNotContain("private", string.Join(";", s.TagObjects)); Assert.Null(s.StatusDescription); });
        Assert.Same(parent, Activity.Current);
    }
}
