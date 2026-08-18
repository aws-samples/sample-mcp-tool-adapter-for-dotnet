// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;

namespace McpToolAdapter
{
    /// <summary>
    /// Thrown when a tool catalog cannot be built. Carries <em>every</em> problem found, not just
    /// the first, so a misconfigured application can be fixed in one pass rather than one
    /// deploy-per-error.
    /// </summary>
    public sealed class ToolRegistrationException : Exception
    {
        public ToolRegistrationException(IEnumerable<string> errors)
            : base(BuildMessage(errors))
        {
            Errors = (errors ?? Enumerable.Empty<string>()).ToList();
        }

        public ToolRegistrationException(string error)
            : this(new[] { error })
        {
        }

        public IReadOnlyList<string> Errors { get; }

        private static string BuildMessage(IEnumerable<string> errors)
        {
            var list = (errors ?? Enumerable.Empty<string>()).ToList();
            if (list.Count == 0) return "Tool registration failed.";
            return "Tool registration failed with " + list.Count + " error(s):" + Environment.NewLine +
                   string.Join(Environment.NewLine, list.Select(e => "  - " + e));
        }
    }

    /// <summary>
    /// Thrown when a CLR type cannot be represented as JSON Schema.
    /// </summary>
    public sealed class SchemaGenerationException : Exception
    {
        public SchemaGenerationException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Thrown when incoming arguments cannot be bound to a method's parameters. Carries every
    /// binding failure so the caller sees all of them at once.
    /// </summary>
    public sealed class ArgumentBindingException : Exception
    {
        public ArgumentBindingException(IEnumerable<string> errors)
            : base(string.Join("; ", (errors ?? Enumerable.Empty<string>()).ToArray()))
        {
            Errors = (errors ?? Enumerable.Empty<string>()).ToList();
        }

        public IReadOnlyList<string> Errors { get; }
    }
}
