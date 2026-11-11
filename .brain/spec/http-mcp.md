---
type: Specification
title: HTTP MCP
description: "okbrain serve: Streamable HTTP MCP server scoped to one bundle."
tags: [okbrain, spec]
---
# HTTP MCP

`okbrain serve <address>` starts a standard Streamable HTTP MCP server bound to
the supplied address and scoped to one bundle (default `~/.brain`). It exposes
validate, search, stats, reindex and every editing operation.

```text
okbrain serve http://127.0.0.1:8080
okbrain serve http://0.0.0.0:8080
okbrain serve http://[::1]:8080
okbrain serve http://127.0.0.1:8080 --bundle /path/to/other-bundle
```

Support loopback, explicit interface/host binding, all interfaces and an explicit
port using normal HTTP hosting address semantics. Report invalid addresses and
startup binding failures. HTTP transport does not itself mean public hosting.

## Project brain per session

The server's bundle is the common brain. A session MAY add a project brain with
the `set_project` tool, which takes a project directory containing `.brain/` (or
the brain root itself, e.g. `/path/to/project/.brain`) and applies to that session only; other sessions are
unaffected. `get_project` reports it.

- With a project brain set, `read` checks the project brain first and falls back
  to the common brain when the document is absent, and `search` returns hits
  from both, project first. Each result names its `source` (`project` or
  `common`).
- Every tool accepts an optional `scope` (`project` or `common`) to address one
  brain. Read-only tools default to the project brain when one is set.
- Writes MUST name a `scope` once a project brain is set, so an edit cannot land
  in the wrong brain; without a project brain they go to the common brain.
- The project brain is held in memory for the life of the server and is not
  persisted.
