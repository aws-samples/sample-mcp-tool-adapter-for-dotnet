// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

export interface DumpOpenApiDocumentProps {
  /** Path to the .csproj of an application hosting the adapter. */
  readonly projectPath: string;

  /** Where to write the document. Created if its directory does not exist. */
  readonly outputPath: string;

  /** Skip regeneration when the file is already newer than every source file. Defaults to true. */
  readonly cache?: boolean;

  /**
   * Directories whose contents invalidate the cached document. Defaults to the project's own directory.
   *
   * Include the adapter's source as well: the document is generated from the schema generator and the
   * tool registry together, so a change to either should regenerate it. Watching only the sample would
   * leave a stale document after an adapter change — the worst kind of caching bug, because it deploys
   * quietly.
   */
  readonly watchPaths?: string[];
}

/**
 * Generates the application's OpenAPI document during synth and returns the path it was written to.
 *
 * This closes the ordering problem that otherwise forces a manual step. AgentCore needs the document at
 * synth time, but the document is a product of the application's own `ToolRegistry`, so the honest
 * source is the application itself — not a copy in this repository that drifts the moment someone adds a
 * tool. Asking the operator to start the app and curl `/_mcp/openapi.json` works, but it is a step that
 * can be skipped or done against the wrong build.
 *
 * So the application exposes `--dump-openapi <path>`, which runs the same code that serves the endpoint
 * and writes the result without binding a port. Synth invokes it, exactly as it invokes `dotnet publish`
 * to build the Lambda package. The document therefore cannot disagree with the code being deployed.
 *
 * The server URL is not this function's problem: it is a deploy-time value, so the stack overrides
 * `servers[0].url` with a CDK token.
 */
export function dumpOpenApiDocument(props: DumpOpenApiDocumentProps): string {
  const output = path.resolve(props.outputPath);
  const project = path.resolve(props.projectPath);

  if (!fs.existsSync(project)) {
    throw new Error(`Cannot generate an OpenAPI document: no project at ${project}`);
  }

  const watchPaths = (props.watchPaths ?? [path.dirname(project)]).map((p) => path.resolve(p));

  if ((props.cache ?? true) && isUpToDate(output, watchPaths)) {
    return output;
  }

  fs.mkdirSync(path.dirname(output), { recursive: true });

  try {
    execFileSync(
      'dotnet',
      ['run', '--project', project, '--configuration', 'Release', '--', '--dump-openapi', output],
      // Inherited so a build failure is readable in the synth output rather than swallowed.
      { stdio: ['ignore', 'inherit', 'inherit'] },
    );
  } catch (error) {
    throw new Error(
      `Generating the OpenAPI document failed. Run this to see why:\n` +
        `  dotnet run --project ${project} -- --dump-openapi ${output}\n` +
        `${(error as Error).message}`,
    );
  }

  if (!fs.existsSync(output)) {
    throw new Error(`The dump command reported success but wrote nothing to ${output}`);
  }

  return output;
}

/**
 * True when the document is newer than every source file, so regenerating it would be a no-op.
 *
 * Worth the few lines: without it every `cdk synth`, `cdk diff` and `cdk deploy` pays for a full .NET
 * build of the sample, which makes the CDK feel broken rather than careful.
 */
function isUpToDate(document: string, watchPaths: string[]): boolean {
  if (!fs.existsSync(document)) return false;

  const documentModified = fs.statSync(document).mtimeMs;

  const newestSource = (directory: string): number => {
    let newest = 0;

    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      // Build output, not input. Including it would make the document permanently stale.
      if (entry.name === 'bin' || entry.name === 'obj') continue;

      const candidate = path.join(directory, entry.name);
      newest = Math.max(newest, entry.isDirectory() ? newestSource(candidate) : fs.statSync(candidate).mtimeMs);
    }

    return newest;
  };

  return watchPaths.every((directory) => documentModified >= newestSource(directory));
}
