using System.Reflection;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridgeStarter.Tests;

public sealed class QueryContractTests
{
    [Fact]
    public void Query_is_parameterized_without_driver_access()
    {
        var driver = DispatchProxy.Create<IDriver, OfflineDriver>();
        using var db = new SmokeContext(driver);
        var id = Guid.NewGuid();
        var text = db.Items.Where(item => item.Id == id && item.Score >= 10).ToQueryString();
        Assert.Contains("MATCH", text);
        Assert.Contains("$p0", text);
        Assert.DoesNotContain(id.ToString(), text);
        Assert.Equal("GraphBridge.EntityFrameworkCore", db.Database.ProviderName);
    }

    [Fact]
    public void Unsupported_query_fails_before_transport()
    {
        var driver = DispatchProxy.Create<IDriver, OfflineDriver>();
        using var db = new SmokeContext(driver);
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Items.Where(item => item.Name.GetHashCode() > 0).ToQueryString());
    }

    public class OfflineDriver : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => throw new InvalidOperationException("Offline contract test attempted driver access: " + method?.Name);
    }
}
