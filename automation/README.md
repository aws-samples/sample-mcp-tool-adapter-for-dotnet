# Automating target registration

Turns "fetch the schema, edit CDK, deploy" into one idempotent command. Run it on every application
deploy. Run it twice and nothing happens the second time.

```bash
# Dry run is the default. It shows what would change and touches nothing.
python3 agentcore_reconcile.py applications.json

# Apply.
python3 agentcore_reconcile.py applications.json --apply
```

Adding an application is an entry in `applications.json`, not a code change.

## What one run does, per application

1. Reads the shared secret from Secrets Manager.
2. `GET /_mcp/health`, and aborts if the application reports AgentCore compatibility errors.
3. `GET /_mcp/openapi.json`.
4. Checks the tool-name budget against the manifest's target name, and that `servers[0].url` matches the
   host it actually fetched from.
5. Compares against the deployed target's schema by content hash.
6. Creates, updates, or does nothing.

## Three decisions worth knowing

Compatibility is not re-checked here. The application already validates itself against AgentCore's
constraints and publishes the verdict on `/_mcp/health`, and this reads it. Reimplementing those rules in
a third language, after C# and TypeScript, would guarantee the copies drift. There are two exceptions,
both checks the application cannot make for itself: the tool-name budget needs the target name, which only
the manifest knows, and the `servers[0].url` comparison needs to know which host was actually contacted.
That second one catches a stale `mcp:serverUrl` registering a target that points at production from a
staging deploy.

Nothing is deleted implicitly. Removing an application from the manifest reports the orphan and leaves it
alone. Pruning needs both `--prune` and `--apply`, and it names each target. The Lambda entry point does
not expose pruning at all, because an unattended job should not be able to delete production tools.

Failures are per-application. One unreachable application is reported and skipped, and the rest still
reconcile. The exit code is non-zero if any failed.

## Private endpoints

An application with no public endpoint needs a `privateEndpoint` block in its manifest entry: VPC id,
subnet ids, the resource gateway security group, which is the one that needs egress, and a `routingDomain`
when the document's host is not publicly resolvable. See [`applications.json`](applications.json) and
[`../docs/agentcore-test.md`](../docs/agentcore-test.md).

Two things to know before you try it.

This reconciler reads `/_mcp/health` and `/_mcp/openapi.json` from the application, so it cannot reach a
private target from outside the VPC. There are three ways out, in order of preference. Declare the target
in CDK instead, since `GatewayStack` supports `privateEndpoint` and that is the path the sample uses. Or
run this inside the VPC. Or set `schemaFile` in the manifest entry to a document produced by
`dotnet run --project <app> -- --dump-openapi <path>`.

`schemaFile` has a cost, and the run says so in its warnings: the `/_mcp/health` check is skipped, so the
application's own AgentCore compatibility verdict is never consulted. The tool-name budget and server-URL
checks still run.

The gateway role needs `bedrock-agentcore:GetResourceApiKey`. CDK grants that only when CDK itself
declares the target, so `GatewayStack` always grants it. Without it the target reports READY, `tools/list`
works, and only `tools/call` fails, with a generic error and no logs.

## Scheduled reconciliation

`handler()` is a Lambda entry point for catching drift nobody deployed:

```python
{"manifestPath": "applications.json", "apply": true}
```

Same code path as the CLI. It needs network reach to each application, plus
`secretsmanager:GetSecretValue` and `bedrock-agentcore-control` permissions.

## CDK or this?

Both work. Pick one as the source of truth per application, not both.

Prefer CDK, in [`../cdk`](../cdk/README.md), when CloudFormation deploys the application too. Targets are
then declared infrastructure: reviewed, versioned and visible in a stack diff. Both seams that used to
argue against it are closed, because `GatewayStack` supports `privateEndpoint` and synth generates the
OpenAPI document by running the application's own `--dump-openapi`, so nothing has to be fetched from a
live endpoint. That is what `cdk/bin/app.ts` does for the order portal sample.

Prefer this reconciler when the application's lifecycle is not CloudFormation's to own, such as an
existing IIS estate deployed by something else. It reads the schema from the live application, so a
`ToolRegistry` change is picked up by a scheduled run with no deploy at all. The cost is that those targets
are not in your CloudFormation state.

Either way, use CDK for the gateway, Cognito and credential providers, which are created once.

## Tests

```bash
python3 -m unittest discover -s . -p 'test_*.py'
```

32 tests, with no AWS account or running application required, because the control plane, secrets and HTTP
reader are injected. They cover create, update, no-op, key-ordering-is-not-a-change, every refusal path,
prune safety, the `privateEndpoint` shape, the offline `schemaFile` path, and the URL scheme check.

## What is still unexercised

`create_gateway_target`, `get_gateway_target` and `update_gateway_target` have been run against a live
gateway, including the `privateEndpoint` argument. That argument is where the documentation's example was
misleading, since `routingDomain` belongs inside `managedVpcResource` rather than beside it.

The list and delete calls in `AgentCoreControlPlane` have not been run against a live gateway, so `--prune`
and the unchanged and updated detection paths are unproven in practice. They are isolated in that one small
class, so a correction touches very little. Dry run first.
