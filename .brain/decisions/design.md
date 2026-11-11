---
type: Decision
title: Design decisions
description: Why OKBrain works the way it does; the non-obvious choices behind the spec.
tags: [okbrain, decisions]
---
# Design decisions

- **A dotnet tool, not a CLI app.** Installed with `dotnet tool install`; command
  `okbrain`, package `OKBrain`. Licensed Apache-2.0 to match upstream OKF.
- **Editing stays with standard tools.** Codex, Claude, Copilot and editors edit
  content directly. Special handling exists only for the common brain: the write
  gate and the MCP editing operations. See [editing operations](../spec/editing-operations.md).
- **The write gate compares findings, not counts.** An edit is rejected only for
  findings it newly introduces, so existing problems never block a repair. See
  [validation and writes](../spec/validation-and-writes.md).
- **Links are the graph.** Relations live in the Markdown link title, so there is
  no second copy to drift. See [graph](../spec/graph.md).
- **Writes are safe but not transactional.** Commits pin the bundle root, refuse
  symlink escapes and write temp-then-rename. A small race between the final
  comparison and the rename is accepted and documented, and a multi-file commit
  can partly succeed; the tool reports that honestly.
- **One server, one common brain; projects are per session.** `okbrain serve`
  serves `~/.brain`. A session adds its project `.brain/` with `set_project`;
  reads check the project first, then the common brain, and writes must name a
  `scope`. See [HTTP MCP](../spec/http-mcp.md).
- **`okbrain create` makes a base brain.** A valid `index.md`, a `.gitignore`
  that ignores `.git/` and the tool's temp files, then `git init` and an initial
  commit unless `--no-git`. See [commands](../spec/commands.md).
- **Git history is the audit trail** for the common brain; the tool never
  rewrites it. See [git](../spec/git.md).
- **The upstream OKF spec lives in the brain too**, with its Apache-2.0 license
  and provenance. See [the vendored spec](../okf/README.md).
- **The spec is itself a brain.** `.brain/` here is the specification, checked
  with `okbrain validate .brain --strict`.

## Ideas not built

- Comments or amendments that supersede part of a document, or add information
  without a full rewrite.
- A single server addressing many projects without `set_project`.
