using System.Linq.Expressions;

namespace FinancialAppApi.Services;

/// <summary>
/// Combines two entity predicates into one lambda over a single parameter.
///
/// EF Core can translate a folded OR/AND chain of closed-over constants, but translating
/// <c>list.Any(item =&gt; ...)</c> over a client-side collection depends on primitive-collection
/// support that the InMemory provider silently evaluates in LINQ-to-Objects instead. Folding here
/// means the shape the tests exercise is the shape the database receives.
/// </summary>
internal static class QueryPredicate
{
    public static Expression<Func<T, bool>> Or<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right) =>
        Combine(left, right, Expression.OrElse);

    public static Expression<Func<T, bool>> And<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    private static Expression<Func<T, bool>> Combine<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right,
        Func<Expression, Expression, BinaryExpression> join)
    {
        var parameter = Expression.Parameter(typeof(T), "t");
        var body = join(
            Rebind(left.Body, left.Parameters[0], parameter),
            Rebind(right.Body, right.Parameters[0], parameter));
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static Expression Rebind(Expression body, ParameterExpression from, ParameterExpression to) =>
        new ParameterRebinder(from, to).Visit(body);

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}
