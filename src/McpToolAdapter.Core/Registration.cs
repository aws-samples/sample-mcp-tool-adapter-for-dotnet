// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace McpToolAdapter
{
    /// <summary>
    /// Declares which existing methods an application exposes.
    /// </summary>
    /// <remarks>
    /// Registration is explicit and lives in the application, not as attributes on the business
    /// logic. That keeps existing assemblies untouched — which matters most when the logic sits in
    /// a shared library used by several applications — and it means each application has exactly
    /// one greppable file listing everything it exposes. That file is what a security reviewer
    /// reads.
    /// </remarks>
    /// <example>
    /// <code>
    /// public sealed class OrderAppTools : ToolRegistry
    /// {
    ///     public override void Configure(IToolBuilder b)
    ///     {
    ///         b.Expose&lt;OrderService&gt;(s =&gt; s.GetOrderById(default(int)))
    ///          .Describes("Fetch a single order by numeric order ID.");
    ///
    ///         b.Expose&lt;OrderService&gt;(s =&gt; s.CancelOrder(default(int), default(string)))
    ///          .Describes("Cancel an unshipped order.")
    ///          .Mutating();
    ///     }
    /// }
    /// </code>
    /// </example>
    public abstract class ToolRegistry
    {
        public abstract void Configure(IToolBuilder builder);
    }

    /// <summary>Collects method registrations.</summary>
    public interface IToolBuilder
    {
        /// <summary>
        /// Exposes an instance method that returns a value. Write the body as a call with dummy
        /// arguments — <c>s => s.GetOrder(default(int))</c>. The call is never executed; only the
        /// <see cref="MethodInfo"/> is read from the expression tree, which is what makes this
        /// refactor-safe. (C# forbids bare method groups in expression trees, hence the dummy args.)
        /// </summary>
        IToolRegistration Expose<T, TResult>(Expression<Func<T, TResult>> call);

        /// <summary>Exposes an instance method that returns void.</summary>
        IToolRegistration Expose<T>(Expression<Action<T>> call);

        /// <summary>Exposes a static method that returns a value.</summary>
        IToolRegistration ExposeStatic<TResult>(Expression<Func<TResult>> call);

        /// <summary>Exposes a static method that returns void.</summary>
        IToolRegistration ExposeStatic(Expression<Action> call);

        /// <summary>
        /// Exposes a method by name. Not refactor-safe, but catalog validation fails loudly at
        /// application start if the name stops resolving, so the failure is never silent. Use when
        /// the expression form is awkward.
        /// </summary>
        IToolRegistration Expose(Type declaringType, string methodName);
    }

    /// <summary>Fluent configuration for a single registration.</summary>
    public interface IToolRegistration
    {
        /// <summary>
        /// Overrides the generated tool name. The catalog's name prefix is still applied.
        /// </summary>
        IToolRegistration Named(string name);

        /// <summary>
        /// Sets the description a model reads when deciding whether to call this tool. Required
        /// unless the catalog is configured otherwise, because an undescribed tool is either
        /// ignored or misused.
        /// </summary>
        IToolRegistration Describes(string description);

        /// <summary>Documents a single parameter.</summary>
        IToolRegistration Describes(string parameterName, string description);

        /// <summary>Marks the method as state-changing. Required for anything that is not a pure read.</summary>
        IToolRegistration Mutating();

        /// <summary>Supplies the target instance, overriding the catalog's instance factory.</summary>
        IToolRegistration Using(Func<object> instanceFactory);

        /// <summary>Caps returned collection items for this tool, overriding the catalog default.</summary>
        IToolRegistration MaxResultItems(int max);
    }

    internal sealed class ToolRegistration : IToolRegistration
    {
        public ToolRegistration(MethodInfo method, Type declaringType)
        {
            Method = method;
            DeclaringType = declaringType;
            ParameterDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public MethodInfo Method { get; }
        public Type DeclaringType { get; }
        public string ExplicitName { get; private set; }
        public string Description { get; private set; }
        public bool IsMutating { get; private set; }
        public Func<object> InstanceFactory { get; private set; }
        public int? MaxItems { get; private set; }
        public Dictionary<string, string> ParameterDescriptions { get; }

        public IToolRegistration Named(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Tool name cannot be empty.", nameof(name));
            ExplicitName = name.Trim();
            return this;
        }

        public IToolRegistration Describes(string description)
        {
            Description = description;
            return this;
        }

        public IToolRegistration Describes(string parameterName, string description)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
                throw new ArgumentException("Parameter name cannot be empty.", nameof(parameterName));
            ParameterDescriptions[parameterName.Trim()] = description;
            return this;
        }

        public IToolRegistration Mutating()
        {
            IsMutating = true;
            return this;
        }

        public IToolRegistration Using(Func<object> instanceFactory)
        {
            InstanceFactory = instanceFactory ?? throw new ArgumentNullException(nameof(instanceFactory));
            return this;
        }

        public IToolRegistration MaxResultItems(int max)
        {
            if (max < 1) throw new ArgumentOutOfRangeException(nameof(max), "Item cap must be at least 1.");
            MaxItems = max;
            return this;
        }
    }

    internal sealed class ToolBuilder : IToolBuilder
    {
        private readonly List<ToolRegistration> _registrations = new List<ToolRegistration>();

        public IReadOnlyList<ToolRegistration> Registrations
        {
            get { return _registrations; }
        }

        public IToolRegistration Expose<T, TResult>(Expression<Func<T, TResult>> call)
        {
            return Add(ExtractMethod(call), typeof(T));
        }

        public IToolRegistration Expose<T>(Expression<Action<T>> call)
        {
            return Add(ExtractMethod(call), typeof(T));
        }

        public IToolRegistration ExposeStatic<TResult>(Expression<Func<TResult>> call)
        {
            var method = ExtractMethod(call);
            return Add(method, method.DeclaringType);
        }

        public IToolRegistration ExposeStatic(Expression<Action> call)
        {
            var method = ExtractMethod(call);
            return Add(method, method.DeclaringType);
        }

        public IToolRegistration Expose(Type declaringType, string methodName)
        {
            if (declaringType == null) throw new ArgumentNullException(nameof(declaringType));
            if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("Method name cannot be empty.", nameof(methodName));

            var candidates = declaringType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
                throw new ToolRegistrationException(
                    "No public method '" + methodName + "' on " + declaringType.FullName + ".");

            if (candidates.Count > 1)
                throw new ToolRegistrationException(
                    declaringType.FullName + "." + methodName + " has " + candidates.Count +
                    " overloads; name-based registration cannot disambiguate. Use the expression " +
                    "form: Expose<" + declaringType.Name + ">(x => x." + methodName + "(...)).");

            return Add(candidates[0], declaringType);
        }

        private IToolRegistration Add(MethodInfo method, Type declaringType)
        {
            var registration = new ToolRegistration(method, declaringType);
            _registrations.Add(registration);
            return registration;
        }

        private static MethodInfo ExtractMethod(LambdaExpression lambda)
        {
            if (lambda == null) throw new ArgumentNullException(nameof(lambda));

            var body = lambda.Body;
            while (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                body = unary.Operand;

            if (body is MethodCallExpression call) return call.Method;

            throw new ToolRegistrationException(
                "Expected a method call expression such as 'x => x.DoThing(default(int))', but got '" +
                lambda.Body + "'. Property and field access cannot be exposed as a tool.");
        }
    }
}
