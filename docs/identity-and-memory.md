# Caller identity and agent memory

Two questions come up immediately once the tools work: can a call carry the end user's identity into the
existing authorization checks, and can the agent remember anything between calls.

Both are answered by AgentCore services rather than by this adapter, and both are deployed by the CDK in
this repository. This page records what has been run and what has not.

## Identity: what the deployed sample proves

Bearer-token mode has been run end to end in us-east-1. Deploy it with:

```bash
cd cdk
npx cdk deploy McpToolAdapterIdentity McpToolAdapterPrivateApp McpToolAdapterGateway -c authMode=jwt
```

`authMode` defaults to `apikey`. With `jwt` the deployment adds `McpToolAdapterIdentity`, a Cognito
authorization server with two clients, and switches both ends: the gateway obtains a token from an
AgentCore Identity OAuth2 credential provider and presents it as a bearer token, and the application
validates it with `McpToolAdapter.Jwt` instead of checking a shared secret.

The two clients are separate on purpose. `agent` is what a caller presents to the gateway, which the
gateway validates as an OAuth resource server. `gateway-outbound` is what the gateway uses to obtain a
token for the application, and it is the only client the application accepts.

Observed, from the application's own log:

```
Deployed mode: 15 tool(s), mutating=False, target=orderportal, auth=jwt bearer
     Authorization: <redacted, 868 chars>
  audit: get_order caller=3r5cq6ifo53ve0b285094ierls ok=True 20ms args=[orderId]
```

An 868-character bearer token arrived instead of `X-Mcp-Key`, and the caller recorded in the audit trail
is the outbound client id extracted from the token's claims. That is the full chain working: caller token,
gateway inbound validation, a token fetched from the token vault, signature and issuer and scope and
client checked in .NET, and a principal established for the invocation.

Rejections were checked too, because an authorization path that only accepts is not evidence of anything:

| Presented | Result |
|---|---|
| A token from a client not in `allowedClients` | 403, `insufficient_scope` |
| No token | 401, `Missing Bearer token` |
| A malformed token | 401, `Invalid Bearer token` |

## Identity: what it does not prove

The grant type is `CLIENT_CREDENTIALS`, so the token names a client rather than a person. Its `sub` is
the client id. Everything about the bearer path is exercised, but no named end user crosses the gateway,
which is why the audit line shows a client id.

Carrying a named user needs a user-federation flow, and that is where persistence comes in. AgentCore
Identity's token vault stores a user's refresh token, keyed by workload identity and user id, so consent
is needed once rather than per call:

1. The agent gets a workload identity token with `GetWorkloadAccessTokenForUserId(workloadName, userId)`,
   or `GetWorkloadAccessTokenForJWT` when it already holds the user's token.
2. It calls `GetResourceOauth2Token`. If the vault holds a usable token for that user it returns an
   `accessToken`. If not, it returns an `authorizationUrl` and a `sessionUri`.
3. The user consents once, and `CompleteResourceTokenAuth(userIdentifier, sessionUri)` finishes it.
4. Later calls return the token from the vault. That is the persistence, and it is configuration rather
   than something to build.

The receiving half in this repository does not change for that flow. `JwtBearerAuthorizer` validates
whatever token arrives, and `PrincipalMapper` maps its claims onto a principal. What changes is which
claims are present: a user-federation token carries the user's `sub` rather than a client id.

It has not been run here, because it needs a browser consent step that cannot be automated in a test.

One thing that never carries over, whichever flow you use: code reading `Session["CurrentUser"]` depends
on a browser session an agent does not have. That code has to take its inputs as parameters.

## Memory: where it belongs

Memory is Amazon Bedrock AgentCore Memory, and it belongs to the agent, not to the application behind the
tools. Nothing in `McpToolAdapter` reads or writes it.

That is a deliberate boundary rather than an omission. The adapter is a stateless surface over methods
that already exist, and an agent has no browser session for a `System.Web` application to attach state
to. Putting conversational state inside a long-lived line-of-business application would rebuild a managed
service in the one place least able to host it.

```bash
npx cdk deploy McpToolAdapterMemory
```

The stack creates one memory with two extraction strategies, both scoped by actor:

| Strategy | Namespace | For |
|---|---|---|
| `facts` (semantic) | `/facts/{actorId}` | Things worth recalling across sessions |
| `preferences` (user preference) | `/preferences/{actorId}` | How a user prefers answers shaped |

Namespaces are the isolation boundary. A retrieval scoped to `/facts/pspies` cannot return another
actor's records, so getting the namespace wrong is a data-leak bug rather than an untidiness. That is why
both are actor-scoped here.

Short-term memory is the raw event log, written with `CreateEvent(memoryId, actorId, sessionId, payload)`
and expiring after 30 days in this stack. Long-term memory is extracted from those events by the
strategies and read back with `RetrieveMemoryRecords`, which is a semantic query rather than a key
lookup.

Verified against the deployed memory. Two conversational turns were written for actor `pspies` in session
`session-1`:

```
status: ACTIVE
strategies: [('facts', 'SEMANTIC', ['/facts/{actorId}']),
             ('preferences', 'USER_PREFERENCE', ['/preferences/{actorId}'])]
actors: ['pspies']      sessions: ['session-1']      events stored: 2
```

Retrieval immediately afterwards returned nothing. A few minutes later, the same query returned extracted
records:

```
/facts/pspies       "The user frequently asks about the customer with email accounts003@example.com."
/preferences/pspies "The user explicitly asked about unshipped orders for a specific customer email…"
```

That gap matters when you write the agent. Extraction is an asynchronous managed job, so records appear
some minutes after the events that produced them, not on the next call. Code that writes an event and
immediately retrieves will find nothing, and should not treat that as an error or retry in a loop.

## The gap worth planning around

AgentCore Gateway sends an OpenAPI target no conversation identity. The full header set the application
receives is the content headers, `Host`, `User-Agent`, `X-Amzn-Trace-Id`, the VPC endpoint headers,
`X-Forwarded-For`, and the credential. There is no actor id and no session id.

So the application's audit trail cannot be joined to an agent conversation today. `X-Amzn-Trace-Id`
correlates a single request, not a conversation, and `ToolAuditEntry.CorrelationId` exists but nothing
populates it from the gateway.

If you need that join, the options are to pass a session identifier as an explicit tool argument, which
costs a parameter on every signature, or to correlate on the caller identity and a time window, which is
weaker. Worth deciding before someone is asked to produce an audit trail spanning both sides.
