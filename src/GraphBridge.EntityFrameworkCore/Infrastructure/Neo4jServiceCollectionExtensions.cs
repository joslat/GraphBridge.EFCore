using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using GraphBridge.EntityFrameworkCore.Storage;
using GraphBridge.EntityFrameworkCore.Transport;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.Extensions.DependencyInjection;

public static class Neo4jServiceCollectionExtensions
{
    public static IServiceCollection AddEntityFrameworkNeo4j(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        new EntityFrameworkServicesBuilder(services)
            .TryAdd<LoggingDefinitions, Neo4jLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<Neo4jDbContextOptionsExtension>>()
            .TryAdd<IDatabase, Neo4jDatabase>()
            .TryAdd<IDatabaseCreator, Neo4jDatabaseCreator>()
            .TryAdd<IDbContextTransactionManager>(p => p.GetRequiredService<Neo4jTransactionManager>())
            .TryAdd<ITypeMappingSource, Neo4jTypeMappingSource>()
            .TryAdd<IModelValidator, Neo4jModelValidator>()
            .TryAdd<IQueryContextFactory, Neo4jQueryContextFactory>()
            .TryAdd<IQueryTranslationPreprocessorFactory, Neo4jQueryTranslationPreprocessorFactory>()
            .TryAdd<IQueryableMethodTranslatingExpressionVisitorFactory, Neo4jQueryableMethodTranslatingExpressionVisitorFactory>()
            .TryAdd<IShapedQueryCompilingExpressionVisitorFactory, Neo4jShapedQueryCompilingExpressionVisitorFactory>()
            .TryAddProviderSpecificServices(b => b
                .TryAddScoped<GraphBindingCompiler, GraphBindingCompiler>()
                .TryAddScoped<Neo4jTransactionManager, Neo4jTransactionManager>())
            .TryAddCoreServices();
        return services;
    }

    private sealed class Neo4jLoggingDefinitions : LoggingDefinitions;
}
