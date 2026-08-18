// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using McpToolAdapter.Invocation;
using McpToolAdapter.Schema;
using McpToolAdapter.Shaping;

namespace McpToolAdapter
{
    /// <summary>Catalog-wide configuration.</summary>
    public sealed class ToolCatalogOptions
    {
        private static readonly Regex ValidName = new Regex("^[a-z0-9][a-z0-9_]*$", RegexOptions.Compiled);

        /// <summary>
        /// Prefix applied to every tool name, identifying the owning application — for example
        /// <c>orderapp</c> yields <c>orderapp_get_order</c>. Set this. Tool names share a single
        /// namespace at the client, and unprefixed names from separate applications collide.
        /// </summary>
        public string NamePrefix { get; set; }

        /// <summary>
        /// Resolves an instance for instance methods. Defaults to
        /// <see cref="Activator.CreateInstance(Type)"/>; point it at an existing container when
        /// the application has one.
        /// </summary>
        public Func<Type, object> InstanceFactory { get; set; }

        /// <summary>How deep to expand nested object graphs into schema. Default 5.</summary>
        public int MaxSchemaDepth { get; set; } = 5;

        /// <summary>
        /// Require a description on every tool. Default true, and worth keeping: an undescribed
        /// tool is either never selected or selected for the wrong reason.
        /// </summary>
        public bool RequireDescriptions { get; set; } = true;

        /// <summary>
        /// Default cap on returned collection items. Default 200. Null means unlimited, which
        /// risks exhausting the calling model's context window.
        /// </summary>
        public int? DefaultMaxResultItems { get; set; } = 200;

        /// <summary>Result shapers, tried in order. Pre-seeded with <see cref="DataTableShaper"/>.</summary>
        public IList<IResultShaper> Shapers { get; } = new List<IResultShaper> { new DataTableShaper() };

        /// <summary>
        /// Longest permitted tool name. Defaults to 64, the documented maximum for Bedrock's
        /// <c>ToolSpecification.name</c> (pattern <c>[a-zA-Z0-9_-]+</c>).
        /// </summary>
        /// <remarks>
        /// Enforced here so an over-long name fails at application start. A gateway typically adds its
        /// own prefix on top of this — AgentCore prepends <c>targetName___</c> — so the real budget is
        /// smaller than this number. <see cref="Gateway.AgentCoreCompatibility"/> does that arithmetic
        /// with a known target name.
        /// </remarks>
        public int MaxToolNameLength { get; set; } = 64;

        internal static bool IsValidName(string name)
        {
            return !string.IsNullOrEmpty(name) && ValidName.IsMatch(name);
        }
    }

    /// <summary>
    /// The validated set of tools an application exposes.
    /// </summary>
    /// <remarks>
    /// Built once at application start. Every problem — an unbindable parameter, a duplicate name,
    /// a missing description, an unconstructable target — is a build-time failure listing all
    /// offenders, never a tool that silently fails to appear. A tool missing from the catalog with
    /// no explanation is far more expensive to diagnose than an application that refuses to start.
    /// </remarks>
    public sealed class ToolCatalog
    {
        private readonly Dictionary<string, ToolDescriptor> _byName;

        private ToolCatalog(IReadOnlyList<ToolDescriptor> tools, ToolCatalogOptions options)
        {
            Tools = tools;
            Options = options;
            ShapingPipeline = new ResultShapingPipeline(options.Shapers);
            _byName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ToolDescriptor> Tools { get; }
        public ToolCatalogOptions Options { get; }
        internal ResultShapingPipeline ShapingPipeline { get; }

        public bool TryGet(string name, out ToolDescriptor tool)
        {
            if (string.IsNullOrEmpty(name))
            {
                tool = null;
                return false;
            }
            return _byName.TryGetValue(name, out tool);
        }

        /// <summary>Builds a catalog from explicit registries.</summary>
        /// <exception cref="ToolRegistrationException">One or more registrations are invalid.</exception>
        public static ToolCatalog Build(ToolCatalogOptions options, params ToolRegistry[] registries)
        {
            return Build(options, (IEnumerable<ToolRegistry>)registries);
        }

        /// <summary>Builds a catalog from explicit registries.</summary>
        public static ToolCatalog Build(ToolCatalogOptions options, IEnumerable<ToolRegistry> registries)
        {
            options = options ?? new ToolCatalogOptions();
            var registryList = (registries ?? Enumerable.Empty<ToolRegistry>()).Where(r => r != null).ToList();

            var builder = new ToolBuilder();
            var errors = new List<string>();

            foreach (var registry in registryList)
            {
                try
                {
                    registry.Configure(builder);
                }
                catch (ToolRegistrationException ex)
                {
                    errors.AddRange(ex.Errors.Select(e => registry.GetType().Name + ": " + e));
                }
            }

            var descriptors = Compile(builder.Registrations, options, errors);

            if (errors.Count > 0) throw new ToolRegistrationException(errors);
            return new ToolCatalog(descriptors, options);
        }

        /// <summary>
        /// Discovers every concrete <see cref="ToolRegistry"/> in the given assemblies and builds
        /// from them. This is the path the hosts use, so adding a registry class is the only step
        /// needed to expose new tools.
        /// </summary>
        public static ToolCatalog BuildFromAssemblies(ToolCatalogOptions options, params Assembly[] assemblies)
        {
            var registries = new List<ToolRegistry>();
            var errors = new List<string>();

            foreach (var assembly in (assemblies ?? new Assembly[0]).Where(a => a != null))
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in types)
                {
                    if (!typeof(ToolRegistry).IsAssignableFrom(type)) continue;
                    if (type.IsAbstract || type.IsInterface) continue;

                    if (type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        errors.Add(type.FullName + " derives from ToolRegistry but has no public " +
                                   "parameterless constructor, so it cannot be discovered.");
                        continue;
                    }

                    registries.Add((ToolRegistry)Activator.CreateInstance(type));
                }
            }

            if (errors.Count > 0) throw new ToolRegistrationException(errors);
            return Build(options, registries);
        }

        private static List<ToolDescriptor> Compile(
            IReadOnlyList<ToolRegistration> registrations,
            ToolCatalogOptions options,
            List<string> errors)
        {
            var generator = new JsonSchemaGenerator(options.MaxSchemaDepth);
            var descriptors = new List<ToolDescriptor>();
            var seen = new Dictionary<string, ToolRegistration>(StringComparer.OrdinalIgnoreCase);

            foreach (var registration in registrations)
            {
                var origin = Origin(registration);
                var name = ComposeName(options.NamePrefix, registration);

                if (!ToolCatalogOptions.IsValidName(name))
                {
                    errors.Add(origin + ": '" + name + "' is not a valid tool name. Use lowercase " +
                               "letters, digits and underscores, starting with a letter or digit.");
                    continue;
                }

                if (name.Length > options.MaxToolNameLength)
                {
                    errors.Add(origin + ": tool name '" + name + "' is " + name.Length +
                               " characters, over the " + options.MaxToolNameLength + "-character limit " +
                               "that model tool specifications impose. Shorten it with .Named(\"...\") or " +
                               "use a shorter name prefix. Note a gateway usually prepends its own " +
                               "prefix, so the usable budget is smaller still.");
                    continue;
                }

                ToolRegistration clash;
                if (seen.TryGetValue(name, out clash))
                {
                    errors.Add("duplicate tool name '" + name + "' declared by " + origin +
                               " and " + Origin(clash) + ". Disambiguate with .Named(\"...\").");
                    continue;
                }
                seen[name] = registration;

                if (options.RequireDescriptions && string.IsNullOrWhiteSpace(registration.Description))
                {
                    errors.Add(origin + ": no description. Add .Describes(\"...\") — it is what a " +
                               "model reads to decide whether to call this tool.");
                    continue;
                }

                if (registration.Method.IsGenericMethodDefinition)
                {
                    errors.Add(origin + ": generic methods cannot be exposed; the type arguments " +
                               "are not knowable from a JSON call.");
                    continue;
                }

                var methodParameters = registration.Method.GetParameters();
                var badByRef = methodParameters.Where(p => p.ParameterType.IsByRef).Select(p => p.Name).ToList();
                if (badByRef.Count > 0)
                {
                    errors.Add(origin + ": out/ref parameter(s) " + string.Join(", ", badByRef.ToArray()) +
                               " cannot be expressed over JSON. Wrap the method in a facade that returns a result object.");
                    continue;
                }

                var parameters = new List<ToolParameter>();
                var parameterFailure = false;

                foreach (var parameter in methodParameters)
                {
                    if (parameter.ParameterType == typeof(CancellationToken)) continue;

                    JsonObject schema;
                    try
                    {
                        schema = generator.ForInput(parameter.ParameterType);
                    }
                    catch (SchemaGenerationException ex)
                    {
                        errors.Add(origin + ": parameter '" + parameter.Name + "' — " + ex.Message);
                        parameterFailure = true;
                        continue;
                    }

                    string parameterDescription;
                    if (registration.ParameterDescriptions.TryGetValue(parameter.Name, out parameterDescription) &&
                        !string.IsNullOrWhiteSpace(parameterDescription))
                    {
                        schema["description"] = parameterDescription;
                    }

                    var isOptional = parameter.IsOptional ||
                                     Nullable.GetUnderlyingType(parameter.ParameterType) != null;

                    parameters.Add(new ToolParameter(
                        parameter.Name,
                        parameter.ParameterType,
                        parameter.Position,
                        isOptional,
                        parameter.IsOptional ? parameter.DefaultValue : null,
                        parameterDescription,
                        schema));
                }

                if (parameterFailure) continue;

                var unknownDescribed = registration.ParameterDescriptions.Keys
                    .Where(k => !parameters.Any(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (unknownDescribed.Count > 0)
                {
                    errors.Add(origin + ": .Describes() names parameter(s) that do not exist: " +
                               string.Join(", ", unknownDescribed.ToArray()) + ".");
                    continue;
                }

                var instanceFactory = ResolveInstanceFactory(registration, options, origin, errors);
                if (!registration.Method.IsStatic && instanceFactory == null) continue;

                JsonObject resultSchema;
                try
                {
                    resultSchema = registration.Method.ReturnType == typeof(void)
                        ? new JsonObject { ["type"] = "null" }
                        : generator.ForOutput(registration.Method.ReturnType);
                }
                catch (SchemaGenerationException ex)
                {
                    errors.Add(origin + ": return type — " + ex.Message);
                    continue;
                }

                Func<object, object[], object> invoker;
                try
                {
                    invoker = CompiledInvoker.Compile(registration.Method);
                }
                catch (Exception ex)
                {
                    errors.Add(origin + ": could not compile an invoker — " + ex.Message);
                    continue;
                }

                descriptors.Add(new ToolDescriptor(
                    name,
                    registration.Description,
                    registration.Method,
                    registration.IsMutating,
                    parameters,
                    BuildInputSchema(parameters),
                    resultSchema,
                    registration.MaxItems ?? options.DefaultMaxResultItems,
                    instanceFactory,
                    invoker));
            }

            return descriptors;
        }

        private static Func<object> ResolveInstanceFactory(
            ToolRegistration registration,
            ToolCatalogOptions options,
            string origin,
            List<string> errors)
        {
            if (registration.Method.IsStatic) return null;
            if (registration.InstanceFactory != null) return registration.InstanceFactory;

            var targetType = registration.DeclaringType ?? registration.Method.DeclaringType;

            if (options.InstanceFactory != null)
            {
                var captured = options.InstanceFactory;
                return () => captured(targetType);
            }

            if (targetType == null || targetType.GetConstructor(Type.EmptyTypes) == null)
            {
                errors.Add(origin + ": " + (targetType == null ? "target type" : targetType.Name) +
                           " has no public parameterless constructor. Supply one with " +
                           ".Using(() => ...) or set ToolCatalogOptions.InstanceFactory.");
                return null;
            }

            return () => Activator.CreateInstance(targetType);
        }

        private static JsonObject BuildInputSchema(IReadOnlyList<ToolParameter> parameters)
        {
            var properties = new JsonObject();
            var required = new List<object>();

            foreach (var parameter in parameters)
            {
                properties[parameter.Name] = parameter.Schema;
                if (!parameter.IsOptional) required.Add(parameter.Name);
            }

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count > 0) schema["required"] = required.ToArray();
            schema["additionalProperties"] = false;
            return schema;
        }

        private static string ComposeName(string prefix, ToolRegistration registration)
        {
            var core = registration.ExplicitName ?? ToSnakeCase(registration.Method.Name);
            if (string.IsNullOrWhiteSpace(prefix)) return core;
            return ToSnakeCase(prefix.Trim()) + "_" + core;
        }

        private static string Origin(ToolRegistration registration)
        {
            var declaring = registration.DeclaringType ?? registration.Method.DeclaringType;
            return (declaring == null ? "<unknown>" : declaring.Name) + "." + registration.Method.Name;
        }

        internal static string ToSnakeCase(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var builder = new StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];

                if (current == '_' || current == '-' || current == ' ' || current == '.')
                {
                    if (builder.Length > 0 && builder[builder.Length - 1] != '_') builder.Append('_');
                    continue;
                }

                if (char.IsUpper(current))
                {
                    var previous = i > 0 ? value[i - 1] : '\0';
                    var next = i + 1 < value.Length ? value[i + 1] : '\0';

                    // Break before a new word, and at the tail of an acronym run (HTTPStatus -> http_status).
                    var startsWord = previous != '\0' && previous != '_' &&
                                     (!char.IsUpper(previous) || (char.IsUpper(previous) && char.IsLower(next)));

                    if (startsWord && builder.Length > 0 && builder[builder.Length - 1] != '_')
                        builder.Append('_');

                    builder.Append(char.ToLowerInvariant(current));
                    continue;
                }

                builder.Append(char.ToLowerInvariant(current));
            }

            return builder.ToString().Trim('_');
        }
    }
}
