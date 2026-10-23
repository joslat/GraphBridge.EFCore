# EF Core provider service map — GB-002

Null-converter boundary (2026-10-11): `GraphModelValidation` checks the resolved type mapping's `Converter.ConvertsNulls` and rejects `true` before transport. Pinned `Storage/ValueConversion/ValueConverter.cs` identifies that constructor as an internal, risky contract; GraphBridge's null-preserving scalar bindings and parameter resolver do not implement it. Two actual node/identified-edge model regressions failed before the guard and pass afterward within 566 unit cases. The experimental constructor is used only in those negative tests, not runtime code.

Readonly-navigation correction (2026-10-11): `GraphIncludeBinding.CompileFixup` calls the public provider-facing `Microsoft.EntityFrameworkCore.Infrastructure.ExpressionExtensions.Assign(MemberExpression, Expression)` from the pinned [`Infrastructure/ExpressionExtensions.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Infrastructure/ExpressionExtensions.cs). Unlike raw `Expression.Assign`, this EF helper supports `FieldInfo.IsInitOnly`; the pinned InMemory Include fixup uses the same helper. GraphBridge delegates that version-sensitive implementation to EF rather than copying its private-expression reflection. Six new both-direction/tracking query-compilation cases failed before the change; all 564 unit cases and six real Neo4j field-materialization/identity cases pass afterward.

Fluent entity returns (2026-10-22, [ADR 0049](adr/ADR-0049.md)): `CypherEntityReader` resolves the EF default `IStructuralTypeMaterializerSource` from the context's service provider and calls `CreateMaterializeExpression(new StructuralTypeMaterializerSourceParameters(entityType, "instance", entityType.ClrType, IsNullable: false, QueryTrackingBehavior.NoTracking), materializationContext)`, then compiles it once per entity type. This mirrors the pinned [`StructuralTypeMaterializerSource.GetMaterializer`](https://github.com/dotnet/efcore/blob/v10.0.13/src/EFCore/Query/Internal/StructuralTypeMaterializerSource.cs) (EF passes a null tracking behavior there; GraphBridge passes `NoTracking`, as EF's no-tracking shaper does, so injected services see a detached entity). Constructor binding, service properties and singleton materialization interceptors therefore apply as in a LINQ no-tracking query. The record [`StructuralTypeMaterializerSourceParameters`](https://github.com/dotnet/efcore/blob/v10.0.13/src/EFCore/Query/EntityMaterializerSourceParameters.cs) has five members at 10.0.13: `StructuralType`, `InstanceName`, `ClrType`, `IsNullable` and `QueryTrackingBehavior`. Values reach the `ValueBuffer` through the same `GraphReadBinding` slots and `GraphPropertyBinding` conversions as LINQ. No state-manager entry is created.

Source inspection date: 2026-10-11. Target: **Microsoft.EntityFrameworkCore 10.0.12**, .NET 10. The EF source tag is [`v10.0.12`](https://github.com/dotnet/efcore/tree/v10.0.12), resolved by `git ls-remote` and shallow clone to **`001bfac21c1910cdf1110ca256c9ea0a06f374ca`**. All EF links below use that tag. Do not substitute signatures from `main`, older tutorials, or another EF package version.

Patch update to **10.0.13** (2026-10-17): tag [`v10.0.13`](https://github.com/dotnet/efcore/tree/v10.0.13) resolves to **`4c96c90aa32d1d339911d82609f80200248c64f1`**. `git diff v10.0.12 v10.0.13` changes one file under `src/EFCore`: `ChangeTracking/Internal/InternalEntryBase.InternalComplexCollectionEntry.cs`, which reuses tracked complex-collection entries (EF issue 38632, switch `Microsoft.EntityFrameworkCore.Issue38632`). GraphBridge maps no complex types and calls none of it. `EFCore.Abstractions`, `EFCore.Design` and `test/EFCore.Specification.Tests` are unchanged; the other changes are build files, Sqlite and relational or Cosmos tests. Every signature and link in this map therefore holds for 10.0.13, and the links keep the `v10.0.12` tag they were inspected at.

Sections 1–9 record the source evidence established **before provider code**. Section 10 records the subsequently implemented GB-003–006 adapter and executed query tests. Persistence, relationship materialization, and wider EF compatibility are separate gates; see `IMPLEMENTATION_STATUS.md` and the relevant tests.

## 1. Registration and lifetime authority

[`EntityFrameworkServicesBuilder.cs`][services] is the registration authority: `CoreServices` records lifetimes; `TryAddCoreServices()` supplies defaults. Register provider implementations before calling `TryAddCoreServices()`. Both [Cosmos registration][cosmos-di] and [MongoDB registration][mongo-di] follow this order.

Stability notation:

- **P**: public provider-facing API. It is an extension seam, not a promise that arbitrary EF versions are interchangeable. Pin and compile against the selected patch.
- **U**: public visibility but explicitly internal infrastructure documentation and/or `[EntityFrameworkInternal]`; isolate usage and review on every version change.
- **E**: `[Experimental(EFDiagnostics.PrecompiledQueryExperimental)]`; deferred.
- **N/A**: value/object lifetime, not an EF DI service registration.

| EF service / object | EF lifetime | GraphBridge implementation / planned boundary | Source / stability | Acceptance evidence needed |
|---|---|---|---|---|
| `IDbContextOptionsExtension` + `DbContextOptionsExtensionInfo` | N/A; immutable options value | `Neo4jDbContextOptionsExtension`; validate supplied driver/database, define ownership and service-provider reuse | [Options interface][options], [info][options-info]; P | Missing/invalid options; options clones do not mutate earlier builders; no credentials in logs |
| `IDatabaseProvider` | Singleton, multiple registrations allowed | Reuse `DatabaseProvider<Neo4jDbContextOptionsExtension>` | [Provider][provider], [generic implementation][provider-generic]; P | Correct `Database.ProviderName`; provider selection and conflicting-provider failure |
| `LoggingDefinitions` | Singleton | Small provider subclass | [Services][services]; P | Context initialization and diagnostics resolve without missing dependencies |
| `IDatabase` | Scoped | `Neo4jDatabase : Database`; inherit query compilation | [Interface][database-interface], [base][database]; P | Activation resolves it; unsupported saves throw until GB-007; later saved-entry counts/atomicity |
| `IDatabaseCreator` | Scoped | `Neo4jDatabaseCreator`; unsupported schema/destructive operations throw | [Interface][creator]; P | `CanConnectAsync` is real when implemented; `EnsureDeleted` never silently deletes data |
| `IDbContextTransactionManager` | Scoped | Context-local `Neo4jTransactionManager`, initially explicit unsupported operations if needed | [Interface][transaction-manager]; P | Unsupported operations fail; later transaction/session ownership and reset/disposal tests |
| `ITypeMappingSource` | Singleton | `Neo4jTypeMappingSource : TypeMappingSource` | [Interface][mapping-interface], [base][mapping-source], [base hook][mapping-base]; P | Scalar mappings/converters; incompatible CLR types rejected |
| `IModelValidator` | Singleton | `Neo4jModelValidator : ModelValidator` | [Base][model-validator]; P | Preserve core validation; reject unsupported storage/model shapes |
| `IProviderConventionSetBuilder` | Scoped | Keep core `ProviderConventionSetBuilder` initially; replace only for a demonstrated need | [Services][services]; P | Explicit annotations survive finalized model; conventions do not imply unsupported relationships |
| `IModelRuntimeInitializer` | Singleton | Keep core default initially | [Services][services]; P | Finalized EF model is binding authority; no second mutable model |
| `IQueryContextFactory` | Scoped | `Neo4jQueryContextFactory`; creates subclass carrying scoped transport | [Factory][query-context-factory], [context][query-context]; P | Different contexts do not share session/transaction state |
| `IQueryCompilationContextFactory` | Scoped | Core default initially; custom factory only if provider compilation state is needed | [Factory][compilation-factory], [context][compilation-context]; P / E | EF pipeline and query cache remain in control; precompilation unsupported |
| `IQueryTranslationPreprocessorFactory` | Scoped | Core default for initial scalar node query | [Factory][pre-factory]; P | EF normalization/parameter extraction preserved |
| `IQueryableMethodTranslatingExpressionVisitorFactory` | Scoped | `Neo4jQueryableMethodTranslatingExpressionVisitorFactory` | [Factory][method-factory], [visitor][method-visitor]; P | Server `Where`; all unsupported methods fail before transport |
| `IQueryTranslationPostprocessorFactory` | Scoped | Core default unless projection finalization requires replacement | [Factory][post-factory]; P | Projection bindings and actual row layout agree |
| `IShapedQueryCompilingExpressionVisitorFactory` | Scoped | `Neo4jShapedQueryCompilingExpressionVisitorFactory` | [Factory][shaper-factory], [visitor][shaper]; P | EF tracking/no-tracking/identity/cardinality on live records |
| `IStructuralTypeMaterializerSource` | Singleton | Keep EF default; accessed by EF shaper injection and, for untracked Fluent entity returns, directly (ADR 0049) | [Interface][materializer], [services][services]; P | EF constructs instances and snapshots; no driver object mapper substituting for tracking |
| `IStateManager` | Scoped | Keep EF default; no provider replacement | [Services][services], [implementation][state-manager]; U | One tracked reference per key; original values and change detection |
| `IExecutionStrategyFactory` | Scoped | Core default initially; no added retry strategy before outcome tests | [Services][services]; P | No stacked EF/driver retries or repeated side effects |
| `IUpdateEntry` | N/A; EF-owned entry passed to database service | Read entry metadata/state/values; never create a parallel tracker | [Interface][update-entry]; P | Captured actual node/join entry shapes, counts, failure state |
| `CoreTypeMapping`, `ValueBuffer`, `StructuralTypeShaperExpression` | N/A; mapping / row / compilation values | Provider mapping subclass; converted scalar row; EF shaper expression | [Mapping][core-mapping], [buffer][value-buffer], [structural shaper][structural-shaper]; P with particular U helpers | Conversion fidelity and correct property ordinals |

**Services without a core default:** provider identity, logging definitions, database, database creator, transaction manager, query context factory, type mapping source, method translator factory, and shaped compiler factory. The default query compilation context depends on those factories even if GB-003 only intends to demonstrate activation. Diagnostic throwing implementations may be used at activation stage, but they are not working query/storage features.

**Provider-owned lifetime proposal:** an externally supplied Neo4j `IDriver` is borrowed and never disposed by GraphBridge; a provider-owned driver would need an explicit singleton owner. Sessions/transactions remain scoped or enumeration-owned. A singleton mapping/validator service must never capture `DbContext`, session, or mutable context options. Driver reuse and EF service-provider reuse are separate concerns: option hashing/equality must reflect any settings captured by singleton services, and must never expose credentials.

## 2. Exact activation, database, and update signatures

The following declarations reproduce the relevant pinned-source signatures, omitting bodies and XML documentation. They are **not** a complete GraphBridge implementation.

```csharp
// Storage/IDatabase.cs
int SaveChanges(IList<IUpdateEntry> entries);
Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default);
Func<QueryContext, TResult> CompileQuery<TResult>(Expression query, bool async);
// Experimental:
Expression<Func<QueryContext, TResult>> CompileQueryExpression<TResult>(Expression query, bool async);

// Storage/Database.cs: subclass constructor and required overrides
protected Database(DatabaseDependencies dependencies);
public abstract int SaveChanges(IList<IUpdateEntry> entries);
public abstract Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default);
public virtual Func<QueryContext, TResult> CompileQuery<TResult>(Expression query, bool async);

// Storage/IDatabaseProvider.cs
string Name { get; }
string? Version { get; } // default interface implementation returns null
bool IsConfigured(IDbContextOptions options);

// Infrastructure/IDbContextOptionsExtension.cs
DbContextOptionsExtensionInfo Info { get; }
void ApplyServices(IServiceCollection services);
IDbContextOptionsExtension ApplyDefaults(IDbContextOptions options); // default returns this
void Validate(IDbContextOptions options);

// Infrastructure/DbContextOptionsExtensionInfo.cs
protected DbContextOptionsExtensionInfo(IDbContextOptionsExtension extension);
public abstract bool IsDatabaseProvider { get; }
public abstract string LogFragment { get; }
public abstract int GetServiceProviderHashCode();
public abstract bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other);
public abstract void PopulateDebugInfo(IDictionary<string, string> debugInfo);
```

`Database.CompileQuery<TResult>` invokes `Dependencies.QueryCompilationContextFactory.Create(async).CreateQueryExecutor<TResult>(query)`. Keep this implementation. The generic provider class constructor is `public DatabaseProvider(DatabaseProviderDependencies dependencies)`; its `Name` is the options extension's assembly name, and `IsConfigured` checks for that options extension. [Database base][database], [generic provider][provider-generic].

Relevant `IUpdateEntry` members (it is an EF-owned interface, not a service to implement):

```csharp
DbContext Context { get; }
IEntityType EntityType { get; }
EntityState EntityState { get; set; }
IUpdateEntry? SharedIdentityEntry { get; }
bool IsModified(IProperty property);
bool IsModified(IComplexProperty property);
bool HasTemporaryValue(IProperty property);
bool HasExplicitValue(IProperty property);
bool HasStoreGeneratedValue(IProperty property);
bool IsStoreGenerated(IProperty property);
object? GetCurrentValue(IPropertyBase propertyBase);
object? GetOriginalValue(IPropertyBase propertyBase);
bool CanHaveOriginalValue(IPropertyBase propertyBase);
TProperty GetCurrentValue<TProperty>(IPropertyBase propertyBase);
TProperty GetOriginalValue<TProperty>(IProperty property);
void SetStoreGeneratedValue(IProperty property, object? value, bool setModified = true);
void SetOriginalValue(IProperty property, object? value);
void SetPropertyModified(IProperty property);
EntityEntry ToEntityEntry();
object? GetRelationshipSnapshotValue(IPropertyBase propertyBase);
object? GetPreStoreGeneratedCurrentValue(IPropertyBase propertyBase);
bool IsConceptualNull(IProperty property);
```

The `SaveChanges` return value counts **state entries persisted**, not Neo4j summary counters. EF's [StateManager][state-manager] invokes `IDatabase.SaveChanges[Async]` and accepts changes after success when requested by the caller. GraphBridge must not accept EF changes before the transaction succeeds. Provider writes use current values, original concurrency values, and modified-property flags; join classification must use explicit mapping and public EF metadata, with GB-009 observing real entries first. [Update interface][update-entry], [database contract][database-interface].

## 3. Exact query pipeline and translator seams

```csharp
// Query factory interfaces (all Scoped)
QueryContext IQueryContextFactory.Create();
QueryCompilationContext IQueryCompilationContextFactory.Create(bool async);
// Experimental, default throws precompiled-query-not-supported:
QueryCompilationContext IQueryCompilationContextFactory.CreatePrecompiled(bool async);
QueryTranslationPreprocessor IQueryTranslationPreprocessorFactory.Create(QueryCompilationContext queryCompilationContext);
QueryableMethodTranslatingExpressionVisitor IQueryableMethodTranslatingExpressionVisitorFactory.Create(QueryCompilationContext queryCompilationContext);
QueryTranslationPostprocessor IQueryTranslationPostprocessorFactory.Create(QueryCompilationContext queryCompilationContext);
ShapedQueryCompilingExpressionVisitor IShapedQueryCompilingExpressionVisitorFactory.Create(QueryCompilationContext queryCompilationContext);

// Query/QueryCompilationContext.cs
public QueryCompilationContext(QueryCompilationContextDependencies dependencies, bool async);
public virtual Func<QueryContext, TResult> CreateQueryExecutor<TResult>(Expression query);
public virtual QueryParameterExpression RegisterRuntimeParameter(string name, LambdaExpression valueExtractor);

// Query/QueryableMethodTranslatingExpressionVisitor.cs
protected QueryableMethodTranslatingExpressionVisitor(
    QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
    QueryCompilationContext queryCompilationContext,
    bool subquery);
public virtual Expression Translate(Expression expression);
protected abstract QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor();
protected abstract ShapedQueryExpression? CreateShapedQueryExpression(IEntityType entityType);
protected abstract ShapedQueryExpression? TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate);
protected abstract ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector);
protected abstract ShapedQueryExpression? TranslateOrderBy(ShapedQueryExpression source, LambdaExpression keySelector, bool ascending);
protected abstract ShapedQueryExpression? TranslateThenBy(ShapedQueryExpression source, LambdaExpression keySelector, bool ascending);
protected abstract ShapedQueryExpression? TranslateSkip(ShapedQueryExpression source, Expression count);
protected abstract ShapedQueryExpression? TranslateTake(ShapedQueryExpression source, Expression count);
protected abstract ShapedQueryExpression? TranslateFirstOrDefault(
    ShapedQueryExpression source, LambdaExpression? predicate, Type returnType, bool returnDefault);
protected abstract ShapedQueryExpression? TranslateSingleOrDefault(
    ShapedQueryExpression source, LambdaExpression? predicate, Type returnType, bool returnDefault);
protected abstract ShapedQueryExpression? TranslateCount(ShapedQueryExpression source, LambdaExpression? predicate);
protected abstract ShapedQueryExpression? TranslateAny(ShapedQueryExpression source, LambdaExpression? predicate);
```

The qualified factory lines identify their declaring interfaces; they are signature notation, not valid top-level C# source. The translator has **additional abstract methods**, including EF 10 `TranslateRightJoin`; a concrete subclass must override all of them. Use the pinned [visitor source][method-visitor] as the complete compiler-checked checklist. Unsupported nullable-returning translations may return `null` through the base dispatcher's failure path; non-null `TranslateSelect` needs an explicit failure for unsupported projection. Do not implement unsupported operators using `Enumerable` evaluation.

The [compilation context][compilation-context] runs preprocessing → method translation → postprocessing → shaped compilation → runtime-parameter insertion. EF owns query compilation/caching; GraphBridge does not replace `IAsyncQueryProvider` or expose its own `IQueryable` ORM. `QueryParameterExpression.Name` binds to **`QueryContext.Parameters`**, whose pinned declaration is `public virtual Dictionary<string, object?> Parameters { get; } = new();`. There is no `QueryContext.ParameterValues` member in this source. Value extraction must occur on each execution, not be captured as the first execution's value. [Query parameter][query-parameter], [query context][query-context].

The base visitor permits some expressions to remain for client projection; its comments are not authorization for client predicates. GraphBridge's predicate translator must accept only its defined scalar forms and reject everything else before creating or executing a command. Graph identifiers come only from validated model binding. Literal and runtime query values both become Cypher parameters.

## 4. EF shaping, materialization, and tracking

```csharp
// Query/ShapedQueryCompilingExpressionVisitor.cs
protected ShapedQueryCompilingExpressionVisitor(
    ShapedQueryCompilingExpressionVisitorDependencies dependencies,
    QueryCompilationContext queryCompilationContext);
protected abstract Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression);
protected virtual Expression InjectStructuralTypeMaterializers(Expression expression);

// Query/ShapedQueryExpression.cs
public ShapedQueryExpression(Expression queryExpression, Expression shaperExpression);

// Query/StructuralTypeShaperExpression.cs
public StructuralTypeShaperExpression(ITypeBase type, Expression valueBufferExpression, bool nullable);

// Storage/ValueBuffer.cs and MaterializationContext.cs
public ValueBuffer(object?[] values);
public MaterializationContext(in ValueBuffer valueBuffer, DbContext context);

// Metadata/IPropertyBase.cs
int GetIndex(); // default interface body resolves EF's property index

// Query/QueryContext.cs
protected QueryContext(QueryContextDependencies dependencies);
public virtual void InitializeStateManager(bool standAlone = false);

// Query/IStructuralTypeMaterializerSource.cs
Expression CreateMaterializeExpression(StructuralTypeMaterializerSourceParameters parameters, Expression materializationExpression);
Func<MaterializationContext, object> GetMaterializer(IEntityType entityType);
Func<MaterializationContext, object> GetMaterializer(IComplexType complexType);
```

**Patch-specific trap:** `InjectEntityMaterializers(Expression)` is present but `[Obsolete("Use InjectStructuralTypeMaterializers instead.", error: true)]`. The retained dependency property name is `ShapedQueryCompilingExpressionVisitorDependencies.EntityMaterializerSource`, but its type is **`IStructuralTypeMaterializerSource`**. Inject dependency records from EF DI; their constructors are explicitly internal infrastructure and should not be reconstructed manually. [Shaped compiler][shaper], [dependencies][shaper-dependencies].

The base injection builds `MaterializationContext`, reads keys from the supplied value-buffer expression, finds tracked entries, materializes when needed, and starts tracking using EF. It enables identity resolution for `TrackAll` and `NoTrackingWithIdentityResolution`. These are the exact public-but-internal methods called by **EF-generated shaping expressions**, not methods for a separate provider tracker:

```csharp
// Query/QueryContext.cs — both [EntityFrameworkInternal]
public virtual InternalEntityEntry? TryGetEntry(
    IKey key, object[] keyValues, bool throwOnNullKey, out bool hasNullKey);
public virtual InternalEntityEntry StartTracking(
    IEntityType entityType, object entity, in ISnapshot snapshot);
```

`InternalEntityEntry` and `ISnapshot` are in `Microsoft.EntityFrameworkCore.ChangeTracking.Internal`. They are **U** risks even though methods exposing them are public. Let `InjectStructuralTypeMaterializers` construct these calls. Do not call `DbContext.Attach` for each result, instantiate `InternalEntityEntry`, copy `StateManager`, or call a driver POCO mapper and label it EF materialization. [Query context][query-context], [shaper injection][shaper].

### Minimal proposed record adapter (requires GB-005/006 proof)

1. Translate a single validated node entity root into a provider query expression plus `StructuralTypeShaperExpression`. Keep the query expression and entity projection explicit.
2. Project the key and supported scalar properties with deterministic Cypher aliases. Convert each driver record to model CLR values in an `object?[]` indexed by `IPropertyBase.GetIndex()`, then a `ValueBuffer`. Do not assume alphabetical order or driver column order is EF property order. Inheritance/complex/owned shapes stay rejected initially.
3. In `VisitShapedQuery`, bind the structural shaper's buffer expression to the row-buffer parameter and invoke `InjectStructuralTypeMaterializers`. Compile a delegate conceptually shaped as `Func<QueryContext, ValueBuffer, T>`; this delegate type is a GraphBridge proposal, not an EF factory interface.
4. Return an expression constructing the provider's async enumerable. Each enumeration owns/borrows the scoped transport correctly, reads runtime parameters, initializes EF's state manager before shaping, and invokes the compiled shaper for each row. For identity-resolution-only queries, pass `standAlone: true` to `InitializeStateManager`; that creates an EF-owned temporary state manager, not a GraphBridge tracker.
5. Retain EF's outer result-cardinality handling. When `Single` is implemented, retrieve enough rows to detect duplicates; never hide multiplicity by limiting it to one.

At the source-map checkpoint, this direct `ValueBuffer` route was an inference from the base EF shaper and its property reads. **The subsequent implementation has now compiled and passed live node-query tests**, as recorded in section 10. The initial shape supplies a real row-buffer expression directly, so it does not need a separate projection-binding removal visitor. Cosmos uses JSON injection, `InjectStructuralTypeMaterializers`, then projection-binding removal and a querying enumerable. Its querying enumerable initializes the EF state manager before applying the shaper. That sequence remains a reference, without introducing a runtime dependency on the Cosmos package. [Cosmos shaper][cosmos-shaper], [Cosmos enumerable][cosmos-enumerable], [EF buffer][value-buffer], [property index][property-base].

### Shaping gates defined at the research checkpoint

- Compile the smallest row-to-EF shaper against the **NuGet binary**, not merely these sources, and execute it through `DbSet` on the live fixture.
- Test `TrackAll`, `AsNoTracking`, and `AsNoTrackingWithIdentityResolution`; repeated keys must have their intended reference identity. Test original values and a second context.
- Confirm converter application occurs exactly once: driver integer (`long`) to model `int` needs checked range policy; GUID strings and enum conversions must match EF mappings in reads, writes, and parameters.
- Missing/null properties, null keys, conversion failures, shadow values, empty/single/multiple cardinality, and unsupported projections require error-path tests.
- Cancellation, early disposal, exception disposal, overlapping enumeration, context disposal, and existing explicit transactions require real transport tests. Async enumeration must not close its session before the cursor finishes.
- Includes, inheritance, owned/complex values, edge materialization, precompiled queries/NativeAOT, and navigation fix-up are outside the initial proof. No source-only success claim for them.

## 5. Type mapping and model validation

```csharp
// Storage/TypeMappingSource.cs / TypeMappingSourceBase.cs
protected TypeMappingSource(TypeMappingSourceDependencies dependencies);
protected virtual CoreTypeMapping? FindMapping(in TypeMappingInfo mappingInfo);

// Storage/CoreTypeMapping.cs
protected CoreTypeMapping(CoreTypeMappingParameters parameters);
protected abstract CoreTypeMapping Clone(CoreTypeMappingParameters parameters);
public abstract CoreTypeMapping WithComposedConverter(
    ValueConverter? converter,
    ValueComparer? comparer = null,
    ValueComparer? keyComparer = null,
    CoreTypeMapping? elementMapping = null,
    JsonValueReaderWriter? jsonValueReaderWriter = null);

// Infrastructure/ModelValidator.cs
public ModelValidator(ModelValidatorDependencies dependencies);
public virtual void Validate(IModel model, IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger);
```

Override the protected mapping hook and preserve `TypeMappingSource`'s conversion composition rather than implementing only `FindMapping(Type)`. The full [ITypeMappingSource][mapping-interface] includes `FindMapping(IProperty)`, `FindMapping(IElementType)`, `FindMapping(MemberInfo, IModel, bool)`, `FindMapping(Type)`, and `FindMapping(Type, IModel, CoreTypeMapping? elementMapping = null)`; the `MemberInfo` overload without a model is obsolete. `CoreTypeMapping` is non-relational: do not introduce `RelationalTypeMapping`, SQL type names, or a relational connection.

GB-004 should establish supported storage types and fail invalid models before transport. Mapping availability alone does not prove a scalar is supported: core conversion fallback can create mappings such as collection-to-JSON-string conversion. Explicitly reject accidental types/shapes outside the supported subset. Call core model validation as well as graph checks. Binding is an immutable projection of the finalized EF `IModel`; if model-affecting options vary, investigate the model cache key before sharing bindings.

## 6. Transactions and database facade

```csharp
// Storage/IDbContextTransactionManager.cs : IResettableService
IDbContextTransaction BeginTransaction();
Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
void CommitTransaction();
Task CommitTransactionAsync(CancellationToken cancellationToken = default);
void RollbackTransaction();
Task RollbackTransactionAsync(CancellationToken cancellationToken = default);
IDbContextTransaction? CurrentTransaction { get; }

// Infrastructure/IResettableService.cs
void ResetState();
Task ResetStateAsync(CancellationToken cancellationToken = default);

// Storage/IDbContextTransaction.cs : IDisposable, IAsyncDisposable
Guid TransactionId { get; }
void Commit();
Task CommitAsync(CancellationToken cancellationToken = default);
void Rollback();
Task RollbackAsync(CancellationToken cancellationToken = default);
bool SupportsSavepoints { get; } // default false

// Storage/IDatabaseCreator.cs
bool EnsureDeleted();
Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default);
bool EnsureCreated();
Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default);
bool CanConnect();
Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
```

These are non-relational contracts; there is no `DbConnection`/`DbTransaction` requirement. `IDbContextTransaction` additionally has default savepoint creation/rollback methods that throw and default release methods that do nothing; GraphBridge should keep `SupportsSavepoints == false` until genuinely implemented. `TryAddCoreServices()` registers `IDbContextTransactionManager` as an `IResettableService`, so reset cannot leave a transaction/session attached to a pooled context. Synchronous support must be explicitly implemented or rejected; do not conceal sync-over-async behind a compatibility claim. [Transaction manager][transaction-manager], [transaction][transaction], [reset][reset], [creator][creator].

One future transaction coordinator must serve EF queries, `SaveChangesAsync`, and the explicitly requested native Cypher lane. GB-007/008 must prove atomic writes, failed commit/rollback behavior, and proper EF exceptions before success counts are returned. Explicit transaction behavior, ambient transactions, savepoints, and retries remain unproven; unsupported operations should throw. `EnsureDeleted` must never become a broad graph deletion convenience.

## 7. Reference-provider findings

**Cosmos:** inspected at the exact EF tag. Its registration confirms scoped database/query services and singleton mapping/validation through `EntityFrameworkServicesBuilder`, plus explicit provider-specific client lifetimes. Its query implementation separates provider query expressions, materializer injection, projection removal, and enumeration. Cosmos storage/query classes under `*.Internal` have explicit internal-API warnings: they are reference material, not GraphBridge base classes. [DI][cosmos-di], [shaper][cosmos-shaper], [enumerable][cosmos-enumerable].

**MongoDB:** inspected tag **`v10.0.4`**, commit **`dec7e26fcc87dec0d31fd704a7ffef1ccd90d7f0`**. Read both `src/MongoDB.EntityFrameworkCore/Extensions/MongoServiceCollectionExtensions.cs` and `src/MongoDB.EntityFrameworkCore/Query/AGENTS.md`. The same commit serves multiple EF-major tag lines, and the repository contains conditional compilation; it is not proof of exact EF 10.0.12 binary compatibility. No MongoDB code was built or copied. [Registration][mongo-di], [query notes][mongo-query-notes].

Mongo's notes highlight exact projection-member/shaper agreement, strict supported method dispatch, query-cache shape stability, and keeping cursor/transaction execution in storage. Its provider translates EF to the MongoDB driver's LINQ provider; GraphBridge cannot reuse that approach as a substitute for its own Cypher translator because this research establishes no equivalent Neo4j LINQ translator. Mongo's rendered-query test patterns supplement, but do not replace, live Neo4j functional tests.

## 8. Version-risk register and next work

| Dependency / seam | Planned usage | Upgrade review |
|---|---|---|
| EF `Database`, options, DI factories and query visitor abstract methods (P) | Direct subclass/interface adapter | Recompile every override and recheck `CoreServices` lifetimes |
| `InjectStructuralTypeMaterializers` (P) and its generated U calls | Reuse base injection | Tracking, snapshots, duplicate keys, null keys, converter regression tests |
| `QueryContext.TryGetEntry`, `StartTracking`, `InternalEntityEntry`, `ISnapshot` (U) | Indirect through EF shaper; no direct provider tracker | Verify exact signatures; keep all knowledge inside query adapter |
| Dependency-record constructors marked internal (U) | EF DI creates records | Do not construct records manually; recheck constructor dependencies on upgrades |
| `QueryContext.Parameters`, `QueryParameterExpression`, `IPropertyBase.GetIndex()` (P) | Runtime values and EF row layout | Cache re-execution with changed parameters; reordered/shadow property tests |
| `TypeMappingSource.FindCollectionMapping` (U) | No override planned; guard unsupported models | Check conversion fallback has not silently broadened support |
| `Metadata.Internal` join detection, private reflection | No planned use | Use explicit annotations and GB-009 actual-entry evidence |
| Experimental precompiled query entry points (E) | Deferred | Do not advertise NativeAOT/precompiled-query support |
| Cosmos/Mongo internal implementations | Source reference only | No runtime dependency or wholesale implementation copying |
| `IDatabaseFacadeDependenciesAccessor.Context`, `DbContext.ContextId.Lease` (P) | VectorData adapter: the borrowed context and its pool lease, captured when the adapter is created and compared before each search | A re-leased pooled context must still change `Lease` (unit and live pooled-context tests in `VectorDataAdapterTests` and `ProviderVectorDataTests`) |
| `QueryCompilationContext.Tags`, `IDatabaseFacadeDependenciesAccessor.Context` (P) | Query tags written as leading `//` comments by shaped compilation and bulk execution; `SetGraphCommandTimeout`/`GetGraphCommandTimeout` reach the context's transaction manager (ADR 0050) | Tags are still collected for non-relational providers and stay part of the query cache key (`OperationalControlsTests`, `CompiledCommandTests`) |
| `DbSet<T>.EntityType`, `ICurrentDbContext`, `IClrPropertyGetter.GetClrValueUsingContainingEntity`, `IReadOnlyProperty.Sentinel` (P) | `ExecuteUpsertAsync` reads key and property values from the entities passed in, without attaching them, and refuses keys left at their sentinel (ADR 0051) | Values must equal what SaveChanges stores for the same entity (`UpsertTests`, `ProviderUpsertTests`) |

The required sequence was GB-001 live direct-driver baseline → GB-002 source map → GB-003 options → GB-004 binding → GB-005/006 server translation and EF shaping. The node-query gate has now passed (section 10). Next dependency-ordered work is **GB-007/008**, node persistence and transaction/concurrency proofs; source inspection alone cannot close those tickets.

## 9. Commands and evidence

Commands executed for this map (PowerShell, repository root):

```text
git ls-remote --tags https://github.com/dotnet/efcore.git v10.0.12
# Exit 0: 001bfac21c1910cdf1110ca256c9ea0a06f374ca refs/tags/v10.0.12
git clone --depth 1 --branch v10.0.12 https://github.com/dotnet/efcore.git .research/efcore
# Exit 0; detached at the commit above.
git ls-remote --tags https://github.com/mongodb/mongo-efcore-provider.git
# Exit 0; v10.0.4 resolved to dec7e26fcc87dec0d31fd704a7ffef1ccd90d7f0.
git clone --depth 1 --branch v10.0.4 https://github.com/mongodb/mongo-efcore-provider.git .research/efcore/mongo-reference
# Exit 0; detached at the commit above.
```

`rg -n` and `Get-Content` inspected the linked interfaces, implementations, service table, and reference-provider query notes. Both `git rev-parse HEAD` checks reproduced the commits above (exit 0). A path check verified all 37 EF source links against the local tagged checkout, and `git check-ignore` confirmed the source cache is ignored (exit 0). Installed EF 10.0.12 NuGet XML also confirms `QueryContext.Parameters` and `InjectStructuralTypeMaterializers`; this is an additional documentation check, not a binary execution test. The attempted web raw-file fetch returned a cache miss; the successful Git source clones supplied the actual evidence. The `.research` clones are disposable, ignored research input, not shipped provider code. GB-002 itself performed no runtime validation; subsequent implementation evidence follows.

## 10. Implemented query adapter and evidence — GB-005/006

This checkpoint was validated on .NET SDK 10.0.401, EF Core 10.0.12, Neo4j.Driver 6.3.0, and disposable Neo4j 2026.09.0 Community. It establishes a working **bounded node-query subset**, not full EF provider compatibility.

| Seam | Actual implementation and usage |
|---|---|
| Provider DI | `Infrastructure/Neo4jServiceCollectionExtensions.cs` registers the source-mapped services and uses `TryAddCoreServices()`; compilation/preprocessor/postprocessor/materializer/state-manager services retain EF defaults. |
| Query context | `Query/Neo4jQueryContext.cs` injects options and the scoped transaction manager. The cached executable reads driver/database/transaction state from the execution's context, not a captured driver constant. |
| Translation | `Query/Neo4jQueryableMethodTranslatingExpressionVisitor.cs` builds `Neo4jQueryExpression` plus EF `StructuralTypeShaperExpression`. Its companion `.Unsupported.cs` contains 26 rejection overrides using exact pinned-source abstract signatures. |
| Cypher | `Cypher/CypherQueryPlan.cs` and `CypherQueryRenderer.cs` are independent of EF and the driver. They validate syntax tokens, parameterize query values, compensate nullable comparison semantics, and explicitly sort null first ascending / last descending. |
| Parameter binding | `Query/Neo4jQueryExpression.cs` resolves runtime names through `QueryContext.Parameters`, applies model converters, preserves null comparisons to required properties, handles enum compiler conversions, and clamps nonpositive paging counts to zero. |
| Materialization | `Query/Neo4jShapedQueryCompilingExpressionVisitor.cs` calls `InjectStructuralTypeMaterializers` and compiles `Func<QueryContext, ValueBuffer, T>`. Property slots use `IPropertyBase.GetIndex()`. There are **no direct GraphBridge calls** to `TryGetEntry`, `StartTracking`, `InternalEntityEntry`, or a provider-owned state manager. EF-generated expressions own those U seams. |
| Enumeration | `Query/Neo4jQueryingEnumerable.cs` converts record values through the binding, initializes the EF state manager, applies the EF shaper, checks concurrency/cancellation, and disposes the cursor/session. It borrows an existing driver transaction if present; explicit transaction behavior still requires the M2 tests. |
| Diagnostics | EF `IQueryingEnumerable.ToQueryString()` returns the actual generated Cypher with placeholders and no values. Command logging likewise includes text without parameter values. `Neo4jQueryTranslationException` reports unsupported translation. |

Executed centrally by the root agent after the changes (not inferred from source):

```text
dotnet build GraphBridge.sln --no-restore
# Exit 0, zero build errors.
dotnet test tests/GraphBridge.UnitTests --no-restore --logger 'console;verbosity=minimal'
# Exit 0, 35 passed.
powershell -NoProfile -ExecutionPolicy Bypass -File eng/scripts/Run-IntegrationTests.ps1 -Filter FullyQualifiedName~ProviderQueryTests
# Exit 0, 16 passed; included build with zero warnings.
```

The unit total includes provider/binding tests and `QueryTranslationTests.cs`. The live query suite covers changed `Where` closure values through the same query shape, a fresh context, EF original values and change detection, all three tracking modes with repeated keys, nullable values, GUID/enum/shadow-property mapping, ordering/paging, first/single cardinality, aggregate/scalar projections, cancellation before execution, early disposal, corrupt store values, disposed-context execution, and translation rejection. Earlier experiments found enum conversion and a paging-test construction error; the commands above are the passing reruns after their corrections.

Representative generated command shape (aliases depend on the finalized EF property indices):

```cypher
MATCH (n:`QueryPerson`)
WHERE coalesce(n.`age` >= $p0, false)
RETURN n.`Id` AS `c0`, n.`age` AS `c1`
```

The actual entity query projects every required scalar/shadow slot. Direct scalar `Select` projects only its selected slot; aggregate queries return one aggregate column. The integration suite inspects executed-query text through `ToQueryString` and validates actual results and EF state, rather than relying on this illustrative command alone.

### Exact support boundary at this checkpoint

- Entity scans; direct mapped-property comparisons (`==`, `!=`, `<`, `<=`, `>`, `>=`), boolean `&&`/`||`/`!`, null checks, and `EF.Property` shadow-property access; values must be constants or EF runtime parameters. Arbitrary methods, arithmetic and lossy casts are rejected. Enum-to-underlying compiler conversions are handled narrowly.
- `OrderBy`/`ThenBy` and descending variants over entity properties; one `Skip` followed by one `Take`; `First[OrDefault]`, `Single[OrDefault]`; `Count`, `LongCount`, `Any`; direct scalar-property `Select`. Terminal first/single preserves an existing smaller `Take` limit. Async results are required; `ToQueryString` remains available without executing transport.
- `Where`/ordering after pagination, aggregates after pagination, repeated `Skip`/`Take`, `Skip` after `Take`, and entity-dependent operators after scalar projection are explicitly rejected. Supporting an individual operator does not imply every composition is supported.
- Explicit converters are used for parameter equality and materialization. Range/order on explicitly converted properties is rejected because arbitrary converters need not preserve order. Property-to-property comparisons with different explicit converters are rejected. Default GUID/enum mappings are supported by the node-query tests.
- Null ordering is compensated and tested. String ordering otherwise follows Neo4j's ordering; culture-sensitive .NET collation equivalence is **not promised**. Arbitrary converter/collation equivalence, special floating-point values, inheritance, complex/owned values, includes, joins, grouping, paths, and relationship shaping are not established by this gate.
- Driver `RunAsync`/`FetchAsync` do not expose cancellation tokens at the used seam. Cancellation is checked before/after awaited work and cleanup is awaited; cancellation does **not** promise immediate interruption of an in-flight server operation. No abandoned task is created to simulate cancellation.
- This query checkpoint makes no persistence, explicit-transaction, retry, concurrency-token, edge-storage, migration, NativeAOT/precompiled-query, or performance claim. Consult later ticket evidence before using those features.

## 11. Node persistence implementation addendum

The following replacements now execute real node persistence; section 10's original query-only limitations describe its historical checkpoint.

| Service or seam | Implementation and lifetime | Source/version risk |
|---|---|---|
| `IDatabase` / `Database.SaveChangesAsync(IList<IUpdateEntry>, CancellationToken)` | Scoped `Update/Neo4jDatabase.cs`; uses finalized bindings and planner, own/borrowed transaction, schema verification and entry counts | Provider-facing API from pinned `Storage/Database.cs`; signature compiles against EF 10.0.12 |
| `IUpdateEntry` | Planner uses `EntityType`, `EntityState`, `SharedIdentityEntry`, `GetCurrentValue`, `GetOriginalValue`, `IsModified`, `HasTemporaryValue`, `IsStoreGenerated`; successful save uses `SetStoreGeneratedValue` | Exact members inspected in pinned `Update/IUpdateEntry.cs`; no concrete internal entry dependency in production |
| `IDbContextTransactionManager` | Scoped `Neo4jTransactionManager`; interface factory resolves the same concrete scoped instance used by query context and database | `EntityFrameworkServicesBuilder.cs` lines 117/119 declare scoped database/transaction manager; public provider contract plus explicit async-only boundary |
| `IDbContextTransaction` | `Neo4jContextTransaction` owns driver transaction/session; preserves commit acknowledgement state and primary exceptions while attempting all cleanup | EF interface from pinned source; Driver 6.3.0 methods lack cancellation-token overloads at this seam |
| `IModelValidator` | Singleton `Neo4jModelValidator` plus immutable model-derived validation; rejects unsupported Ignore save behavior except generated versions | Core validator lifetime at pinned builder line 74; read `IProperty` save behavior and generation metadata |
| Mutation unit test entry extraction | `NodeMutationTests.cs` obtains the actual EF entry via `EntityEntry.GetInfrastructure()` and views it as `IUpdateEntry` | **Test-only internal EF dependency**; localized EF1001 suppression, revalidate on upgrades; never copied into provider production code |

Pinned `ChangeTracking/Internal/StateManager.cs` lines 1410–1447 confirm entry selection, automatic acceptance on success, and discard of generated values on failure. These methods were inspected as an oracle; GraphBridge does not invoke them directly or implement replacement tracking. Explicit transaction rollback therefore does not rewind already accepted EF entities.

Central evidence: 13 live node update/transaction tests passed at the recorded M2 checkpoint. Subsequent client fault injection reproduced a cancellation-cleanup defect (6 pass, 1 fail); after correction all **57 unit tests passed**. Seven fault tests use `DispatchProxy` against the installed driver interfaces, with no production proxy dependency or database claims. Shared-machine limits currently defer the combined M2 live regression. See [ADR-0013](adr/ADR-0013.md) and test evidence (retained maintainer evidence).

## 12. GB-009 test-only join-entry observation

`JoinEntryExperimentTests` replaces scoped `IDatabase` with a recording `Database` subclass and singleton `IModelValidator` with EF's core `ModelValidator`, using `DbContextOptionsBuilder.ReplaceService`. Production validation still rejects unsupported relationships; all other EF/GraphBridge services remain registered. No internal EF calls or new runtime services were added by the experiment.

The recorder receives actual `IUpdateEntry` instances selected by EF. It captures public `GetCurrentValue`, `GetOriginalValue`, `IsModified`, `HasTemporaryValue`, `GetRelationshipSnapshotValue`, metadata and `ToEntityEntry`. Public `ISkipNavigation.JoinEntityType`, `ForeignKey` and `Inverse` identify roles without depending on generated join names or internal implicit-join flags.

All **19 observation cases passed** centrally; the complete unit suite afterward passed **76 tests**. Accepted composite-key FK changes reject in EF; association replacement yields Deleted/Added entries. For independently keyed entities, original FK values retain the old endpoint while relationship snapshots have already advanced. Use originals for persisted endpoint matching. Observed EF entry order is not a graph dependency execution order. These are EF seam results, not native graph persistence or materialization evidence; see [experiment](JOIN_ENTRY_EXPERIMENT.md) and [ADR-0014](adr/ADR-0014.md).

## 13. Association metadata and refresh capability boundary

GB-010's metadata increment uses public finalized `IForeignKey` (`Properties`, `PrincipalKey`, `PrincipalEntityType`, `IsRequired`, `IsUnique`, `IsOwnership`), `IEntityType` key/navigation enumeration, `ISkipNavigation` roles and property type mappings. No EF internal metadata helpers or new DI service replacements were added. The existing model validator and compiler produce immutable `AssociationEdgeBinding`/endpoint projections beside node bindings in the same model-keyed cache.

`ToGraphRelationship` selects FK roles explicitly using copied property-name arrays. Converter compatibility compares the actual resolved converter instances, including EF's principal-to-FK converter propagation from pinned `Storage/TypeMappingSource.cs`. Strict reference checks passed ordinary Guid and shared/inherited custom converter cases. GetNode refuses association entities, so existing node-only query/write execution cannot accidentally create join nodes. Twenty focused tests passed; see [ADR-0015](adr/ADR-0015.md).

Pinned `Internal/EntityFinder.cs`, `GetDatabaseValuesQuery` (lines 665–684), constructs a key predicate and object-array projection used by public `EntityEntry.ReloadAsync` / `GetDatabaseValuesAsync`. The current translator rejects that shape. Two public-API unit cases confirm rejection before any driver call and unchanged pending/original tracker values. This source is inspected only; GraphBridge does not call EntityFinder internals. These APIs remain unsupported, so rollback recovery guidance uses a fresh context.

## 14. Unregistered association mutation planning

`GraphMutationPlanner` and `GraphAssociationMutationPlanner` consume the same public `IUpdateEntry` interface as node persistence; neither is registered in `Neo4jDatabase` execution yet. Association key comparisons use `IProperty.GetKeyValueComparer()` (pinned `Metadata/IProperty.cs`, line 94), while existing current/original/temporary/modified/generated APIs retain their prior source-map coverage. No new internal EF API or state manager was introduced.

The fifteen planner tests replace only `IDatabase` and the model-cache-key factory, retaining the production validator and actual EF entries. Two boundary tests deliberately modify an actual entry's original value (`IUpdateEntry.SetOriginalValue`, pinned interface line 31) or temporary flag after EF preparation to exercise planner rejection. These are test-only fault injections, not production state manipulation. Fourteen renderer tests additionally passed; all 127 unit tests passed. An independent source review found no remaining concrete issue in the inspected planning/rendering scope. Live command semantics and runtime association execution remain pending.

## 15. Association read preparation and EF fix-up source path

The staged `GraphReadBinding` maps every finalized property to one EF `GetIndex()` slot. Association foreign-key slots read the corresponding principal store key from source/target nodes; payload slots read relationship properties. The dependent property's existing type mapping owns conversion to its CLR value. Typed graph owners distinguish these storage locations without representing an edge as a node. Runtime association query-root activation remains gated separately.

Pinned `ShapedQueryCompilingExpressionVisitor.InjectStructuralTypeMaterializers` already generates entity materialization and query tracking calls. `QueryContext.StartTracking` delegates to `StateManager.StartTrackingFromQuery`, which retains the supplied `IEntityType` for shared CLR types and marks the entry unchanged from query. `NavigationFixer.InitialFixup` and `FixupSkipNavigations` use the join FKs to locate tracked principals and update navigations. These internal methods are source-inspected behavior, not APIs called directly by GraphBridge.

A test-only subclass of the public shaped-query visitor exposes the same protected materializer injection as the production compiler, using actual query services and deterministic projected row values. Twenty-one cases pass: six row orders, explicit and named dictionary joins, from-query tracking events, originals, identity, tracking-mode isolation, unloaded navigation flags and malformed input before tracking. The probe now uses production `GraphReadBinding.ReadRow`; duplicate and missing cardinality diagnostics reject before a tracking event. No direct internal fix-up APIs or replacement state manager are used. Live fresh-context graph reads and duplicate physical relationship rejection remain required; the runtime root still rejects association queries.

## 16. Prepared row guards and shared schema validation

The read binding's hidden scalar key projection uses finalized property metadata and existing property conversion into `ValueBuffer`. Cardinality validation precedes decoding; the complete row is decoded before materializer invocation. No new EF internal API, service replacement or state owner was added. Previously materialized rows are not undone when a subsequent row fails.

`GraphKeyConstraintValidator` operates on immutable binding requirements and schema observations, without EF service calls or driver ownership. Existing `Neo4jDatabase` writes invoke it after their existing `SHOW CONSTRAINTS` query. Future association reads must perform the same selected-database check; that runtime integration is still pending. Eleven schema cases and the seven existing production SaveChanges failure cases pass in the 214-test unit run. Eight new direct-driver guard cases compile but have not run live.

## 17. Runtime association activation after live command acceptance

The 56-case combined live gate passed, including 11 association command and eight row-guard cases. `Neo4jDatabase` now executes `GraphMutationPlan.Operations`, verifies `RequiredNodeBindings`, checks each lock/mutation separately, publishes generated values after successful owned commit or explicit batch completion, and returns `ExpectedStateEntries`. It still consumes public `IUpdateEntry` and retains the existing transaction manager. Ten additional real EF save cases passed in focused runs, including missing endpoint constraints, atomic failures, duplicate edges, dependency ordering and competing EF writers.

The query expression now holds `GraphReadBinding` for nodes or associations; the existing EF translator, compiler and injected materializers remain in use. Association enumeration checks schema through the selected runner (including a borrowed transaction) and calls the guarded row decoder before EF shaping. Association aggregates remain explicitly unsupported. Inherited FK converter safety uses public `IProperty.GetContainingForeignKeys()` (pinned `Metadata/IProperty.cs:54`) and `IReadOnlyProperty.GetProviderClrType()` (`Metadata/IReadOnlyProperty.cs:150`), following principal-key metadata to reject unsafe ordering/ranges. No new internal EF calls or state manager were introduced. All 216 unit tests pass; new runtime query live acceptance and combined regression are still pending at this checkpoint.

The subsequent runtime query gate passed all eight live cases after correcting colliding test fixture constraint names. Final combined verification passed 74 live tests with a clean solution build. Actual EF queried joins fix up references, inverse collections and skip navigations in either row-loading order without marking entire navigations loaded; the fresh-context query/save/read/delete flow passes. This adds runtime evidence to the materializer probes without expanding the documented unsupported query shapes.

## 18. M4 native facade and identified relationships (initial scope verified)

The native facade uses public `IDatabaseFacadeDependenciesAccessor.Context` ([pinned source](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/IDatabaseFacadeDependenciesAccessor.cs), declaration line 29) to obtain the owning context and public `IConcurrencyDetector.EnterCriticalSection()` ([pinned source](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Infrastructure/IConcurrencyDetector.cs), line 28) to protect context operations. Both signatures were rechecked in the local v10.0.12 source checkout. The existing scoped `Neo4jTransactionManager` owns/borrows transport; DTO projection is an explicit callback and never injects an EF shaper or changes EF tracking.

Identified-edge bindings project the same finalized EF property slots and reuse the existing query compiler/materializer pipeline and public `IUpdateEntry` save planner. The relationship's application ID becomes the EF key; endpoint FKs remain node-projected slots. Read/write schema guards and physical-ID diagnostics are storage contracts, not another identity map. Hosted acceptance passes 280 unit and 106 live tests on `a8a92d6`; [ADR-0016](adr/ADR-0016.md) records verified boundaries.

Key-comparer validation uses public `IProperty.GetKeyValueComparer()` and `CoreTypeMapping.KeyComparer`. Their default-object relationship was inspected in pinned `Metadata/Internal/Property.cs` and `Metadata/RuntimeProperty.cs`, without calling those internal classes from production code. Nondefault resolved comparers are rejected for identity slots because Cypher equality cannot be assumed to implement them. Shared key conversion now requires model/wire round trips before writes and before EF shaping. Native projector recursion has a provider-owned scoped lease in addition to EF's intentionally reentrant concurrency detector; no new EF service is registered for that guard.

## 19. M5 storage witness and schema inspection

The manifest derives from the same finalized `IModel` and compiled bindings. Public pinned `IModel.GetModelDependencies()` (Metadata/IModel.cs:107) rejects uninitialized models; `IEntityType.GetKeys()` (155) and `GetIndexes()` (421) expose alternate-key and index intent. `IReadOnlyProperty.GetValueConverter()` (144), existing type mapping/save-behavior metadata and endpoint FK metadata describe storage facts. No new internal EF API or service replacement is introduced. Runtime index sort direction is deliberately not queried: pinned RuntimeIndex.cs:61 throws for unavailable `IsDescending` metadata, so the manifest caveats those facets.

The database facade reuses public context/concurrency access and the existing scoped native-operation lease. Inspection borrows the current provider transaction or owns a separate selected-database read session; it changes no tracker state. The new manifest suite has 24 passing cases within the 378-case unit run. Full live schema acceptance is recorded in the execution report.

The enlarged unit suite exposed an old test assumption about EF retaining models across contexts. Pinned ModelSource.cs:76–77 sizes design/runtime cache entries at 150/100 units; EntityFrameworkServicesBuilder.cs:302 sets a 10,240-unit limit. Three binding-cache tests now explicitly pass `UseModel(context.Model)` to the second context, verifying the provider's actual per-model cache contract independently of EF eviction. Production binding cache behavior is unchanged.

## 20. M5.2 explicit migration facade

Migration operations reuse the public `IDatabaseFacadeDependenciesAccessor.Context`, `IDbContextOptions` lookup, `IConcurrencyDetector` and existing scoped native-operation lease described above. No EF service replacement, relational migration dependency or new internal API is introduced. Active EF/ambient transactions reject because schema and data steps require separate explicitly owned driver transactions. Migration commands never update EF tracking state or run during ordinary context activation.

The pinned Neo4j.Driver 6.3.0 package XML confirms `BeginTransactionAsync(Action<TransactionConfigBuilder>)`, `WithTimeout(TimeSpan?)` and `WithMetadata(IDictionary<string, object>)`; transport supplies finite timeouts and migration/step metadata to those exact APIs. No managed retry callback is used. Typed plans and driver journal execution stay in the existing runtime package; `dotnet ef migrations` services remain separate, unimplemented design-time work.

## 21. M6 ordinary node-reference binding and Include prerequisites

`ToGraphReference` uses public `ReferenceCollectionBuilder.HasAnnotation(string, object?)` (pinned v10.0.12 `Metadata/Builders/ReferenceCollectionBuilder.cs:63`); its generic extension delegates to that public base builder. `OnDelete(DeleteBehavior)` is public at line 169. The provider reads finalized `IForeignKey` requiredness, uniqueness, ownership, principal key and ordered property metadata through existing binding services. No EF service replacement or internal builder access is introduced. `ValueGenerated.OnUpdateSometimes` is a distinct EF 10 flag; reference principal keys must allow only `Never` or `OnAdd`, not infer stability by checking `OnAddOrUpdate` alone.

The pinned navigation expander (`Query/Internal/NavigationExpandingExpressionVisitor.ExpressionVisitors.cs`, `ExpandForeignKey`, lines 355–507) expands collection navigations through correlated subqueries and required references through joins, then wraps Include expressions. The InMemory provider's projection visitor and shaper-processing visitor illustrate collection shaping and navigation-loaded/fixup behavior. These internal/private implementation examples were inspected as source evidence only: GraphBridge neither calls their helpers nor depends on the InMemory provider. Ordinary root queries and schema witnesses have separate live acceptance; the direct Include implementation is described in section 22. See [ADR-0021](adr/ADR-0021.md).

The subsequent native-reference write planner uses public `IUpdateEntry.IsConceptualNull(IProperty)` (pinned `Update/IUpdateEntry.cs:170`), existing temporary/original/current-value APIs and the finalized property's key comparer to reject missing or reassigned endpoints before transport. EF entries remain the owners of both node and native membership commands. Distinct reference identity determines the returned EF entry count and driver-error entry list; only the node command publishes generated values. No new EF service or internal API is introduced. Live acceptance is tracked separately from source/build evidence.

Root reads reuse the existing EF materializer pipeline with derived FK slots and pre-shaping membership/key diagnostics. No new EF API is called. Pinned `ChangeTracking/Internal/NavigationFixer.cs:1471` and `InternalEntityEntry.cs:669` explain the observed distinction: reference fixup sets the populated reference's loaded flag, while an inverse collection is not thereby marked fully loaded. Those internal implementations are source evidence only. The schema witness describes dependent ownership using finalized metadata; logical derived FK names are exposed for review but excluded from the storage fingerprint, consistently with existing edge FK slots.

## 22. Direct native-reference Include

The provider replaces `IQueryTranslationPreprocessorFactory` with its own factory and subclasses public `QueryTranslationPreprocessor`. Its `Process(Expression)` override (pinned v10.0.12 `Query/QueryTranslationPreprocessor.cs:51`) extracts one direct typed Include and still calls `base.Process` on the remaining query, preserving EF normalization and tracking metadata. A provider expression carries the navigation to translation; unsupported shapes reject explicitly. This avoids the required-reference inner join hiding orphan roots.

Root and included values use separate `InjectStructuralTypeMaterializers` calls with the same EF `QueryContext`. Tracking and standalone identity resolution remain EF-owned. Public `IPropertyBase.GetMemberInfo(bool, bool)` (`Metadata/IPropertyBase.cs:51`), collection accessor `Add(object, object, bool)` / `GetOrCreate(object, bool)` (`IClrCollectionAccessor.cs:29/84`) implement no-tracking CLR fixup. `QueryContext.SetNavigationIsLoaded(object, INavigationBase)` (`Query/QueryContext.cs:61`) and `INavigationBase.SetIsLoadedWhenNoTracking(object)` (`Metadata/INavigationBase.cs:46`) provide loaded-state semantics. Tracked references rely on actual EF fixup; only requested tracked collections are explicitly marked loaded, preserving unresolved local FK changes. Inverse collections are never marked complete by reference Include.

Public `IReadOnlyEntityType.GetDeclaredQueryFilters()` (`Metadata/IReadOnlyEntityType.cs:36`) and navigation eager-loading metadata guard unsupported target filters/AutoInclude. No InMemory runtime dependency or direct NavigationFixer call is introduced. Twenty-six offline pipeline cases and nine provider live facts pass in hosted run 38118649416, separately from source-signature evidence; the actual Blogging workflow and three relationship/Include acceptance facts pass as well.

[services]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Infrastructure/EntityFrameworkServicesBuilder.cs
[options]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Infrastructure/IDbContextOptionsExtension.cs
[options-info]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Infrastructure/DbContextOptionsExtensionInfo.cs
[provider]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/IDatabaseProvider.cs
[provider-generic]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/DatabaseProvider.cs
[database-interface]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/IDatabase.cs
[database]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/Database.cs
[creator]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/IDatabaseCreator.cs
[transaction-manager]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/IDbContextTransactionManager.cs
[transaction]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/IDbContextTransaction.cs
[reset]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Infrastructure/IResettableService.cs
[mapping-interface]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/ITypeMappingSource.cs
[mapping-source]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/TypeMappingSource.cs
[mapping-base]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/TypeMappingSourceBase.cs
[core-mapping]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/CoreTypeMapping.cs
[model-validator]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Infrastructure/ModelValidator.cs
[query-context-factory]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/IQueryContextFactory.cs
[query-context]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/QueryContext.cs
[compilation-factory]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/IQueryCompilationContextFactory.cs
[compilation-context]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/QueryCompilationContext.cs
[pre-factory]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/IQueryTranslationPreprocessorFactory.cs
[post-factory]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/IQueryTranslationPostprocessorFactory.cs
[method-factory]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/IQueryableMethodTranslatingExpressionVisitorFactory.cs
[method-visitor]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/QueryableMethodTranslatingExpressionVisitor.cs
[shaper-factory]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/IShapedQueryCompilingExpressionVisitorFactory.cs
[shaper]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/ShapedQueryCompilingExpressionVisitor.cs
[shaper-dependencies]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/ShapedQueryCompilingExpressionVisitorDependencies.cs
[materializer]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/IStructuralTypeMaterializerSource.cs
[state-manager]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/ChangeTracking/Internal/StateManager.cs
[update-entry]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Update/IUpdateEntry.cs
[value-buffer]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/ValueBuffer.cs
[structural-shaper]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/StructuralTypeShaperExpression.cs
[property-base]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Metadata/IPropertyBase.cs
[query-parameter]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Query/QueryParameterExpression.cs
[cosmos-di]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore.Cosmos/Extensions/CosmosServiceCollectionExtensions.cs
[cosmos-shaper]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore.Cosmos/Query/Internal/CosmosShapedQueryCompilingExpressionVisitor.cs
[cosmos-enumerable]: https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore.Cosmos/Query/Internal/CosmosShapedQueryCompilingExpressionVisitor.QueryingEnumerable.cs
[mongo-di]: https://github.com/mongodb/mongo-efcore-provider/blob/dec7e26fcc87dec0d31fd704a7ffef1ccd90d7f0/src/MongoDB.EntityFrameworkCore/Extensions/MongoServiceCollectionExtensions.cs
[mongo-query-notes]: https://github.com/mongodb/mongo-efcore-provider/blob/dec7e26fcc87dec0d31fd704a7ffef1ccd90d7f0/src/MongoDB.EntityFrameworkCore/Query/AGENTS.md

## Feature increment: DTO projections

FS-14 adds a narrowly fenced `IStateManager.TryGetEntry(object, bool)` / `InternalEntryBase.this[IPropertyBase]` read for an absent optional tracked Include. Source checked at EF `v10.0.12`: `QueryContext.InitializeStateManager(false)` uses its scoped `Dependencies.StateManager`, and `SetNavigationIsLoaded` calls that manager's `TryGetEntry(entity).SetIsLoaded`. The provider reads current FK slots only for TrackAll, avoiding `DbContext.Entry` automatic DetectChanges and preserving local edits. Standalone identity-resolution queries do not use this scoped-manager check. No service replacement or entity state mutation is introduced by the helper. Three internal API analyzer sites are fenced within that helper only and remain a pinned-version risk; live tracking acceptance passes under ADR-0032, including all tracking modes and pending local FK changes.

FS-14 model validation recognizes the exact EF `ChangeTracking.Internal.NullableValueComparer<T>` type, using its `IInfrastructure<ValueComparer>.Instance` to verify reference identity with the resolved type-mapping key comparer. Pinned `ValueComparerExtensions.ToNullableComparer` and `Property.GetKeyValueComparer` wrap ordinary mapping comparers for nullable FK slots. Only that exact generic type is unwrapped once; custom wrappers, subclasses, or custom inner equality are not accepted. The type reference is an internal API risk fenced within the validation helper. Optional Guid/long FK model tests and custom FK/principal comparer rejection tests guard this boundary.

FS-15 typed native search adds no EF service or internal EF API. Its internal native-executor preflight callback runs under the existing `IConcurrencyDetector` and `Neo4jTransactionManager.EnterNativeOperation` guards, using the same owned/borrowed driver transaction as the result query. Bounded metadata observations use `IAsyncTransaction.RunAsync`, `IResultCursor` and `IRecord.Values`; typed projections receive Neo4j.Driver `INode` / `IRelationship` and never enter EF materializers or tracking. Protocol tests cover recursive access, ownership and cleanup; live evidence is recorded under ADR-0033. Search operation spans use the existing value-free command diagnostics.

FS-16 bounded paths add no EF service or internal EF API. `QueryPathsAsync` uses the same native executor with one fixed statement, capped anchor candidates and limit+1 path collection. `IPath.Nodes`/`Relationships` and `IRelationship.StartNodeElementId`/`EndNodeElementId` supply physical ordering/orientation before caller projection; native element IDs are used only for structural checks, never as EF application keys. Ten unit cases and live cycle/direction tests verify repeated-node ordering and relationship uniqueness. Explicit native pattern metadata does not enter or override finalized EF `IModel` bindings.

FS-13 extrema use the pinned `QueryableMethodTranslatingExpressionVisitor.TranslateMin` / `TranslateMax` overrides with `(ShapedQueryExpression, LambdaExpression?, Type)` and the existing compiled aggregate ValueBuffer slot. There is no new EF service or client aggregate executor. Integer input metadata comes from finalized `IProperty.ClrType` / `IsNullable`; explicit converters remain rejected. `NorthwindAggregateOperatorsQueryTestBase` original selector/no-selector, empty, nullable-widening and Count methods are test-only references at the same `v10.0.12` tag. The two-property original-data adapter and verified limits are recorded in [ADR-0031](adr/ADR-0031.md).

FS-13 projection shaping reuses `StructuralTypeShaperExpression`, `InjectStructuralTypeMaterializers`, `ValueBuffer` and `QueryCompilationContext.QueryContextParameter`. DTO query parameters are read from the pinned public `QueryContext.Parameters` dictionary; no new EF service replacement is added. Source audit: `src/EFCore/Query/Internal/ExpressionTreeFuncletizer.cs`, `VisitNew` / `VisitMemberInit` / `ProcessEvaluatableRoot.TryHandleNonEvaluatableAsRoot` at tag `v10.0.12` confirms that captured row-independent DTO constructors may become `QueryParameterExpression`, whereas nonparameterized construction roots are preserved. This normalization is version-sensitive and covered by projection/rebinding regressions. `ShapedQueryCompilingExpressionVisitor` retains its constant verification before EF materializer injection. See [ADR-0030](adr/ADR-0030.md).

## Feature increment: explicit EXPLAIN

`Neo4jExplainDatabaseFacadeExtensions` reuses `IDatabaseFacadeDependenciesAccessor`, `IDbContextOptions`, `IConcurrencyDetector` and the existing `Neo4jTransactionManager.EnterNativeOperation` guard. No additional EF service registration or internal query compilation access is introduced. `Neo4j.Driver` 6.3.0 `IResultSummary`/`IPlan` provide actual plans and counters; compilation validates that pinned public surface. The exact-statement fingerprint is diagnostic only. Transaction/cancellation/parameter ownership and limitations are recorded in [ADR-0023](adr/ADR-0023.md).

## Feature increment: Contains

The existing pinned `TranslateContains(ShapedQueryExpression, Expression)` override now handles scalar node projections through the existing Any aggregate. Local-collection predicates inspect the output of EF 10.0.12 `QueryableMethodNormalizingExpressionVisitor.TryConvertCollectionContainsToQueryableContains` and unwrap its AsQueryable around an already supported parameter/constant. No EF normalization, funcletization, cache or entity state ownership is replaced. The pinned `ExpressionTreeFuncletizer` rewrites default span Contains; non-null comparer spans have an earlier unsupported interpretation path. Live compilation-event evidence and supported equality boundaries are recorded in [ADR-0024](adr/ADR-0024.md).

Command tracing uses public .NET `ActivitySource` and the pinned Driver `IAsyncQueryRunner.RunAsync` overloads; it adds no EF internal service dependency. See [COMMAND_TRACING.md](../features/COMMAND_TRACING.md) for scope and privacy.

FS-10 expands this source with operation, statement/cursor and transaction lifetimes. `TracedResultCursor` forwards the pinned `IResultCursor` contract (`Current`, `IsOpen`, `KeysAsync`, `PeekAsync`, `FetchAsync`, `ConsumeAsync`, `IAsyncEnumerable<IRecord>`), inspected against the resolved Driver assembly. No new EF service replacement is added; existing querying enumerable, database persistence, facade helpers and owned transport remain execution authorities. Live acceptance is recorded in ADR-0028 and the implementation ledger.

The conformance expansion references unmodified `NorthwindWhereQueryTestBase`, `NorthwindSelectQueryTestBase` and `NorthwindAsNoTrackingQueryTestBase` from the same EF 10.0.12 package/source tag. The explicitly Customer-only fixture and local/hosted live acceptance are documented in the specification-test README; it adds no runtime EF internal dependency.

The prepared update adapter uses the same tag's `Update/UpdatesTestBase.cs` (including its nested `UpdatesFixtureBase`) and `TestModels/UpdatesModel/UpdatesContext.SeedAsync`. The pinned `TestHelpers.ExecuteWithStrategyInTransactionAsync` uses synchronous disposal and cross-context transaction sharing; the GraphBridge adapter explicitly substitutes independent async contexts, per-save transactions and serialized reseeding. Original update test bodies/assertions are invoked unchanged. An offline finalized-model check verifies the retained scalar and concurrency contract. This test-only adaptation passes hosted run 38139246843 (eleven original update cases plus its fixture contract) and adds no runtime EF service dependency.

Finder translation is source-checked against EF 10.0.12 `Internal/EntityFinder.cs` (`FindAsync`, `BuildLambda`) and `Extensions/Internal/ExpressionExtensions.cs` (`BuildPredicate`, namespace `Microsoft.EntityFrameworkCore.Internal`). GUID keys use `Infrastructure/ExpressionExtensions.CreateEqualsExpression`, producing static `object.Equals`. The runtime recognizes that BCL method and existing EF parameter/property expressions; it adds no service replacement or direct internal EntityFinder dependency. Unit tests invoke the pinned internal predicate builder explicitly, so this test seam must be rechecked on EF upgrades. See [ADR-0025](adr/ADR-0025.md) for equality/type boundaries and hosted acceptance.

The direct relationship conformance adapter invokes seven unchanged methods from pinned `NorthwindIncludeQueryTestBase`. Its required Customer/Order native reference preserves all upstream data and the original model's scalar fields/asserters; optional-reference behavior is not implied. Both model/data checks and original live methods pass hosted 38139977914. This adds no runtime EF service replacement.
# FS-17 compiled-command checkpoint

Source-checked against EF 10.0.12: `Query/Internal/CompiledQueryCache.cs`, `Query/CompiledQueryCacheKeyGenerator.cs` and `Infrastructure/EntityFrameworkServicesBuilder.cs`. EF's default memory cache has a 10,240-unit size limit; compiled-query entries use size 10. The provider adds no cache service replacement or runtime dependency on those internal classes. Application-supplied caches have their own policy. Public `EF.CompileAsyncQuery` delegates are caller-owned, outside the automatic cache's lifetime policy.

`Neo4jShapedQueryCompilingExpressionVisitor` now renders finalized Cypher once and passes the immutable command with the existing shape/shaper into execution. Runtime values, sessions, row buffers and context state remain per execution. Live regression and performance acceptance are tracked separately in [ADR-0035](adr/ADR-0035.md) and the ledger; preparation throughput alone is not server-query throughput.

## FS-18 design-time command subset

Source checked against EF 10.0.12 `Design/IDesignTimeDbContextFactory.cs`, `EFCore.Design/Design/Internal/DbContextOperations.cs` and `DesignTimeServicesBuilder.cs`. The public factory supplies the sample context; actual CLI list/info succeed. `DbContextOperations.GetContextInfo` intentionally reports connection information only for relational providers and uses synchronous context disposal. The sample owns its lazy driver and implements both disposal paths.

No provider design-service registration, relational `IMigrator`, service replacement or new runtime internal API dependency is introduced. EF Design and aligned Roslyn workspace packages are private sample dependencies. The optional tooling command host reuses public graph migration preview/validation APIs. Actual unsupported EF migration-command behavior, privacy and ownership contracts are recorded in [ADR-0036](adr/ADR-0036.md).

## FS-19 asynchronous persistence decision

Source checked at EF 10.0.12: `DbContext.SaveChanges(bool)` performs events/interception/change detection before calling `StateManager.SaveChanges`; `ChangeTracking/Internal/StateManager.cs` returns zero for no changed entries or no entries to save before dispatching to `IDatabase`. Existing provider sync rejection remains unchanged. Tests use real public context calls and the existing scripted driver seam; no new internal runtime dependency or EF service replacement is introduced. See [ADR-0037](adr/ADR-0037.md) for the driver/hosting evaluation, preserved transaction ownership and no-op boundary.

## FS-20 bulk translation checkpoint

Pinned EF 10.0.12 `Extensions/EntityFrameworkQueryableExtensions.cs` exposes the actual core `ExecuteDeleteAsync` and `ExecuteUpdateAsync(Action<UpdateSettersBuilder<T>>)` APIs. `Query/QueryableMethodTranslatingExpressionVisitor.cs` recognizes their method infos, unpacks setter tuples into `ExecuteUpdateSetter(LambdaExpression PropertySelector, Expression ValueExpression)` and invokes the protected virtual translation hooks. GraphBridge overrides those hooks and returns an immutable nonquery expression. `ShapedQueryCompilingExpressionVisitor.VisitExtension` compiles it into `Task<int>` using the current `Neo4jQueryContext`; no relational service or external expression evaluator is introduced. The existing public query context provides runtime parameters, cancellation and concurrency detection. Captured primary-constructor state was avoided by using the base `QueryCompilationContext` property.

Actual EF calls compile and pass seven focused protocol cases within 718 units and fourteen bulk live cases within 255 provider cases. The store executor uses finalized graph bindings, existing schema guards and transaction ownership; it does not materialize or mutate tracked entities. This expression pipeline is pinned-version-sensitive and must be rechecked on EF upgrades. Graph-root compilation preserves read-binding identity/membership diagnostics and metadata-owned lock keys. The scoped node benchmark gate is accepted with retained raw/protocol evidence; see [ADR-0038](adr/ADR-0038.md) and the report (retained maintainer evidence).

## Fluent Cypher clause compiler (FGQ, 2026-10-12)

The native clause compiler reuses GraphBindingCompiler/GraphPropertyBinding and the finalized IModel. It uses the already audited public GetDeclaredQueryFilters() metadata API to reject implicit filter bypass, and introduces no EF internal service replacement. Native execution delegates to the existing DatabaseFacade.QueryCypherAsync transaction/concurrency/diagnostic path. ADR 0041 separates native scalar DTO semantics from EF LINQ tracking and null compensation. Live acceptance remains recorded in IMPLEMENTATION_STATUS.md.

## Native DateTimeOffset payload mapping (NEXT-RUNTIME, 2026-10-12)

Pinned v10.0.12 `Storage/CoreTypeMapping.cs` constructor inspected: CoreTypeMappingParameters accepts `clrType`, `converter`, `comparer`, `keyComparer`, `providerValueComparer`, followed by generation/element/JSON arguments. The native mapping supplies its ordinary EF ValueComparer through the named `comparer` parameter; clone/composition preserve EF parameters. EqualsExact tracks offset-only edits; identity/concurrency roles reject before activation. Existing nullable comparer adaptation and EF tracking remain authoritative. No additional EF internal APIs, provider services or state owners are introduced. Existing TranslateWhere/OrderBy hooks emit the proved UTC instant syntax while projections retain native storage. [ADR-0045](adr/ADR-0045.md).
