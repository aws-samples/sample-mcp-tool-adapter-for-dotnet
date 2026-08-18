// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Threading;

namespace McpToolAdapter.Tests
{
    // Stand-ins for the kind of code this SDK is pointed at: plain classes, no framework
    // attributes, some awkward signatures.

    public enum OrderStatus
    {
        Pending,
        Shipped,
        Cancelled
    }

    public sealed class Order
    {
        public int Id { get; set; }
        public string CustomerEmail { get; set; }
        public decimal Total { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime PlacedUtc { get; set; }
    }

    public sealed class OrderQuery
    {
        public string CustomerEmail { get; set; }
        public OrderStatus? Status { get; set; }
        public int Take { get; set; }
        public IList<string> Tags { get; set; }
    }

    public sealed class RecursiveNode
    {
        public string Name { get; set; }
        public RecursiveNode Child { get; set; }
    }

    public sealed class LevelOne
    {
        public LevelTwo Two { get; set; }
    }

    public sealed class LevelTwo
    {
        public LevelThree Three { get; set; }
    }

    public sealed class LevelThree
    {
        public string Value { get; set; }
    }

    public sealed class OrderService
    {
        public static int ConstructionCount;

        public OrderService()
        {
            Interlocked.Increment(ref ConstructionCount);
        }

        public Order GetOrderById(int id)
        {
            return new Order
            {
                Id = id,
                CustomerEmail = "someone@example.com",
                Total = 12.50m,
                Status = OrderStatus.Pending,
                PlacedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            };
        }

        public IList<Order> Search(OrderQuery query)
        {
            var results = new List<Order>();
            var take = query == null ? 0 : query.Take;
            for (var i = 0; i < take; i++) results.Add(GetOrderById(i));
            return results;
        }

        public void CancelOrder(int id, string reason)
        {
            LastCancelled = id + ":" + reason;
        }

        public string LastCancelled { get; private set; }

        public string Describe(int id, string note = "none")
        {
            return id + "/" + note;
        }

        public IEnumerable<int> Sequence(int count)
        {
            for (var i = 0; i < count; i++) yield return i;
        }

        public int SumWithToken(int a, int b, CancellationToken cancellationToken)
        {
            return a + b;
        }

        public string Boom()
        {
            throw new InvalidOperationException("connection string Server=secret;Password=hunter2 failed");
        }

        public bool TrySomething(int id, out string result)
        {
            result = id.ToString();
            return true;
        }

        public T Echo<T>(T value)
        {
            return value;
        }

        public object Loose(object anything)
        {
            return anything;
        }
    }

    public static class Maths
    {
        public static int Add(int a, int b)
        {
            return a + b;
        }
    }

    public sealed class NeedsConstructorArgs
    {
        public NeedsConstructorArgs(string connectionString)
        {
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public string Read()
        {
            return ConnectionString;
        }
    }

    /// <summary>Ad-hoc registry for tests. Has a parameterless constructor so assembly scanning can load it.</summary>
    public sealed class LambdaRegistry : ToolRegistry
    {
        private readonly Action<IToolBuilder> _configure;

        public LambdaRegistry()
        {
        }

        public LambdaRegistry(Action<IToolBuilder> configure)
        {
            _configure = configure;
        }

        public override void Configure(IToolBuilder builder)
        {
            if (_configure != null) _configure(builder);
        }
    }

    /// <summary>Present so assembly-scanning discovery has something concrete to find.</summary>
    public sealed class DiscoverableTools : ToolRegistry
    {
        public override void Configure(IToolBuilder builder)
        {
            builder.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Named("discovered_get_order")
                .Describes("Discovered by assembly scanning.");
        }
    }
}
