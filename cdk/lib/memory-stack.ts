// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import * as cdk from 'aws-cdk-lib';
import * as agentcore from 'aws-cdk-lib/aws-bedrockagentcore';
import * as iam from 'aws-cdk-lib/aws-iam';
import { Construct } from 'constructs';

export interface MemoryStackProps extends cdk.StackProps {
  /** Pattern `[a-zA-Z][a-zA-Z0-9_]{0,47}`. Hyphens are rejected. */
  readonly memoryName?: string;

  /**
   * How long raw events are kept, between 7 and 365 days. This is short-term memory only; extracted
   * records outlive it.
   */
  readonly eventRetention?: cdk.Duration;

  /** Principals allowed to write events and read records, usually your agent's execution role. */
  readonly grantees?: iam.IGrantable[];
}

/**
 * Memory for an agent that uses the exposed tools.
 *
 * Read this before wiring it to anything, because the layering matters more than the code.
 *
 * Memory belongs to the agent, not to the application behind the tools. This stack exists so the
 * repository shows where it goes, not because the adapter needs it. Nothing in `McpToolAdapter` reads or
 * writes memory, and it should stay that way: the adapter is a stateless surface over methods that
 * already exist, and an agent has no browser session for a `System.Web` application to attach state to.
 * Putting conversational state inside a fifteen-year-old application would rebuild a managed service in
 * the one place least able to host it.
 *
 * How the two halves fit:
 *
 * Short-term memory is the raw event log. The agent calls `CreateEvent` with a `memoryId`, an `actorId`
 * (who the conversation is with) and a `sessionId` (which conversation), and events expire after
 * `eventRetention`.
 *
 * Long-term memory is extracted from those events by whichever strategies you configure, and read back
 * with `RetrieveMemoryRecords`, which is a semantic query rather than a key lookup. Extraction is a
 * managed job, so records appear shortly after the events, not instantly.
 *
 * Namespaces are how one memory serves many users without leaking between them. `/facts/{actorId}`
 * gives each actor a partition, and a retrieval scoped to that namespace cannot return another user's
 * records. Getting this wrong is a data-leak class of bug, not a tidiness one, so the namespaces here
 * are deliberately actor-scoped.
 *
 * One limitation worth knowing before you plan an audit trail. AgentCore Gateway sends an OpenAPI target
 * no conversation identity: the request your application receives carries no actor id and no session id,
 * only tracing headers. So the application's own audit log cannot be joined to an agent conversation
 * without passing an identifier explicitly. See docs/agentcore-test.md.
 */
export class MemoryStack extends cdk.Stack {
  public readonly memory: agentcore.Memory;

  constructor(scope: Construct, id: string, props: MemoryStackProps = {}) {
    super(scope, id, props);

    this.memory = new agentcore.Memory(this, 'Memory', {
      memoryName: props.memoryName ?? 'orderportal_agent_memory',
      description: 'Conversation memory for agents using the order portal tools',
      expirationDuration: props.eventRetention ?? cdk.Duration.days(30),
      memoryStrategies: [
        // Facts the agent should recall later, such as which customer a user is usually asking about.
        new agentcore.ManagedMemoryStrategy(agentcore.MemoryStrategyType.SEMANTIC, {
          strategyName: 'facts',
          description: 'Facts worth recalling across sessions',
          namespaces: ['/facts/{actorId}'],
        }),

        // Stable preferences, such as a default reporting period. Separated from facts so a retrieval
        // can ask for one without the other.
        new agentcore.ManagedMemoryStrategy(agentcore.MemoryStrategyType.USER_PREFERENCE, {
          strategyName: 'preferences',
          description: 'How this user prefers answers to be shaped',
          namespaces: ['/preferences/{actorId}'],
        }),
      ],
    });

    for (const grantee of props.grantees ?? []) {
      // Least privilege is awkward here: writing an event and retrieving records are different actions
      // on the same resource, and an agent needs both. Granting them together is honest about that.
      grantee.grantPrincipal.addToPrincipalPolicy(new iam.PolicyStatement({
        sid: 'AgentMemoryAccess',
        actions: [
          'bedrock-agentcore:CreateEvent',
          'bedrock-agentcore:ListEvents',
          'bedrock-agentcore:GetEvent',
          'bedrock-agentcore:RetrieveMemoryRecords',
          'bedrock-agentcore:ListMemoryRecords',
          'bedrock-agentcore:GetMemoryRecord',
        ],
        resources: [this.memory.memoryArn, `${this.memory.memoryArn}/*`],
      }));
    }

    new cdk.CfnOutput(this, 'MemoryId', {
      value: this.memory.memoryId,
      description: 'Pass as memoryId to CreateEvent and RetrieveMemoryRecords',
    });
    new cdk.CfnOutput(this, 'MemoryArn', { value: this.memory.memoryArn });
    new cdk.CfnOutput(this, 'FactsNamespace', { value: '/facts/{actorId}' });
    new cdk.CfnOutput(this, 'PreferencesNamespace', { value: '/preferences/{actorId}' });
  }
}
