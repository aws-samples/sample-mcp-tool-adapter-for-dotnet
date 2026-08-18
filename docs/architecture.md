# Architecture

Four views, each deliberately kept to one idea. Detail lives in the prose under each diagram rather
than inside the boxes.

## 1. Where things sit

One gateway, one target per application. Agents connect once.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontFamily":"ui-sans-serif, system-ui, sans-serif","fontSize":"14px","lineColor":"#94a3b8","primaryColor":"#eff6ff","primaryBorderColor":"#1d4ed8","primaryTextColor":"#0f172a"}}}%%
flowchart LR
    Agent(["Agent"])

    subgraph AWS["AWS"]
        direction TB
        GW["AgentCore<br/>Gateway"]
        ID["AgentCore<br/>Identity"]
    end

    subgraph OnPrem["Your network"]
        direction TB
        A1["Application 1"]
        A2["Application 2"]
        A3["Application 3"]
    end

    Agent --> GW
    GW <--> ID
    GW --> A1
    GW --> A2
    GW --> A3

    classDef aws fill:#fffbeb,stroke:#b45309,stroke-width:1.5px,color:#0f172a
    classDef app fill:#f0fdf4,stroke:#15803d,stroke-width:1.5px,color:#0f172a
    classDef ext fill:#f8fafc,stroke:#475569,stroke-width:1.5px,color:#0f172a
    class GW,ID aws
    class A1,A2,A3 app
    class Agent ext
    linkStyle default stroke:#94a3b8,stroke-width:1.5px
```

Two authentication hops, not one. The agent authenticates to the gateway; the gateway separately
authenticates to each application. Identity handles token storage and, in on-behalf-of mode, exchanges
the agent's token for one scoped to the target application.

Tools reach the agent as `orderapp___get_order_by_id`. AgentCore prefixes every operation with its
target name, which is why each application is its own target and why target names stay short.

## 2. Inside one application

A straight line. Everything reusable is in the zero-dependency core; the `System.Web` box is an
adapter.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontFamily":"ui-sans-serif, system-ui, sans-serif","fontSize":"14px","lineColor":"#94a3b8","primaryColor":"#eff6ff","primaryBorderColor":"#1d4ed8","primaryTextColor":"#0f172a"}}}%%
flowchart LR
    Req(["Request"])

    subgraph Web["McpToolAdapter.Web"]
        Mod["Module"]
    end

    subgraph Core["McpToolAdapter.Core"]
        direction LR
        Route["Route"]
        Auth["Authorize"]
        Bind["Bind args"]
        Call["Invoke"]
        Shape["Shape result"]
    end

    Logic["Your business logic"]
    Pass(["Application's<br/>own pages"])

    Req --> Mod
    Mod -->|"not our path"| Pass
    Mod --> Route --> Auth --> Bind --> Call --> Logic
    Logic --> Shape

    classDef ours fill:#eff6ff,stroke:#1d4ed8,stroke-width:1.5px,color:#0f172a
    classDef untouched fill:#f0fdf4,stroke:#15803d,stroke-width:2px,color:#0f172a
    classDef ext fill:#f8fafc,stroke:#475569,stroke-width:1.5px,color:#0f172a
    class Mod,Route,Auth,Bind,Call,Shape ours
    class Logic untouched
    class Req,Pass ext
    linkStyle default stroke:#94a3b8,stroke-width:1.5px
```

The module's first act is a single string comparison; anything that isn't ours passes straight through
untouched. That is the whole cost imposed on the application's normal traffic.

Three things happen at **Invoke** that are worth naming. The method is called through a delegate
compiled once at startup, not through reflection. `PrincipalScope` establishes the end user on
`HttpContext.Current.User` for the duration of the call and restores the previous value afterwards, so
existing `User.IsInRole` checks still govern access. **Shape result** flattens `DataTable`, caps
collection size, and normalises dates to ISO 8601.

## 3. From code change to live tool

Three gates. Each one converts a failure that would otherwise appear late into one that appears now.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontFamily":"ui-sans-serif, system-ui, sans-serif","fontSize":"14px","lineColor":"#94a3b8","primaryColor":"#eff6ff","primaryBorderColor":"#1d4ed8","primaryTextColor":"#0f172a"}}}%%
flowchart LR
    Edit(["Registry edit"])
    Build["Schema<br/>generated"]
    G1{"Catalog<br/>valid?"}
    Serve["OpenAPI<br/>published"]
    G2{"AgentCore<br/>compatible?"}
    G3{"Name fits<br/>64 chars?"}
    Live(["Tool live"])
    Stop["Blocked"]

    Edit --> Build --> G1
    G1 -->|yes| Serve --> G2
    G2 -->|yes| G3
    G3 -->|yes| Live

    G1 -->|no| Stop
    G2 -->|no| Stop
    G3 -->|no| Stop

    classDef ours fill:#eff6ff,stroke:#1d4ed8,stroke-width:1.5px,color:#0f172a
    classDef gate fill:#fefce8,stroke:#a16207,stroke-width:1.5px,color:#0f172a
    classDef bad fill:#fef2f2,stroke:#b91c1c,stroke-width:1.5px,color:#0f172a
    classDef ext fill:#f8fafc,stroke:#475569,stroke-width:1.5px,color:#0f172a
    class Build,Serve ours
    class G1,G2,G3 gate
    class Stop bad
    class Edit,Live ext
    linkStyle default stroke:#94a3b8,stroke-width:1.5px
```

**Gate 1** runs at application startup and reports every problem at once: unbindable parameter,
duplicate name, missing description, `out` parameter, unconstructable target.

**Gate 2** checks the emitted document against AgentCore's documented constraints: no `oneOf`, no
specification-level security schemes, a real `servers` URL, an `operationId` on every operation.

**Gate 3** is the one that earns its place. AgentCore documents that breaching a model's tool-name
limit fails in the data plane, so the target creates cleanly and calls fail later. Checking it at
registration turns a mystery runtime failure into a refused deployment.

Registration runs either from CDK or from the reconciler script; both apply gates 2 and 3. CDK is the
default for anything CloudFormation deploys, private endpoints included; the reconciler is for
applications whose lifecycle CloudFormation does not own.

## 4. Acting as the end user

How an agent calls as a named person rather than a service account, so existing role checks still
decide.

```mermaid
%%{init: {"theme":"base","themeVariables":{"fontFamily":"ui-sans-serif, system-ui, sans-serif","fontSize":"14px","actorBkg":"#f8fafc","actorBorder":"#475569","noteBkg":"#fefce8","noteBorderColor":"#a16207"}}}%%
sequenceDiagram
    autonumber
    participant Agent
    participant GW as Gateway
    participant ID as Identity
    participant App as Application
    participant Logic as Business logic

    Agent->>GW: tools/call with user token
    GW->>ID: exchange token
    ID-->>GW: token for this app, same user
    GW->>App: POST with bearer token
    App->>App: validate signature, audience, expiry
    App->>Logic: invoke as that user
    Logic-->>App: result or access denied
    App-->>GW: JSON envelope
    GW-->>Agent: tool result

    note over App,Logic: The gateway authenticates.<br/>The application still authorizes.
```

The application validates the token itself rather than trusting the gateway's word for it: signature
against the provider's published keys, audience, issuer and expiry. A key-retrieval failure returns
503, not 401, so an outage is never mistaken for "unauthorized".

Every call is audited with caller, operation and outcome. Argument *names* are recorded; values are
not, because they routinely contain the data you are protecting.

What does not carry over is `Session`. Code reading `Session["CurrentUser"]` is bound to a browser
session an agent does not have, and there is no honest way to synthesise one.

## Caveat

The `System.Web` adapter in view 2 compiles but has not run inside a live IIS application. Module
registration, virtual-directory path resolution and request-body reading are the unverified parts. See
"Sources checked, and what is still assumed" in the root README.
