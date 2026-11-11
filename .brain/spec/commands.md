---
type: Specification
title: Commands
description: "Commands of the brain tool: create, validate, search, stats, reindex, graph, delete, move, serve."
tags: [okbrain, spec]
---
# Commands

```text
okbrain create <bundle> [--no-git]
okbrain validate <bundle> [--strict]
okbrain search <bundle> [--query <text>]
okbrain stats <bundle>
okbrain reindex <bundle>
okbrain graph <bundle> [--doc <doc>] [--depth <n>] [--relation <name>] [--findings]
okbrain delete <bundle> <doc>
okbrain move <bundle> <from> <to>
okbrain serve <address> [--bundle <path>]
```

The bundle path is required and positional for every command except `serve`;
`delete` also takes the document path, and `move` the source and destination
paths, as required positional arguments.
`serve` takes the listener address as its required positional argument and
serves `~/.brain` unless `--bundle` names another bundle; there is no implicit
listener address default. Other named options are optional. Missing required
arguments produce an input error; help prints usage and exits without starting
a server.

- **Create:** make a base brain at the given path: an `index.md` with OKF
  frontmatter, indexed and strictly valid, and a `.gitignore` that ignores `.git/`
  and the tool's temp files (written even with `--no-git`). The path must be absent or an empty
  directory; nothing existing is overwritten. Unless `--no-git` is given it also
  runs `git init` and makes an initial commit (if the commit fails, for example
  with no git identity configured, the files stay staged and the result says so).
  `okbrain create ~/.brain` sets up the common brain.
- **Validate:** report [OKF](../okf/SPEC.md) conformance and validation findings.
- **Search:** search current bundle content; the query is optional.
- **Stats:** summarize the bundle.
- **Reindex:** regenerate index listings while preserving authored prefixes and
  the existing root version. Change generated sections only, creating missing
  indexes where needed; do not rewrite concepts or logs.

- **Graph:** read-only knowledge graph queries; see Graph.
- **Delete:** delete the named bundle-relative Markdown document and deal with
  its local inbound links: each link to it is unlinked, keeping the link text
  and changing nothing else in the referencing file. Reports every file
  touched.

- **Move:** move an existing document to an absent destination; never
  overwrite. Deals with links: local inbound links are retargeted to the new
  path, and the moved document's own relative local links are adjusted so they
  still resolve. Only link targets change; text and formatting are preserved.
  Reports every file touched.

Delete and move keep the bundle sane, on the command line and in MCP alike: they share
one implementation. They also reindex: the index listings affected by the
deleted, source and destination paths are regenerated with reindex rules
(generated sections only; authored prefixes and the root version preserved).
They apply the validation gate to the proposed bundle including the link and
index changes, and are rejected with originals unchanged if they
still introduce new strict findings (for example an inbound link that cannot be
rewritten).

No other file-editing commands are provided.
