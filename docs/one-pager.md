# McpToolAdapter — MCP for traditional .NET platforms

**Makes functions inside existing ASP.NET Framework applications callable by AI agents, through one
central AWS Bedrock AgentCore Gateway, without rewriting the applications.**

## The problem

Most institutional logic in a mature .NET estate lives in ASP.NET Framework applications — WebForms,
MVC 5 — that have run correctly for years. Agents cannot reach it. The default path is to rewrite that
logic as a modern service, which means re-deriving and re-validating rules that were already correct,
one application at a time. It is slow, and it introduces risk into processes that had none.

The official MCP C# SDK does not close this gap: its HTTP hosting ships only .NET 8/9/10 assets, so it
cannot run where these applications live.

## How it works

1. **Declare.** One new file per application lists which existing methods to expose. No existing code
   is edited.
2. **Describe.** At startup the application reflects over those methods, generates JSON Schema for
   their inputs and outputs, and serves an OpenAPI document at `/_mcp/openapi.json`. Because it is
   generated from the code, it cannot drift from it.
3. **Register.** AgentCore Gateway consumes that document as an OpenAPI target. Each operation becomes
   an MCP tool. Registration is automated and idempotent.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontFamily":"ui-sans-serif, system-ui, sans-serif","fontSize":"14px","lineColor":"#94a3b8"}}}%%
flowchart LR
    A(["AI agents"]) --> G["AgentCore Gateway<br/>one endpoint · identity · audit"]
    G --> N["Adapter<br/><b>new, small</b>"]
    N --> L["Existing business logic<br/><b>unchanged</b>"]
    G -.-> M(["Further applications<br/>configuration only"])

    classDef gw fill:#fffbeb,stroke:#b45309,stroke-width:2px,color:#0f172a
    classDef new fill:#eff6ff,stroke:#1d4ed8,stroke-width:1.5px,color:#0f172a
    classDef keep fill:#f0fdf4,stroke:#15803d,stroke-width:2px,color:#0f172a
    classDef out fill:#f8fafc,stroke:#475569,stroke-width:1.5px,color:#0f172a
    class G gw
    class N new
    class L keep
    class A,M out
    linkStyle default stroke:#94a3b8,stroke-width:1.5px
```

## What it accelerates

| | Rewrite approach | With McpToolAdapter |
|---|---|---|
| **First application** | Reimplement and revalidate business rules | One file plus four configuration entries |
| **Each further application** | Another project | A configuration entry |
| **Keeping tools current** | Manual, drifts from code | Generated from code; registration reconciles automatically |
| **Integrations to secure** | One per application | One gateway for the estate |
| **Risk to business rules** | Re-derived, so divergence is possible | Untouched, so behaviour is identical |

The one variable: where logic sits in page code-behind rather than in service classes, it must first be
lifted into a callable method. That ratio drives effort per application and is assessed before
estimating.

## AgentCore integration

- **OpenAPI target per application.** Separate targets mean access can be granted to one application's
  tools without the others, and one application's change cannot invalidate another.
- **Inbound identity at the gateway.** JWT validation — audience, client and scope — configured once.
- **Outbound credential injected by the gateway.** API key for service-account access, or OAuth token
  exchange to carry the end user's identity through to the application.
- **Compatibility validated before deployment.** AgentCore's constraints — including the 64-character
  tool-name limit, which otherwise fails at invocation rather than at registration — are checked at
  application startup and again at registration.
- **Infrastructure as code.** The gateway, credentials and tool targets all deploy as CloudFormation,
  including targets reached privately over VPC Lattice. For applications deployed by something other than
  CloudFormation, a reconciler registers their targets idempotently from a manifest instead.

## Security posture

Inactive until explicitly enabled per application, and refuses all traffic without a credential.
Read-only unless an operation is deliberately approved for making changes. The gateway authenticates;
each application still authorizes using the access rules it already enforces. Every call is audited
with caller and operation; argument values are excluded by design.

## Status

Core platform complete, 186 automated tests, plus AgentCore compatibility checks and registration
automation. Remaining unknown: validating the hosting layer inside a live IIS application — the purpose
of the first pilot, and the main technical risk.
