using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed partial class Neo4jQueryableMethodTranslatingExpressionVisitor
{
    protected override ShapedQueryExpression? TranslateAll(ShapedQueryExpression source, LambdaExpression predicate) => throw Unsupported("TranslateAll");

    protected override ShapedQueryExpression? TranslateAverage(ShapedQueryExpression source, LambdaExpression? selector, Type resultType) => throw Unsupported("TranslateAverage");

    protected override ShapedQueryExpression? TranslateCast(ShapedQueryExpression source, Type castType) => throw Unsupported("TranslateCast");

    protected override ShapedQueryExpression? TranslateConcat(ShapedQueryExpression source1, ShapedQueryExpression source2) => throw Unsupported("TranslateConcat");

    protected override ShapedQueryExpression? TranslateDefaultIfEmpty(ShapedQueryExpression source, Expression? defaultValue) => throw Unsupported("TranslateDefaultIfEmpty");

    protected override ShapedQueryExpression? TranslateDistinct(ShapedQueryExpression source) => throw Unsupported("TranslateDistinct");

    protected override ShapedQueryExpression? TranslateElementAtOrDefault(
        ShapedQueryExpression source,
        Expression index,
        bool returnDefault) => throw Unsupported("TranslateElementAtOrDefault");

    protected override ShapedQueryExpression? TranslateExcept(ShapedQueryExpression source1, ShapedQueryExpression source2) => throw Unsupported("TranslateExcept");

    protected override ShapedQueryExpression? TranslateGroupBy(
        ShapedQueryExpression source,
        LambdaExpression keySelector,
        LambdaExpression? elementSelector,
        LambdaExpression? resultSelector) => throw Unsupported("TranslateGroupBy");

    protected override ShapedQueryExpression? TranslateGroupJoin(
        ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector,
        LambdaExpression resultSelector) => throw Unsupported("TranslateGroupJoin");

    protected override ShapedQueryExpression? TranslateIntersect(ShapedQueryExpression source1, ShapedQueryExpression source2) => throw Unsupported("TranslateIntersect");

    protected override ShapedQueryExpression? TranslateJoin(
        ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector,
        LambdaExpression resultSelector) => throw Unsupported("TranslateJoin");

    protected override ShapedQueryExpression? TranslateLeftJoin(
        ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector,
        LambdaExpression resultSelector) => throw Unsupported("TranslateLeftJoin");

    protected override ShapedQueryExpression? TranslateRightJoin(
        ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector,
        LambdaExpression resultSelector) => throw Unsupported("TranslateRightJoin");

    protected override ShapedQueryExpression? TranslateLastOrDefault(
        ShapedQueryExpression source,
        LambdaExpression? predicate,
        Type returnType,
        bool returnDefault) => throw Unsupported("TranslateLastOrDefault");

    protected override ShapedQueryExpression? TranslateMax(ShapedQueryExpression source, LambdaExpression? selector, Type resultType)
        => TranslateIntegerExtremum(source, selector, resultType, Cypher.CypherAggregate.Max);

    protected override ShapedQueryExpression? TranslateMin(ShapedQueryExpression source, LambdaExpression? selector, Type resultType)
        => TranslateIntegerExtremum(source, selector, resultType, Cypher.CypherAggregate.Min);

    protected override ShapedQueryExpression? TranslateOfType(ShapedQueryExpression source, Type resultType) => throw Unsupported("TranslateOfType");

    protected override ShapedQueryExpression? TranslateReverse(ShapedQueryExpression source) => throw Unsupported("TranslateReverse");

    protected override ShapedQueryExpression? TranslateSelectMany(
        ShapedQueryExpression source,
        LambdaExpression collectionSelector,
        LambdaExpression resultSelector) => throw Unsupported("TranslateSelectMany");

    protected override ShapedQueryExpression? TranslateSelectMany(ShapedQueryExpression source, LambdaExpression selector) => throw Unsupported("TranslateSelectMany");

    protected override ShapedQueryExpression? TranslateSkipWhile(ShapedQueryExpression source, LambdaExpression predicate) => throw Unsupported("TranslateSkipWhile");

    protected override ShapedQueryExpression? TranslateSum(ShapedQueryExpression source, LambdaExpression? selector, Type resultType) => throw Unsupported("TranslateSum");

    protected override ShapedQueryExpression? TranslateTakeWhile(ShapedQueryExpression source, LambdaExpression predicate) => throw Unsupported("TranslateTakeWhile");

    protected override ShapedQueryExpression? TranslateUnion(ShapedQueryExpression source1, ShapedQueryExpression source2) => throw Unsupported("TranslateUnion");
}
