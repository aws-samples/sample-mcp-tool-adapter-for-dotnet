#!/usr/bin/env python3
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: MIT-0
"""
Reconciles McpToolAdapter endpoints into Amazon Bedrock AgentCore Gateway targets.

Declarative and idempotent: a manifest lists the applications, and each run brings the gateway's
targets into line with what those applications currently expose. Safe to run on every deploy and
safe to run twice.

Two entry points, one implementation:
  * `main()`    - CLI, for a post-deploy step in an application's pipeline.
  * `handler()` - Lambda, for scheduled reconciliation that catches drift nobody deployed.

Design decisions worth knowing:

  * Compatibility is not re-checked here. The application already validates itself against
    AgentCore's constraints and reports the result at /_mcp/health; this reads that and refuses to
    proceed on errors. Re-implementing those rules in a third language (after C# and TypeScript)
    would guarantee the copies drift. The one exception is the tool-name budget, which needs the
    target name and is four lines of arithmetic.

  * Nothing is ever deleted implicitly. Removing an application from the manifest leaves its target
    alone; pruning requires --prune and names each target it would remove. A reconciler that
    silently deletes production tools because someone edited a config file is a bad trade.

  * --dry-run is the default. You must pass --apply to mutate anything.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional, Protocol, Sequence

# Bedrock ToolSpecification: "Maximum length of 64. Pattern: [a-zA-Z0-9_-]+"
MAX_TOOL_NAME_LENGTH = 64

# AgentCore joins target name and operationId with three underscores.
TARGET_NAME_DELIMITER = "___"

API_KEY_HEADER = "X-Mcp-Key"


# --------------------------------------------------------------------------------------------------
# Manifest
# --------------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class PrivateEndpoint:
    """
    VPC Lattice private connectivity for a target, so the gateway reaches the application without it
    being exposed to the internet.

    `routing_domain` is needed when the document's server domain is not publicly resolvable — for a
    private REST API Gateway it is the execute-api VPC endpoint's DNS name, which tells Lattice where
    to send the traffic.
    """

    vpc_id: str
    subnet_ids: List[str]
    security_group_ids: List[str]
    routing_domain: Optional[str] = None
    ip_address_type: str = "IPV4"

    @staticmethod
    def from_dict(raw: Optional[Dict[str, Any]]) -> Optional["PrivateEndpoint"]:
        if not raw:
            return None

        missing = [k for k in ("vpcId", "subnetIds", "securityGroupIds") if not raw.get(k)]
        if missing:
            raise ValueError(f"privateEndpoint is missing required key(s): {', '.join(missing)}")

        return PrivateEndpoint(
            vpc_id=raw["vpcId"],
            subnet_ids=list(raw["subnetIds"]),
            security_group_ids=list(raw["securityGroupIds"]),
            routing_domain=raw.get("routingDomain"),
            ip_address_type=raw.get("ipAddressType", "IPV4"),
        )

    def to_configuration(self) -> Dict[str, Any]:
        """
        Builds the privateEndpoint argument.

        `privateEndpoint` is a tagged union — exactly one of `managedVpcResource` or
        `selfManagedLatticeResource`. `routingDomain` goes *inside* managedVpcResource, not beside it;
        placing it beside produces "Unknown parameter in privateEndpoint: routingDomain" from the SDK.
        Verified against the live service model rather than inferred from the documentation example.
        """
        managed: Dict[str, Any] = {
            "vpcIdentifier": self.vpc_id,
            "subnetIds": self.subnet_ids,
            "endpointIpAddressType": self.ip_address_type,
            "securityGroupIds": self.security_group_ids,
        }
        if self.routing_domain:
            managed["routingDomain"] = self.routing_domain
        return {"managedVpcResource": managed}


@dataclass(frozen=True)
class Application:
    """One .NET application to expose as a gateway target."""

    target_name: str
    base_url: str
    shared_secret_arn: str
    credential_provider_arn: str
    description: str = ""
    shared_secret_json_field: Optional[str] = None
    private_endpoint: Optional[PrivateEndpoint] = None

    # Path to a pre-generated OpenAPI document, used instead of fetching from the application.
    #
    # Needed for a private target: this reconciler reads the document over HTTP, and a private endpoint
    # is unreachable from outside the VPC — including from wherever you are running this. Produce the
    # document by running the application locally with MCP_SERVER_URL set to the deployed URL; it is
    # generated from the same code, so it is the same document.
    #
    # The cost is real and worth stating: supplying a file skips the /_mcp/health check, so the
    # application's own AgentCore compatibility verdict is not consulted. The tool-name budget and
    # server-URL checks below still run.
    schema_file: Optional[str] = None

    @staticmethod
    def from_dict(raw: Dict[str, Any]) -> "Application":
        missing = [
            key
            for key in ("targetName", "baseUrl", "sharedSecretArn", "credentialProviderArn")
            if not raw.get(key)
        ]
        if missing:
            raise ValueError(f"manifest entry is missing required key(s): {', '.join(missing)}")

        return Application(
            target_name=raw["targetName"],
            base_url=raw["baseUrl"].rstrip("/"),
            shared_secret_arn=raw["sharedSecretArn"],
            credential_provider_arn=raw["credentialProviderArn"],
            description=raw.get("description", ""),
            shared_secret_json_field=raw.get("sharedSecretJsonField"),
            private_endpoint=PrivateEndpoint.from_dict(raw.get("privateEndpoint")),
            schema_file=raw.get("schemaFile"),
        )


@dataclass(frozen=True)
class Manifest:
    gateway_identifier: str
    applications: List[Application]

    @staticmethod
    def load(path: str) -> "Manifest":
        with open(path, "r", encoding="utf-8") as handle:
            raw = json.load(handle)

        if not raw.get("gatewayIdentifier"):
            raise ValueError("manifest is missing 'gatewayIdentifier'")

        applications = [Application.from_dict(entry) for entry in raw.get("applications", [])]
        if not applications:
            raise ValueError("manifest lists no applications")

        names = [a.target_name for a in applications]
        duplicates = {n for n in names if names.count(n) > 1}
        if duplicates:
            raise ValueError(f"duplicate targetName(s) in manifest: {', '.join(sorted(duplicates))}")

        return Manifest(gateway_identifier=raw["gatewayIdentifier"], applications=applications)


# --------------------------------------------------------------------------------------------------
# Collaborators, injected so the reconciler is testable without AWS or a running application
# --------------------------------------------------------------------------------------------------


class SecretReader(Protocol):
    def read(self, secret_arn: str, json_field: Optional[str]) -> str: ...


class EndpointReader(Protocol):
    def health(self, base_url: str, secret: str) -> Dict[str, Any]: ...
    def openapi(self, base_url: str, secret: str) -> Dict[str, Any]: ...


class GatewayControlPlane(Protocol):
    def list_targets(self, gateway_identifier: str) -> List[Dict[str, Any]]: ...
    def get_target_schema(self, gateway_identifier: str, target_id: str) -> Optional[Dict[str, Any]]: ...
    def create_target(self, gateway_identifier: str, application: Application, document: Dict[str, Any]) -> str: ...
    def update_target(self, gateway_identifier: str, target_id: str, application: Application, document: Dict[str, Any]) -> None: ...
    def delete_target(self, gateway_identifier: str, target_id: str) -> None: ...


# --------------------------------------------------------------------------------------------------
# Outcome reporting
# --------------------------------------------------------------------------------------------------


@dataclass
class Outcome:
    target_name: str
    action: str  # created | updated | unchanged | failed | would-prune | pruned
    detail: str = ""
    warnings: List[str] = field(default_factory=list)

    @property
    def failed(self) -> bool:
        return self.action == "failed"


@dataclass
class ReconcileReport:
    outcomes: List[Outcome] = field(default_factory=list)

    @property
    def failed(self) -> bool:
        return any(o.failed for o in self.outcomes)

    @property
    def changed(self) -> bool:
        return any(o.action in ("created", "updated", "pruned") for o in self.outcomes)

    def render(self) -> str:
        lines = []
        for outcome in self.outcomes:
            lines.append(f"[{outcome.action:>12}] {outcome.target_name}"
                         + (f" - {outcome.detail}" if outcome.detail else ""))
            for warning in outcome.warnings:
                lines.append(f"{'':>15}warning: {warning}")
        return "\n".join(lines) if lines else "(nothing to do)"


# --------------------------------------------------------------------------------------------------
# Reconciler
# --------------------------------------------------------------------------------------------------


class Reconciler:
    def __init__(
        self,
        control_plane: GatewayControlPlane,
        secrets: SecretReader,
        endpoints: EndpointReader,
        apply: bool = False,
        prune: bool = False,
    ) -> None:
        self._control_plane = control_plane
        self._secrets = secrets
        self._endpoints = endpoints
        self._apply = apply
        self._prune = prune

    def run(self, manifest: Manifest) -> ReconcileReport:
        report = ReconcileReport()

        existing = {
            target.get("name"): target
            for target in self._control_plane.list_targets(manifest.gateway_identifier)
        }

        for application in manifest.applications:
            try:
                report.outcomes.append(
                    self._reconcile_one(manifest.gateway_identifier, application, existing)
                )
            except Exception as error:
                # One unreachable application must not stop the others from reconciling.
                report.outcomes.append(
                    Outcome(application.target_name, "failed", f"{type(error).__name__}: {error}")
                )

        report.outcomes.extend(self._handle_orphans(manifest, existing))
        return report

    def _reconcile_one(
        self,
        gateway_identifier: str,
        application: Application,
        existing: Dict[Optional[str], Dict[str, Any]],
    ) -> Outcome:
        if application.schema_file:
            # Offline path: the endpoint is unreachable from here, so the document comes from disk and
            # the health check is skipped. Say so rather than let it look like a clean run.
            with open(application.schema_file, "r", encoding="utf-8") as handle:
                document = json.load(handle)
            warnings = [
                f"document read from {application.schema_file}; /_mcp/health was not consulted, so the "
                f"application's own AgentCore compatibility verdict was not checked"
            ]
        else:
            secret = self._secrets.read(application.shared_secret_arn, application.shared_secret_json_field)
            health = self._endpoints.health(application.base_url, secret)
            warnings = self._inspect_health(health)
            document = self._endpoints.openapi(application.base_url, secret)

        self._verify_document(application, document)

        current = existing.get(application.target_name)

        if current is None:
            if not self._apply:
                return Outcome(application.target_name, "unchanged",
                               "would create (dry run)", warnings)
            target_id = self._control_plane.create_target(gateway_identifier, application, document)
            return Outcome(application.target_name, "created", f"targetId={target_id}", warnings)

        target_id = current.get("targetId") or current.get("id") or ""
        deployed = self._control_plane.get_target_schema(gateway_identifier, target_id)

        if deployed is not None and _fingerprint(deployed) == _fingerprint(document):
            return Outcome(application.target_name, "unchanged", "schema matches", warnings)

        if not self._apply:
            return Outcome(application.target_name, "unchanged", "would update (dry run)", warnings)

        self._control_plane.update_target(gateway_identifier, target_id, application, document)
        return Outcome(application.target_name, "updated", f"targetId={target_id}", warnings)

    @staticmethod
    def _inspect_health(health: Dict[str, Any]) -> List[str]:
        """
        Trusts the application's own compatibility verdict rather than recomputing it.

        Errors abort: the application has already determined the gateway would reject or
        mis-invoke it, and pushing the target anyway just moves the failure somewhere harder to see.
        """
        if not health.get("ok"):
            raise RuntimeError("endpoint reported ok=false; refusing to reconcile")

        issues = health.get("gatewayIssues", []) or []
        errors = [i for i in issues if i.get("severity") == "error"]
        if errors:
            rendered = "; ".join(f"{i.get('code')}: {i.get('message')}" for i in errors)
            raise RuntimeError(f"endpoint reports AgentCore compatibility errors: {rendered}")

        return [f"{i.get('code')}: {i.get('message')}" for i in issues
                if i.get("severity") == "warning"]

    @staticmethod
    def _verify_document(application: Application, document: Dict[str, Any]) -> None:
        """
        Two checks the application cannot make for itself.

        The tool-name budget needs the target name, which lives in the manifest. And the server URL
        must match the application we actually fetched from, otherwise a misconfigured serverUrl
        would register a target pointing at a different environment.
        """
        server_url = (document.get("servers") or [{}])[0].get("url", "")
        if server_url.rstrip("/") != application.base_url:
            raise RuntimeError(
                f"document declares servers[0].url={server_url!r} but was fetched from "
                f"{application.base_url!r}. Fix the application's mcp:serverUrl before registering, "
                f"or the gateway will call the wrong host."
            )

        budget = MAX_TOOL_NAME_LENGTH - len(application.target_name) - len(TARGET_NAME_DELIMITER)
        over_budget = []

        for path, methods in (document.get("paths") or {}).items():
            for method, operation in (methods or {}).items():
                operation_id = (operation or {}).get("operationId")
                if not operation_id:
                    raise RuntimeError(f"{method.upper()} {path} has no operationId")
                if len(operation_id) > budget:
                    visible = f"{application.target_name}{TARGET_NAME_DELIMITER}{operation_id}"
                    over_budget.append(f"{visible} ({len(visible)} chars)")

        if over_budget:
            raise RuntimeError(
                f"tool name(s) exceed {MAX_TOOL_NAME_LENGTH} characters once the target prefix is "
                f"applied, leaving {budget} for the operationId: {', '.join(over_budget)}. "
                f"This fails at invocation, not at target creation."
            )

    def _handle_orphans(
        self, manifest: Manifest, existing: Dict[Optional[str], Dict[str, Any]]
    ) -> List[Outcome]:
        declared = {a.target_name for a in manifest.applications}
        orphans = [name for name in existing if name and name not in declared]

        outcomes = []
        for name in sorted(orphans):
            if not self._prune:
                outcomes.append(Outcome(
                    name, "would-prune",
                    "present on the gateway but absent from the manifest; "
                    "re-run with --prune to remove it",
                ))
                continue

            if not self._apply:
                outcomes.append(Outcome(name, "would-prune", "dry run"))
                continue

            target = existing[name]
            target_id = target.get("targetId") or target.get("id") or ""
            self._control_plane.delete_target(manifest.gateway_identifier, target_id)
            outcomes.append(Outcome(name, "pruned", f"targetId={target_id}"))

        return outcomes


def _fingerprint(document: Dict[str, Any]) -> str:
    """Order-independent hash, so key ordering alone never looks like a change."""
    canonical = json.dumps(document, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


# --------------------------------------------------------------------------------------------------
# Real collaborators
# --------------------------------------------------------------------------------------------------


class SecretsManagerReader:
    def __init__(self, client: Any) -> None:
        self._client = client

    def read(self, secret_arn: str, json_field: Optional[str]) -> str:
        value = self._client.get_secret_value(SecretId=secret_arn)["SecretString"]
        if json_field:
            return json.loads(value)[json_field]
        return value


class HttpEndpointReader:
    def __init__(self, timeout_seconds: int = 15) -> None:
        self._timeout = timeout_seconds

    def health(self, base_url: str, secret: str) -> Dict[str, Any]:
        return self._get_json(f"{base_url}/_mcp/health", secret)

    def openapi(self, base_url: str, secret: str) -> Dict[str, Any]:
        return self._get_json(f"{base_url}/_mcp/openapi.json", secret)

    def _get_json(self, url: str, secret: str) -> Dict[str, Any]:
        # The base URL comes from the manifest, which is configuration rather than a person typing a
        # URL. urlopen honours file://, ftp:// and anything else registered, so an unvalidated scheme
        # here is a way to make this read a local file — while also sending the shared secret to
        # whatever that scheme resolves to. Restrict it to HTTP(S) before opening.
        scheme = urllib.parse.urlsplit(url).scheme.lower()
        if scheme not in ("http", "https"):
            raise RuntimeError(
                f"baseUrl must be an http or https URL; got scheme '{scheme or 'none'}' in {url!r}"
            )

        request = urllib.request.Request(url, headers={API_KEY_HEADER: secret})
        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:  # nosec B310
                return json.loads(response.read())
        except urllib.error.HTTPError as error:
            hint = {
                404: "the endpoint is not enabled (mcp:enabled)",
                401: "the shared secret does not match",
                403: "HTTPS is required, or the caller address is not allowed",
                503: "the endpoint is enabled but its catalog failed to build",
            }.get(error.code, "")
            raise RuntimeError(f"GET {url} returned {error.code}"
                               + (f" - probably {hint}" if hint else "")) from error


class AgentCoreControlPlane:
    """
    Thin wrapper over bedrock-agentcore-control.

    Deliberately thin and isolated: `create_gateway_target` is confirmed against the AgentCore
    documentation, but the exact shapes of the list, get, update and delete calls are NOT verified
    here. If a call name or field is wrong, it is wrong in one small class rather than throughout
    the reconciler.
    """

    def __init__(self, client: Any) -> None:
        self._client = client

    def list_targets(self, gateway_identifier: str) -> List[Dict[str, Any]]:
        targets: List[Dict[str, Any]] = []
        token: Optional[str] = None

        while True:
            kwargs: Dict[str, Any] = {"gatewayIdentifier": gateway_identifier}
            if token:
                kwargs["nextToken"] = token
            response = self._client.list_gateway_targets(**kwargs)
            targets.extend(response.get("items", []) or response.get("targetSummaries", []) or [])
            token = response.get("nextToken")
            if not token:
                return targets

    def get_target_schema(self, gateway_identifier: str, target_id: str) -> Optional[Dict[str, Any]]:
        response = self._client.get_gateway_target(
            gatewayIdentifier=gateway_identifier, targetId=target_id
        )
        payload = (
            response.get("targetConfiguration", {})
            .get("mcp", {})
            .get("openApiSchema", {})
            .get("inlinePayload")
        )
        if not payload:
            # An S3-backed target, or a shape we do not recognise; treat as "unknown, so update".
            return None
        return json.loads(payload)

    def create_target(
        self, gateway_identifier: str, application: Application, document: Dict[str, Any]
    ) -> str:
        response = self._client.create_gateway_target(
            gatewayIdentifier=gateway_identifier,
            name=application.target_name,
            description=application.description or f"Operations exposed by {application.target_name}",
            targetConfiguration=_target_configuration(document),
            credentialProviderConfigurations=_credential_configuration(application),
            **_private_endpoint_kwargs(application),
        )
        return response.get("targetId", "")

    def update_target(
        self, gateway_identifier: str, target_id: str, application: Application, document: Dict[str, Any]
    ) -> None:
        self._client.update_gateway_target(
            gatewayIdentifier=gateway_identifier,
            targetId=target_id,
            name=application.target_name,
            description=application.description or f"Operations exposed by {application.target_name}",
            targetConfiguration=_target_configuration(document),
            credentialProviderConfigurations=_credential_configuration(application),
            **_private_endpoint_kwargs(application),
        )

    def delete_target(self, gateway_identifier: str, target_id: str) -> None:
        self._client.delete_gateway_target(
            gatewayIdentifier=gateway_identifier, targetId=target_id
        )


# Documents are sent inline. AgentCore also accepts an S3 reference, which is the route for a very
# large schema; this warns rather than failing, since the threshold is not published.
INLINE_PAYLOAD_WARN_BYTES = 200_000


def _target_configuration(document: Dict[str, Any]) -> Dict[str, Any]:
    payload = json.dumps(document)
    if len(payload) > INLINE_PAYLOAD_WARN_BYTES:
        print(f"  warning: the OpenAPI document is {len(payload):,} bytes. If target creation is "
              f"rejected for size, upload it to S3 and reference it instead of sending it inline.")
    return {"mcp": {"openApiSchema": {"inlinePayload": payload}}}


def _private_endpoint_kwargs(application: Application) -> Dict[str, Any]:
    """
    Private connectivity, when configured.

    Passed as a top-level `privateEndpoint` argument rather than inside targetConfiguration, matching
    the developer guide's example. Like the other control-plane shapes here, the exact placement is not
    verified against a live API — see the note on AgentCoreControlPlane.
    """
    if application.private_endpoint is None:
        return {}
    return {"privateEndpoint": application.private_endpoint.to_configuration()}


def _credential_configuration(application: Application) -> List[Dict[str, Any]]:
    # API key in a header. IAM (SigV4) is not usable for an IIS-hosted target, which must natively
    # verify SigV4 to qualify.
    return [
        {
            "credentialProviderType": "API_KEY",
            "credentialProvider": {
                "apiKeyCredentialProvider": {
                    "providerArn": application.credential_provider_arn,
                    "credentialLocation": "HEADER",
                    "credentialParameterName": API_KEY_HEADER,
                }
            },
        }
    ]


# --------------------------------------------------------------------------------------------------
# Entry points
# --------------------------------------------------------------------------------------------------


def _build_reconciler(apply: bool, prune: bool, region: Optional[str]) -> Reconciler:
    import boto3  # imported lazily so tests and --help need no AWS SDK

    session = boto3.session.Session(region_name=region) if region else boto3.session.Session()
    return Reconciler(
        control_plane=AgentCoreControlPlane(session.client("bedrock-agentcore-control")),
        secrets=SecretsManagerReader(session.client("secretsmanager")),
        endpoints=HttpEndpointReader(),
        apply=apply,
        prune=prune,
    )


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("manifest", help="path to applications.json")
    parser.add_argument("--apply", action="store_true",
                        help="actually create or update targets (default is a dry run)")
    parser.add_argument("--prune", action="store_true",
                        help="also remove targets absent from the manifest; requires --apply to act")
    parser.add_argument("--region", default=None)
    args = parser.parse_args(argv)

    manifest = Manifest.load(args.manifest)
    report = _build_reconciler(args.apply, args.prune, args.region).run(manifest)

    print(report.render())
    if not args.apply:
        print("\n(dry run - nothing was changed. Re-run with --apply.)")

    return 1 if report.failed else 0


def handler(event: Dict[str, Any], _context: Any = None) -> Dict[str, Any]:
    """
    Lambda entry point for scheduled reconciliation.

    Expects {"manifestPath": "...", "apply": true}. Pruning is not exposed here on purpose: an
    unattended job should never delete production tools.
    """
    manifest = Manifest.load(event.get("manifestPath", "applications.json"))
    report = _build_reconciler(apply=bool(event.get("apply", False)), prune=False,
                               region=event.get("region")).run(manifest)

    return {
        "failed": report.failed,
        "changed": report.changed,
        "outcomes": [
            {"target": o.target_name, "action": o.action, "detail": o.detail, "warnings": o.warnings}
            for o in report.outcomes
        ],
    }


if __name__ == "__main__":
    sys.exit(main())
