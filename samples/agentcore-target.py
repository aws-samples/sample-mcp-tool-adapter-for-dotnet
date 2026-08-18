# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: MIT-0

# -------------------------------------------------------------------------------------------------
# Sample: register an McpToolAdapter endpoint as an Amazon Bedrock AgentCore Gateway
# OpenAPI target.
#
# Run once per application you are exposing. Everything here is control-plane setup; no change to
# the .NET application is needed beyond enabling its endpoint.
# -------------------------------------------------------------------------------------------------

import json
import os
import urllib.parse
import urllib.request

import boto3

REGION = os.environ.get("AWS_REGION", "us-east-1")
GATEWAY_ID = os.environ.get("MCP_GATEWAY_ID", "your-gateway-id")

# Keep this SHORT. AgentCore prefixes every tool as "<TARGET_NAME>___<operationId>", and that whole
# string must fit the model's tool-name limit (64 characters for Anthropic and Bedrock). A 30-character
# target name leaves only 31 for the operation.
TARGET_NAME = "orderapp"

APP_BASE_URL = os.environ.get("MCP_BASE_URL", "https://orders.internal.example.com")

# Must match mcp:sharedSecret in the application's web.config.
#
# Read from the environment with no default on purpose. A placeholder literal here would be one edit
# away from a real secret in version control, and a sample that invites you to paste a credential into
# source is teaching the wrong habit. Export it from wherever you actually keep secrets:
#
#   export MCP_SHARED_SECRET=$(aws secretsmanager get-secret-value \
#       --secret-id orderapp/mcp --query SecretString --output text)
SHARED_SECRET = os.environ.get("MCP_SHARED_SECRET", "")

if not SHARED_SECRET:
    raise SystemExit(
        "Set MCP_SHARED_SECRET to the value of the application's mcp:sharedSecret before running this."
    )

control = boto3.client("bedrock-agentcore-control", region_name=REGION)


# 1. Fetch the OpenAPI document the application already serves.
#
#    The application generates this from its ToolRegistry, so it cannot drift from the code. Its
#    health endpoint reports any AgentCore compatibility problems it knows about — check that first
#    if target creation fails.
def fetch_openapi_document() -> dict:
    url = f"{APP_BASE_URL}/_mcp/openapi.json"

    # Check the scheme before opening it. urlopen honours file:// and other schemes, so a base URL that
    # came from configuration rather than from a person is a way to make this read a local file and send
    # its contents onward. Both here and in automation/agentcore_reconcile.py.
    scheme = urllib.parse.urlsplit(url).scheme.lower()
    if scheme != "https":
        raise SystemExit(
            f"MCP_BASE_URL must be an https URL; got scheme '{scheme}'. The shared secret travels on "
            f"every call, so it must not go over plain HTTP."
        )

    request = urllib.request.Request(url, headers={"X-Mcp-Key": SHARED_SECRET})
    with urllib.request.urlopen(request) as response:  # nosec B310 - scheme verified https above
        return json.loads(response.read())


# 2. Store the shared secret as an AgentCore Identity API key credential provider.
#
#    Outbound options for an OpenAPI target are API key, OAuth, or IAM. IAM (SigV4) is ruled out
#    here: it requires a target that natively verifies SigV4 — API Gateway, Lambda function URLs, or
#    another AgentCore Gateway — and IIS behind a load balancer does not. That leaves API key or
#    OAuth.
#
#    API key is used below because it is the simplest thing that matches what the .NET endpoint can
#    actually verify today. If you need the calling user's identity to reach the application, use
#    OAUTH instead: grantType CLIENT_CREDENTIALS for machine-to-machine, AUTHORIZATION_CODE for 3LO,
#    or TOKEN_EXCHANGE for on-behalf-of propagation, which preserves the original user's `sub` across
#    hops. Note that requires an IMcpAuthorizer on the .NET side that validates the incoming JWT —
#    McpToolAdapter does not ship one. See "End-user identity" in the README.
def create_credential_provider() -> str:
    response = control.create_api_key_credential_provider(
        name=f"{TARGET_NAME}-mcp-key",
        apiKey=SHARED_SECRET,
    )
    return response["credentialProviderArn"]


# 3. Create the OpenAPI target.
#
#    The document deliberately declares no securitySchemes: AgentCore does not support
#    specification-level security and the credential is injected from the configuration below
#    instead. credentialParameterName must match the header the application checks.
def create_target(openapi_document: dict, credential_provider_arn: str) -> dict:
    return control.create_gateway_target(
        gatewayIdentifier=GATEWAY_ID,
        name=TARGET_NAME,
        description="Order management operations exposed from the existing WebForms application",
        targetConfiguration={
            "mcp": {
                "openApiSchema": {
                    # Inline for a document this size. For larger ones, upload to S3 and use
                    # {"s3": {"uri": "s3://bucket/key", "bucketOwnerAccountId": "111122223333"}}.
                    "inlinePayload": json.dumps(openapi_document)
                }
            }
        },
        credentialProviderConfigurations=[
            {
                "credentialProviderType": "API_KEY",
                "credentialProvider": {
                    "apiKeyCredentialProvider": {
                        "providerArn": credential_provider_arn,
                        "credentialLocation": "HEADER",
                        "credentialParameterName": "X-Mcp-Key",
                    }
                },
            }
        ],
    )


if __name__ == "__main__":
    document = fetch_openapi_document()

    operations = [
        operation["post"]["operationId"]
        for operation in document["paths"].values()
        if "post" in operation
    ]
    print(f"{len(operations)} operation(s) in the document:")
    for operation_id in operations:
        visible = f"{TARGET_NAME}___{operation_id}"
        flag = "  <-- OVER 64 CHARACTERS, WILL FAIL AT INVOCATION" if len(visible) > 64 else ""
        print(f"  {visible}  ({len(visible)} chars){flag}")

    provider_arn = create_credential_provider()
    print(f"credential provider: {provider_arn}")

    target = create_target(document, provider_arn)
    print(f"target created: {target['targetId']}")
    print("Tools appear to agents as: " + ", ".join(f"{TARGET_NAME}___{o}" for o in operations))
