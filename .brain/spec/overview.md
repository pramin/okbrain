---
type: Specification
title: OKBrain specification
description: "Overview, license, purpose and roles of OKBrain, with links to the detailed spec documents."
tags: [okbrain, spec]
---
# OKBrain specification

This specification supersedes the earlier `.brain` draft.

## License

OKBrain is licensed under the Apache License 2.0 (Apache-2.0), matching the
[upstream OKF specification](../okf/README.md). Source files carry no conflicting license, and
dependencies MUST be Apache-2.0-compatible.

## Purpose

Provide a .NET tool (a `dotnet tool`, installed with `dotnet tool install`) for
working with [Open Knowledge Format (OKF)](../okf/SPEC.md) bundles through its commands and an
HTTP MCP server (`okbrain serve`), with shared behavior for shared operations.

Bundles remain ordinary Markdown files with OKF metadata. Standard editors and
file tools remain supported; there is no proprietary storage or database.
Operations use current filesystem content, so external edits are visible without
reindexing or restarting the server.

## Roles

- **Commands:** validating and managing any OKF bundle (repo): bundle-wide
  operations such as validate, delete and move (the most used), plus stats, search
  and reindex. Editing file content is done with standard agent and editor
  tools (Codex, Claude, Copilot, any text editor); the commands have no content-editing
  operations.
- **`okbrain serve`:** serves the common shared brain over HTTP MCP. The common
  brain lives at `~/.brain` and is a git repository (`~/.brain/.git`).

The commands and `serve` share validate, search, stats and reindex. The editing
operations and the write gate exist for the common brain, through MCP.

## Documents

- [Commands](commands.md) - Commands of the brain tool: validate, search, stats, reindex, graph, delete, move, serve.
- [Editing operations](editing-operations.md) - MCP-only editing operations and their contracts.
- [HTTP MCP](http-mcp.md) - okbrain serve: Streamable HTTP MCP server scoped to one bundle.
- [Graph](graph.md) - Read-only knowledge graph derived from links; typed relations in link titles.
- [Git](git.md) - How the common brain relates to git.
- [Validation and writes](validation-and-writes.md) - The validation gate every write passes through.
- [Results and acceptance](results-and-acceptance.md) - Result shapes and acceptance examples.
