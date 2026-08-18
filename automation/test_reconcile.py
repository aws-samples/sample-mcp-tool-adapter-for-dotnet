#!/usr/bin/env python3
# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: MIT-0
"""
Tests for the reconciler, using fakes so no AWS account or running application is required.

Covers the decisions that matter: when it creates, when it updates, when it correctly does nothing,
and every case where it must refuse to act.

Run: python3 -m unittest discover -s automation -v
"""

from __future__ import annotations

import copy
import json
import os
import sys
import tempfile
import unittest
from typing import Any, Dict, List, Optional

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from agentcore_reconcile import (  # noqa: E402
    Application,
    HttpEndpointReader,
    Manifest,
    PrivateEndpoint,
    Reconciler,
)


def document(server_url: str = "https://orders.internal.example.com",
            operation_ids: Optional[List[str]] = None) -> Dict[str, Any]:
    operation_ids = operation_ids or ["get_order_by_id", "cancel_order"]
    return {
        "openapi": "3.0.3",
        "info": {"title": "Orders", "version": "1.0.0"},
        "servers": [{"url": server_url}],
        "paths": {
            f"/_mcp/tools/{operation_id}": {
                "post": {"operationId": operation_id, "summary": "..."}
            }
            for operation_id in operation_ids
        },
    }


class FakeSecrets:
    def read(self, secret_arn: str, json_field: Optional[str]) -> str:
        return "a" * 32


class FakeEndpoints:
    def __init__(self, doc: Dict[str, Any], health: Optional[Dict[str, Any]] = None,
                 error: Optional[Exception] = None) -> None:
        self._doc = doc
        self._health = health if health is not None else {"ok": True, "tools": 2}
        self._error = error

    def health(self, base_url: str, secret: str) -> Dict[str, Any]:
        if self._error:
            raise self._error
        return self._health

    def openapi(self, base_url: str, secret: str) -> Dict[str, Any]:
        if self._error:
            raise self._error
        return self._doc


class FakeControlPlane:
    def __init__(self, targets: Optional[List[Dict[str, Any]]] = None,
                 schemas: Optional[Dict[str, Dict[str, Any]]] = None) -> None:
        self.targets = targets or []
        self.schemas = schemas or {}
        self.created: List[str] = []
        self.updated: List[str] = []
        self.deleted: List[str] = []

    def list_targets(self, gateway_identifier: str) -> List[Dict[str, Any]]:
        return copy.deepcopy(self.targets)

    def get_target_schema(self, gateway_identifier: str, target_id: str) -> Optional[Dict[str, Any]]:
        return self.schemas.get(target_id)

    def create_target(self, gateway_identifier, application, doc) -> str:
        self.created.append(application.target_name)
        return f"tgt-{application.target_name}"

    def update_target(self, gateway_identifier, target_id, application, doc) -> None:
        self.updated.append(target_id)

    def delete_target(self, gateway_identifier, target_id) -> None:
        self.deleted.append(target_id)


def application(target_name: str = "orderapp",
                base_url: str = "https://orders.internal.example.com") -> Application:
    return Application(
        target_name=target_name,
        base_url=base_url,
        shared_secret_arn="arn:aws:secretsmanager:::secret:x",  # nosec B106 - an ARN, not a credential
        credential_provider_arn="arn:aws:bedrock-agentcore:::apikeycredentialprovider/x",
    )


def manifest(*applications: Application) -> Manifest:
    return Manifest(gateway_identifier="gw-1", applications=list(applications or [application()]))


class CreateAndUpdateTests(unittest.TestCase):
    def test_creates_a_target_that_does_not_exist(self):
        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(document()), apply=True).run(manifest())

        self.assertEqual(["orderapp"], control.created)
        self.assertEqual("created", report.outcomes[0].action)
        self.assertFalse(report.failed)

    def test_does_nothing_when_the_deployed_schema_already_matches(self):
        doc = document()
        control = FakeControlPlane(
            targets=[{"name": "orderapp", "targetId": "tgt-1"}],
            schemas={"tgt-1": doc},
        )

        report = Reconciler(control, FakeSecrets(), FakeEndpoints(doc), apply=True).run(manifest())

        self.assertEqual([], control.created)
        self.assertEqual([], control.updated)
        self.assertEqual("unchanged", report.outcomes[0].action)
        self.assertFalse(report.changed)

    def test_key_ordering_alone_is_not_treated_as_a_change(self):
        deployed = document()
        reordered = json.loads(json.dumps(deployed))
        reordered["info"] = {"version": "1.0.0", "title": "Orders"}  # same content, different order

        control = FakeControlPlane(
            targets=[{"name": "orderapp", "targetId": "tgt-1"}], schemas={"tgt-1": deployed}
        )
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(reordered), apply=True).run(manifest())

        self.assertEqual([], control.updated)
        self.assertEqual("unchanged", report.outcomes[0].action)

    def test_updates_when_an_operation_is_added(self):
        deployed = document()
        current = document(operation_ids=["get_order_by_id", "cancel_order", "search_orders"])

        control = FakeControlPlane(
            targets=[{"name": "orderapp", "targetId": "tgt-1"}], schemas={"tgt-1": deployed}
        )
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(current), apply=True).run(manifest())

        self.assertEqual(["tgt-1"], control.updated)
        self.assertEqual("updated", report.outcomes[0].action)

    def test_updates_when_the_deployed_schema_cannot_be_read(self):
        # An S3-backed or unrecognised target shape: prefer a redundant update over silent drift.
        control = FakeControlPlane(targets=[{"name": "orderapp", "targetId": "tgt-1"}], schemas={})
        Reconciler(control, FakeSecrets(), FakeEndpoints(document()), apply=True).run(manifest())

        self.assertEqual(["tgt-1"], control.updated)


class DryRunTests(unittest.TestCase):
    def test_dry_run_is_the_default_and_mutates_nothing(self):
        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(document())).run(manifest())

        self.assertEqual([], control.created)
        self.assertIn("would create", report.outcomes[0].detail)

    def test_dry_run_reports_a_pending_update_without_applying_it(self):
        control = FakeControlPlane(
            targets=[{"name": "orderapp", "targetId": "tgt-1"}],
            schemas={"tgt-1": document(operation_ids=["get_order_by_id"])},
        )
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(document())).run(manifest())

        self.assertEqual([], control.updated)
        self.assertIn("would update", report.outcomes[0].detail)


class RefusalTests(unittest.TestCase):
    def test_refuses_when_the_application_reports_compatibility_errors(self):
        health = {
            "ok": True,
            "gatewayIssues": [
                {"severity": "error", "code": "tool_name_too_long", "message": "over 64"}
            ],
        }
        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(document(), health), apply=True).run(manifest())

        self.assertEqual([], control.created)
        self.assertTrue(report.failed)
        self.assertIn("tool_name_too_long", report.outcomes[0].detail)

    def test_passes_warnings_through_without_blocking(self):
        health = {
            "ok": True,
            "gatewayIssues": [
                {"severity": "warning", "code": "double_prefixed_tool_names", "message": "redundant"}
            ],
        }
        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(document(), health), apply=True).run(manifest())

        self.assertEqual(["orderapp"], control.created)
        self.assertEqual(["double_prefixed_tool_names: redundant"], report.outcomes[0].warnings)

    def test_refuses_when_the_endpoint_reports_not_ok(self):
        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(document(), {"ok": False}), apply=True).run(manifest())

        self.assertTrue(report.failed)
        self.assertEqual([], control.created)

    def test_refuses_when_the_declared_server_url_is_not_the_host_we_fetched_from(self):
        # Guards against a stale mcp:serverUrl registering a target that calls production
        # from a staging deployment, or vice versa.
        mismatched = document(server_url="https://orders-staging.internal.example.com")
        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(mismatched), apply=True).run(manifest())

        self.assertTrue(report.failed)
        self.assertIn("wrong host", report.outcomes[0].detail)
        self.assertEqual([], control.created)

    def test_refuses_a_tool_name_over_budget_once_the_target_prefix_applies(self):
        long_operation = "get_the_full_order_history_for_a_customer_by_email"  # 49 chars
        control = FakeControlPlane()

        report = Reconciler(
            control, FakeSecrets(), FakeEndpoints(document(operation_ids=[long_operation])), apply=True
        ).run(manifest(application(target_name="order_management_target")))  # 23 chars

        self.assertTrue(report.failed)
        self.assertIn("exceed 64 characters", report.outcomes[0].detail)
        self.assertEqual([], control.created)

    def test_accepts_a_tool_name_exactly_at_the_budget(self):
        # 64 - len("orderapp") - 3 = 53
        control = FakeControlPlane()
        report = Reconciler(
            control, FakeSecrets(), FakeEndpoints(document(operation_ids=["a" * 53])), apply=True
        ).run(manifest())

        self.assertFalse(report.failed)
        self.assertEqual(["orderapp"], control.created)

    def test_refuses_an_operation_without_an_operation_id(self):
        doc = document()
        doc["paths"]["/_mcp/tools/broken"] = {"post": {"summary": "no operationId"}}
        control = FakeControlPlane()

        report = Reconciler(control, FakeSecrets(), FakeEndpoints(doc), apply=True).run(manifest())

        self.assertTrue(report.failed)
        self.assertEqual([], control.created)

    def test_one_unreachable_application_does_not_stop_the_others(self):
        class SelectiveEndpoints(FakeEndpoints):
            def health(self, base_url, secret):
                if "broken" in base_url:
                    raise RuntimeError("connection refused")
                return {"ok": True}

            def openapi(self, base_url, secret):
                return document(server_url=base_url)

        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), SelectiveEndpoints(document()), apply=True).run(
            manifest(
                application("broken", "https://broken.internal.example.com"),
                application("healthy", "https://healthy.internal.example.com"),
            )
        )

        self.assertTrue(report.failed)
        self.assertEqual(["healthy"], control.created)
        actions = {o.target_name: o.action for o in report.outcomes}
        self.assertEqual("failed", actions["broken"])
        self.assertEqual("created", actions["healthy"])


class PruneTests(unittest.TestCase):
    def test_never_deletes_without_prune(self):
        control = FakeControlPlane(targets=[{"name": "retired_app", "targetId": "tgt-old"}])
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(document()), apply=True).run(manifest())

        self.assertEqual([], control.deleted)
        orphan = next(o for o in report.outcomes if o.target_name == "retired_app")
        self.assertEqual("would-prune", orphan.action)
        self.assertIn("--prune", orphan.detail)

    def test_prune_still_respects_dry_run(self):
        control = FakeControlPlane(targets=[{"name": "retired_app", "targetId": "tgt-old"}])
        Reconciler(control, FakeSecrets(), FakeEndpoints(document()), apply=False, prune=True).run(manifest())

        self.assertEqual([], control.deleted)

    def test_prune_with_apply_removes_the_orphan(self):
        control = FakeControlPlane(targets=[{"name": "retired_app", "targetId": "tgt-old"}])
        Reconciler(control, FakeSecrets(), FakeEndpoints(document()), apply=True, prune=True).run(manifest())

        self.assertEqual(["tgt-old"], control.deleted)


class OfflineSchemaTests(unittest.TestCase):
    """A private target cannot be fetched from, so the document comes from disk instead."""

    def _document_file(self, doc):
        handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        json.dump(doc, handle)
        handle.close()
        return handle.name

    def test_uses_the_file_and_never_touches_the_endpoint(self):
        class ExplodingEndpoints:
            def health(self, *a): raise AssertionError("health must not be called")
            def openapi(self, *a): raise AssertionError("openapi must not be called")

        path = self._document_file(document())
        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), ExplodingEndpoints(), apply=True).run(
            manifest(Application(
                target_name="orderportal",
                base_url="https://orders.internal.example.com",
                shared_secret_arn="arn:secret",  # nosec B106 - an ARN, not a credential
                credential_provider_arn="arn:provider",
                schema_file=path,
            )))

        self.assertEqual(["orderportal"], control.created)
        self.assertFalse(report.failed)

    def test_warns_that_the_health_check_was_skipped(self):
        path = self._document_file(document())
        report = Reconciler(FakeControlPlane(), FakeSecrets(), FakeEndpoints(document()), apply=True).run(
            manifest(Application(
                target_name="orderportal",
                base_url="https://orders.internal.example.com",
                shared_secret_arn="arn:secret",  # nosec B106 - an ARN, not a credential
                credential_provider_arn="arn:provider",
                schema_file=path,
            )))

        self.assertTrue(any("health was not consulted" in w for w in report.outcomes[0].warnings))

    def test_still_enforces_the_tool_name_budget_offline(self):
        path = self._document_file(document(operation_ids=["a" * 60]))
        control = FakeControlPlane()
        report = Reconciler(control, FakeSecrets(), FakeEndpoints(document()), apply=True).run(
            manifest(Application(
                target_name="orderportal",
                base_url="https://orders.internal.example.com",
                shared_secret_arn="arn:secret",  # nosec B106 - an ARN, not a credential
                credential_provider_arn="arn:provider",
                schema_file=path,
            )))

        self.assertTrue(report.failed)
        self.assertEqual([], control.created)


class UrlSchemeTests(unittest.TestCase):
    """A manifest is configuration, so its baseUrl is not automatically a safe thing to open."""

    def test_refuses_a_file_url(self):
        reader = HttpEndpointReader()
        with self.assertRaises(RuntimeError) as caught:
            reader.health("file:///etc/passwd", "secret-value-that-is-long-enough")
        self.assertIn("http or https", str(caught.exception))

    def test_refuses_a_scheme_less_url(self):
        reader = HttpEndpointReader()
        with self.assertRaises(RuntimeError) as caught:
            reader.openapi("orders.internal.example.com", "secret-value-that-is-long-enough")
        self.assertIn("http or https", str(caught.exception))


class PrivateEndpointTests(unittest.TestCase):
    def test_builds_the_managed_lattice_configuration(self):
        endpoint = PrivateEndpoint.from_dict({
            "vpcId": "vpc-0abc",
            "subnetIds": ["subnet-1", "subnet-2"],
            "securityGroupIds": ["sg-1"],
            "routingDomain": "vpce-123.execute-api.us-east-1.vpce.amazonaws.com",
        })

        configuration = endpoint.to_configuration()
        managed = configuration["managedVpcResource"]

        self.assertEqual("vpc-0abc", managed["vpcIdentifier"])
        self.assertEqual(["subnet-1", "subnet-2"], managed["subnetIds"])
        self.assertEqual(["sg-1"], managed["securityGroupIds"])
        self.assertEqual("IPV4", managed["endpointIpAddressType"])

        # Inside managedVpcResource, not beside it. The SDK rejects the sibling form outright, and
        # privateEndpoint is a tagged union that permits only the one member.
        self.assertEqual("vpce-123.execute-api.us-east-1.vpce.amazonaws.com",
                         managed["routingDomain"])
        self.assertEqual(["managedVpcResource"], list(configuration.keys()))

    def test_omits_routing_domain_when_not_supplied(self):
        endpoint = PrivateEndpoint.from_dict({
            "vpcId": "vpc-0abc", "subnetIds": ["subnet-1"], "securityGroupIds": ["sg-1"],
        })

        self.assertNotIn("routingDomain", endpoint.to_configuration()["managedVpcResource"])

    def test_absent_private_endpoint_is_none(self):
        self.assertIsNone(PrivateEndpoint.from_dict(None))
        self.assertIsNone(PrivateEndpoint.from_dict({}))

    def test_rejects_an_incomplete_private_endpoint(self):
        with self.assertRaises(ValueError) as caught:
            PrivateEndpoint.from_dict({"vpcId": "vpc-0abc"})
        self.assertIn("subnetIds", str(caught.exception))

    def test_manifest_carries_the_private_endpoint_through(self):
        handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        json.dump({
            "gatewayIdentifier": "gw-1",
            "applications": [{
                "targetName": "orderportal",
                "baseUrl": "https://abc.execute-api.us-east-1.amazonaws.com/live",
                "sharedSecretArn": "arn:secret",
                "credentialProviderArn": "arn:provider",
                "privateEndpoint": {
                    "vpcId": "vpc-0abc",
                    "subnetIds": ["subnet-1", "subnet-2"],
                    "securityGroupIds": ["sg-1"],
                    "routingDomain": "vpce-123.execute-api.us-east-1.vpce.amazonaws.com",
                },
            }],
        }, handle)
        handle.close()

        application = Manifest.load(handle.name).applications[0]

        self.assertIsNotNone(application.private_endpoint)
        self.assertEqual("vpc-0abc", application.private_endpoint.vpc_id)


class ManifestTests(unittest.TestCase):
    def _write(self, payload: Dict[str, Any]) -> str:
        handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        json.dump(payload, handle)
        handle.close()
        return handle.name

    def test_loads_a_valid_manifest(self):
        path = self._write({
            "gatewayIdentifier": "gw-1",
            "applications": [{
                "targetName": "orderapp",
                "baseUrl": "https://orders.internal.example.com/",
                "sharedSecretArn": "arn:secret",
                "credentialProviderArn": "arn:provider",
            }],
        })
        loaded = Manifest.load(path)

        self.assertEqual("gw-1", loaded.gateway_identifier)
        # Trailing slash normalised so the server-URL comparison is not defeated by formatting.
        self.assertEqual("https://orders.internal.example.com", loaded.applications[0].base_url)

    def test_rejects_a_missing_required_key(self):
        path = self._write({
            "gatewayIdentifier": "gw-1",
            "applications": [{"targetName": "orderapp"}],
        })
        with self.assertRaises(ValueError) as caught:
            Manifest.load(path)
        self.assertIn("baseUrl", str(caught.exception))

    def test_rejects_duplicate_target_names(self):
        entry = {
            "targetName": "orderapp",
            "baseUrl": "https://a.example.com",
            "sharedSecretArn": "arn:secret",
            "credentialProviderArn": "arn:provider",
        }
        path = self._write({"gatewayIdentifier": "gw-1", "applications": [entry, dict(entry)]})

        with self.assertRaises(ValueError) as caught:
            Manifest.load(path)
        self.assertIn("duplicate", str(caught.exception))

    def test_rejects_an_empty_application_list(self):
        path = self._write({"gatewayIdentifier": "gw-1", "applications": []})
        with self.assertRaises(ValueError):
            Manifest.load(path)


if __name__ == "__main__":
    unittest.main()
