// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Linq.Expressions;
using System.Reflection;

namespace McpToolAdapter.Invocation
{
    /// <summary>
    /// Compiles a <see cref="MethodInfo"/> into a delegate once, at catalog build time.
    /// </summary>
    /// <remarks>
    /// Reflection is for discovery. <see cref="MethodBase.Invoke(object, object[])"/> on every
    /// call would add avoidable overhead and bury real exceptions inside
    /// <see cref="TargetInvocationException"/>; a compiled delegate avoids both.
    /// </remarks>
    internal static class CompiledInvoker
    {
        public static Func<object, object[], object> Compile(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            var instanceParameter = Expression.Parameter(typeof(object), "instance");
            var argsParameter = Expression.Parameter(typeof(object[]), "args");

            var parameters = method.GetParameters();
            var arguments = new Expression[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                arguments[i] = Expression.Convert(
                    Expression.ArrayIndex(argsParameter, Expression.Constant(i)),
                    parameters[i].ParameterType);
            }

            Expression call = method.IsStatic
                ? Expression.Call(method, arguments)
                : Expression.Call(
                    Expression.Convert(instanceParameter, method.DeclaringType ?? typeof(object)),
                    method,
                    arguments);

            Expression body = method.ReturnType == typeof(void)
                ? Expression.Block(call, Expression.Constant(null, typeof(object)))
                : (Expression)Expression.Convert(call, typeof(object));

            return Expression.Lambda<Func<object, object[], object>>(body, instanceParameter, argsParameter)
                .Compile();
        }
    }
}
