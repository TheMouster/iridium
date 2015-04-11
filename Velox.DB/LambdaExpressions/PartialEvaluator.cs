using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Velox.DB.Core;

namespace Velox.DB
{
    internal static class PartialEvaluator
    {
        public static Expression Eval(Expression expression, Func<Expression, bool> fnCanBeEvaluated)
        {
            return SubtreeEvaluator.Eval(Nominator.Nominate(fnCanBeEvaluated, expression), expression);
        }

        public static Expression Eval(Expression expression)
        {
            return Eval(expression, CanBeEvaluatedLocally);
        }

        private static bool CanBeEvaluatedLocally(Expression expression)
        {
            return expression.NodeType != ExpressionType.Parameter;
        }

        private class SubtreeEvaluator : ExpressionVisitor
        {
            readonly HashSet<Expression> _candidates;

            private SubtreeEvaluator(HashSet<Expression> candidates)
            {
                _candidates = candidates;
            }

            internal static Expression Eval(HashSet<Expression> candidates, Expression expression)
            {
                return new SubtreeEvaluator(candidates).Visit(expression);
            }

            public override Expression Visit(Expression expression)
            {
                if (expression == null)
                    return null;

                if (_candidates.Contains(expression))
                    return Evaluate(expression);

                return base.Visit(expression);
            }

            private static Expression Evaluate(Expression expression)
            {
                Type type = expression.Type;

                if (expression.NodeType == ExpressionType.Convert)
                {
                    // check for unnecessary convert & strip them
                    var u = (UnaryExpression)expression;

                    if (u.Operand.Type.Inspector().RealType == type.Inspector().RealType)
                    {
                        expression = ((UnaryExpression)expression).Operand;
                    }
                }

                if (expression.NodeType == ExpressionType.Constant)
                {
                    if (expression.Type == type)
                        return expression;
                    
                    if (expression.Type.Inspector().RealType == type.Inspector().RealType)
                    {
                        return Expression.Constant(((ConstantExpression)expression).Value, type);
                    }
                }

                var memberExpression = expression as MemberExpression;

                if (memberExpression != null)
                {
                    var constantExpression = memberExpression.Expression as ConstantExpression;

                    if (constantExpression != null)
                        return Expression.Constant(memberExpression.Member.Inspector().GetValue(constantExpression.Value), type);
                }

                if (type.Inspector().IsValueType)
                {
                    expression = Expression.Convert(expression, typeof(object));
                }

                return Expression.Constant(Expression.Lambda<Func<object>>(expression).Compile().Invoke(), type);
            }
        }

        private class Nominator : ExpressionVisitor
        {
            private readonly Func<Expression, bool> _fnCanBeEvaluated;
            private readonly HashSet<Expression> _candidates;
            private bool _cannotBeEvaluated;

            private Nominator(Func<Expression, bool> fnCanBeEvaluated)
            {
                _candidates = new HashSet<Expression>();

                _fnCanBeEvaluated = fnCanBeEvaluated;
            }

            internal static HashSet<Expression> Nominate(Func<Expression, bool> fnCanBeEvaluated, Expression expression)
            {
                var nominator = new Nominator(fnCanBeEvaluated);
                
                nominator.Visit(expression);

                return nominator._candidates;
            }

            protected override Expression VisitConstant(ConstantExpression c)
            {
                return base.VisitConstant(c);
            }

            public override Expression Visit(Expression expression)
            {
                if (expression == null) 
                    return null;

                bool saveCannotBeEvaluated = _cannotBeEvaluated;

                _cannotBeEvaluated = false;

                base.Visit(expression);

                if (!_cannotBeEvaluated)
                {
                    if (_fnCanBeEvaluated(expression))
                    {
                        _candidates.Add(expression);
                    }
                    else
                    {
                        _cannotBeEvaluated = true;
                    }
                }

                _cannotBeEvaluated |= saveCannotBeEvaluated;

                return expression;
            }
        }
    }
}