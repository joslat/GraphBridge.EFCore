# Named upstream EF inventory

Pinned upstream: **EF Core 10.0.12**, source tag `v10.0.12`, commit `001bfac21c1910cdf1110ca256c9ea0a06f374ca`.

Current accepted evidence: local 2026-10-11 FS-13 aggregate run passes **76 upstream cases (75 distinct methods), zero failed/skipped**, plus five separate offline model/data checks (81 total). Retained evidence: `.artifacts/aggregates/live.log`; every named adapter row below is reconciled against its passing output. Eleven original aggregate methods extend the earlier 65-case DTO checkpoint. Builds, 222 live provider integration cases and exact owned-fixture cleanup also pass.

A case is an invocation of an original upstream method with selected arguments. The two no-tracking API variants invoke the same method with different arguments: **57 accepted cases cover 56 distinct original methods**. The previous 44-case checkpoint covered 43 methods. Earlier tracking used ?methods? loosely for cases; this inventory makes that distinction explicit without inflating coverage. Adapter model/data checks are never counted as upstream cases.

[Fixture adaptations and historical failures](../GraphBridge.SpecificationTests/README.md) describe converted scalars, Customer-only scope, update transaction helper and the explicitly required Customer/Order reference. Original test bodies and result assertions remain unchanged. The original optional-reference model, shared transaction helper, complete Northwind domain and unselected methods are not claimed by these adaptations.

| Original upstream invocation | Discovered adapter case | Latest verified disposition |
|---|---|---|
| `ComparisonOperatorTranslationsTestBase.Equal()` | [ComparisonOperatorTranslationsNeo4jTest.Equal](../GraphBridge.SpecificationTests/ComparisonOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `ComparisonOperatorTranslationsTestBase.NotEqual()` | [ComparisonOperatorTranslationsNeo4jTest.NotEqual](../GraphBridge.SpecificationTests/ComparisonOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `ComparisonOperatorTranslationsTestBase.GreaterThan()` | [ComparisonOperatorTranslationsNeo4jTest.GreaterThan](../GraphBridge.SpecificationTests/ComparisonOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `ComparisonOperatorTranslationsTestBase.GreaterThanOrEqual()` | [ComparisonOperatorTranslationsNeo4jTest.GreaterThanOrEqual](../GraphBridge.SpecificationTests/ComparisonOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `ComparisonOperatorTranslationsTestBase.LessThan()` | [ComparisonOperatorTranslationsNeo4jTest.LessThan](../GraphBridge.SpecificationTests/ComparisonOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `ComparisonOperatorTranslationsTestBase.LessThanOrEqual()` | [ComparisonOperatorTranslationsNeo4jTest.LessThanOrEqual](../GraphBridge.SpecificationTests/ComparisonOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `LogicalOperatorTranslationsTestBase.And()` | [LogicalOperatorTranslationsNeo4jTest.And](../GraphBridge.SpecificationTests/LogicalOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `LogicalOperatorTranslationsTestBase.And_with_bool_property()` | [LogicalOperatorTranslationsNeo4jTest.And_with_bool_property](../GraphBridge.SpecificationTests/LogicalOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `LogicalOperatorTranslationsTestBase.Or()` | [LogicalOperatorTranslationsNeo4jTest.Or](../GraphBridge.SpecificationTests/LogicalOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `LogicalOperatorTranslationsTestBase.Or_with_bool_property()` | [LogicalOperatorTranslationsNeo4jTest.Or_with_bool_property](../GraphBridge.SpecificationTests/LogicalOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `LogicalOperatorTranslationsTestBase.Not()` | [LogicalOperatorTranslationsNeo4jTest.Not](../GraphBridge.SpecificationTests/LogicalOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `LogicalOperatorTranslationsTestBase.Not_with_bool_property()` | [LogicalOperatorTranslationsNeo4jTest.Not_with_bool_property](../GraphBridge.SpecificationTests/LogicalOperatorTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `EnumTranslationsTestBase.Equality_to_constant()` | [NullableEnumTranslationsNeo4jTest.Equality_to_constant](../GraphBridge.SpecificationTests/NullableEnumTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `EnumTranslationsTestBase.Equality_to_parameter()` | [NullableEnumTranslationsNeo4jTest.Equality_to_parameter](../GraphBridge.SpecificationTests/NullableEnumTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `EnumTranslationsTestBase.Equality_nullable_enum_to_constant()` | [NullableEnumTranslationsNeo4jTest.Equality_nullable_enum_to_constant](../GraphBridge.SpecificationTests/NullableEnumTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `EnumTranslationsTestBase.Equality_nullable_enum_to_parameter()` | [NullableEnumTranslationsNeo4jTest.Equality_nullable_enum_to_parameter](../GraphBridge.SpecificationTests/NullableEnumTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `GuidTranslationsTestBase.New_with_constant()` | [GuidTranslationsNeo4jTest.New_with_constant](../GraphBridge.SpecificationTests/GuidTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `GuidTranslationsTestBase.New_with_parameter()` | [GuidTranslationsNeo4jTest.New_with_parameter](../GraphBridge.SpecificationTests/GuidTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `EnumTranslationsTestBase.Equality_nullable_enum_to_null_constant()` | [NullableEnumTranslationsNeo4jTest.Equality_nullable_enum_to_null_constant](../GraphBridge.SpecificationTests/NullableEnumTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `EnumTranslationsTestBase.Equality_nullable_enum_to_null_parameter()` | [NullableEnumTranslationsNeo4jTest.Equality_nullable_enum_to_null_parameter](../GraphBridge.SpecificationTests/NullableEnumTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `EnumTranslationsTestBase.Equality_nullable_enum_to_nullable_parameter()` | [NullableEnumTranslationsNeo4jTest.Equality_nullable_enum_to_nullable_parameter](../GraphBridge.SpecificationTests/NullableEnumTranslationsNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_simple(true)` | [NorthwindCustomersNeo4jTest.Where_simple](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_simple_closure(true)` | [NorthwindCustomersNeo4jTest.Where_simple_closure](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_indexer_closure(true)` | [NorthwindCustomersNeo4jTest.Where_indexer_closure](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_dictionary_key_access_closure(true)` | [NorthwindCustomersNeo4jTest.Where_dictionary_key_access_closure](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_tuple_item_closure(true)` | [NorthwindCustomersNeo4jTest.Where_tuple_item_closure](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_named_tuple_item_closure(true)` | [NorthwindCustomersNeo4jTest.Where_named_tuple_item_closure](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_simple_closure_constant(true)` | [NorthwindCustomersNeo4jTest.Where_simple_closure_constant](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_simple_closure_via_query_cache(true)` | [NorthwindCustomersNeo4jTest.Where_simple_closure_via_query_cache](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_method_call_closure_via_query_cache(true)` | [NorthwindCustomersNeo4jTest.Where_method_call_closure_via_query_cache](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_field_access_closure_via_query_cache(true)` | [NorthwindCustomersNeo4jTest.Where_field_access_closure_via_query_cache](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindWhereQueryTestBase.Where_property_access_closure_via_query_cache(true)` | [NorthwindCustomersNeo4jTest.Where_property_access_closure_via_query_cache](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_scalar(true)` | [NorthwindCustomersNeo4jTest.Select_scalar](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_customer_table(true)` | [NorthwindCustomersNeo4jTest.Select_customer_table](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_customer_identity(true)` | [NorthwindCustomersNeo4jTest.Select_customer_identity](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_into(true)` | [NorthwindCustomersNeo4jTest.Select_into](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_anonymous_one(true)` | [NorthwindCustomersNeo4jTest.Select_anonymous_one](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_anonymous_two(true)` | [NorthwindCustomersNeo4jTest.Select_anonymous_two](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_anonymous_three(true)` | [NorthwindCustomersNeo4jTest.Select_anonymous_three](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_anonymous_bool_constant_true(true)` | [NorthwindCustomersNeo4jTest.Select_anonymous_bool_constant_true](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_anonymous_with_object(true)` | [NorthwindCustomersNeo4jTest.Select_anonymous_with_object](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_anonymous_nested(true)` | [NorthwindCustomersNeo4jTest.Select_anonymous_nested](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_anonymous_empty(true)` | [NorthwindCustomersNeo4jTest.Select_anonymous_empty](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindSelectQueryTestBase.Select_anonymous_literal(true)` | [NorthwindCustomersNeo4jTest.Select_anonymous_literal](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAsNoTrackingQueryTestBase.Entity_not_added_to_state_manager(false, true)` | [NorthwindCustomersNeo4jTest.No_tracking_entity](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAsNoTrackingQueryTestBase.Entity_not_added_to_state_manager(true, true)` | [NorthwindCustomersNeo4jTest.No_tracking_explicit_parameter](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAsNoTrackingQueryTestBase.Can_get_current_values(true)` | [NorthwindCustomersNeo4jTest.No_tracking_reads_current_database_values](../GraphBridge.SpecificationTests/NorthwindCustomersNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Save_partial_update()` | [UpdatesNeo4jTest.Save_partial_update](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Save_partial_update_on_missing_record_throws()` | [UpdatesNeo4jTest.Save_partial_update_on_missing_record_throws](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Save_partial_update_on_concurrency_token_original_value_mismatch_throws()` | [UpdatesNeo4jTest.Save_partial_update_on_concurrency_token_original_value_mismatch_throws](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Can_remove_partial()` | [UpdatesNeo4jTest.Can_remove_partial](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Remove_partial_on_missing_record_throws()` | [UpdatesNeo4jTest.Remove_partial_on_missing_record_throws](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Remove_partial_on_concurrency_token_original_value_mismatch_throws()` | [UpdatesNeo4jTest.Remove_partial_on_concurrency_token_original_value_mismatch_throws](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Mutation_of_tracked_values_does_not_mutate_values_in_store()` | [UpdatesNeo4jTest.Mutation_of_tracked_values_does_not_mutate_values_in_store](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Update_on_bytes_concurrency_token_original_value_mismatch_throws()` | [UpdatesNeo4jTest.Update_on_bytes_concurrency_token_original_value_mismatch_throws](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Update_on_bytes_concurrency_token_original_value_matches_does_not_throw()` | [UpdatesNeo4jTest.Update_on_bytes_concurrency_token_original_value_matches_does_not_throw](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Remove_on_bytes_concurrency_token_original_value_mismatch_throws()` | [UpdatesNeo4jTest.Remove_on_bytes_concurrency_token_original_value_mismatch_throws](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `UpdatesTestBase.Remove_on_bytes_concurrency_token_original_value_matches_does_not_throw()` | [UpdatesNeo4jTest.Remove_on_bytes_concurrency_token_original_value_matches_does_not_throw](../GraphBridge.SpecificationTests/UpdatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindIncludeQueryTestBase.Include_collection_orderby_take(true)` | [NorthwindRelationshipsNeo4jTest.Include_collection_orderby_take](../GraphBridge.SpecificationTests/NorthwindRelationshipsNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindIncludeQueryTestBase.Include_collection_single_or_default_no_result(true)` | [NorthwindRelationshipsNeo4jTest.Include_collection_single_or_default_no_result](../GraphBridge.SpecificationTests/NorthwindRelationshipsNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindIncludeQueryTestBase.Include_collection_with_filter(true)` | [NorthwindRelationshipsNeo4jTest.Include_collection_with_filter](../GraphBridge.SpecificationTests/NorthwindRelationshipsNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindIncludeQueryTestBase.Include_collection_with_filter_reordered(true)` | [NorthwindRelationshipsNeo4jTest.Include_collection_with_filter_reordered](../GraphBridge.SpecificationTests/NorthwindRelationshipsNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindIncludeQueryTestBase.Include_reference_single_or_default_when_no_result(true)` | [NorthwindRelationshipsNeo4jTest.Include_reference_single_or_default_when_no_result](../GraphBridge.SpecificationTests/NorthwindRelationshipsNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindIncludeQueryTestBase.Include_reference_with_filter(true)` | [NorthwindRelationshipsNeo4jTest.Include_reference_with_filter](../GraphBridge.SpecificationTests/NorthwindRelationshipsNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindIncludeQueryTestBase.Include_reference_with_filter_reordered(true)` | [NorthwindRelationshipsNeo4jTest.Include_reference_with_filter_reordered](../GraphBridge.SpecificationTests/NorthwindRelationshipsNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Min_with_no_arg(true)` | [NorthwindAggregatesNeo4jTest.Min_with_no_arg](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Min_with_arg(true)` | [NorthwindAggregatesNeo4jTest.Min_with_arg](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Min_no_data(true)` | [NorthwindAggregatesNeo4jTest.Min_no_data](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Min_no_data_cast_to_nullable(true)` | [NorthwindAggregatesNeo4jTest.Min_no_data_cast_to_nullable](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Max_with_no_arg(true)` | [NorthwindAggregatesNeo4jTest.Max_with_no_arg](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Max_with_arg(true)` | [NorthwindAggregatesNeo4jTest.Max_with_arg](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Max_no_data(true)` | [NorthwindAggregatesNeo4jTest.Max_no_data](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Max_no_data_cast_to_nullable(true)` | [NorthwindAggregatesNeo4jTest.Max_no_data_cast_to_nullable](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Count_with_no_predicate(true)` | [NorthwindAggregatesNeo4jTest.Count_with_no_predicate](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Count_with_predicate(true)` | [NorthwindAggregatesNeo4jTest.Count_with_predicate](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |
| `NorthwindAggregateOperatorsQueryTestBase.Count_with_order_by(true)` | [NorthwindAggregatesNeo4jTest.Count_with_order_by](../GraphBridge.SpecificationTests/NorthwindAggregatesNeo4jTest.cs) | Pass / local FS-13 |

## Separate adapter checks

| Local check (not an upstream method) | Result |
|---|---|
| `NorthwindFixtureModelTests.Customer_slice_finalizes_all_upstream_scalars_without_other_entities_or_transport` | Pass / local FS-13 |
| `NorthwindRelationshipsFixtureModelTests.Adapted_model_retains_upstream_scalars_and_one_required_inverse_relationship` | Pass / local FS-13 |
| `NorthwindRelationshipsFixtureModelTests.Complete_upstream_data_fits_the_explicit_required_reference_adapter` | Pass / local FS-13 |
| `UpdatesFixtureModelTests.Update_slice_retains_scalar_and_concurrency_contract_without_transport` | Pass / local FS-13 |
| `NorthwindAggregatesFixtureModelTests.Aggregate_slice_retains_original_Order_IDs_and_customer_values_as_independent_nodes` | Pass / local FS-13 |

## Explicit boundaries

The [source-audited candidate table](EF_CONFORMANCE_MATRIX.md#named-upstream-boundaries--ef-10012) tags unselected synchronous setup, Last, nested Include and inheritance cases. The separate aggregate adapter preserves all original OrderID/CustomerID values as independent nodes, excluding navigation/date/employee properties not used by its selected scalar methods; it does not extend relationship-root aggregate support. These exclusions are **not executed skips or passes**. This is a prioritized conformance inventory, not a claim to have classified or passed every EF specification.

Reproduction: use the existing marker-guarded disposable fixture and `dotnet test tests/GraphBridge.SpecificationTests`; the repository workflow provides exact environment and cleanup steps. An unopted run skips live fixtures and is not acceptance evidence. CI separately runs `--filter FullyQualifiedName~FixtureModelTests` before starting the server; those checks execute again in the complete suite and must not be double-counted as distinct tests.
