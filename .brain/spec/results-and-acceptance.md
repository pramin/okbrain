---
type: Specification
title: Results and acceptance
description: Result shapes and acceptance examples.
tags: [okbrain, spec]
---
# Results and acceptance

Return actionable structured results/errors, with file and location where
possible. Distinguish newly introduced findings from existing findings. Command
validation or write rejection returns a nonzero exit; MCP reports a tool error.

Acceptance examples:

- The command line and MCP produce equivalent validation findings for the same bundle.
- A malformed edit or new broken local link is rejected without changing originals.
- An unrelated existing finding does not block a valid edit or repair.
- Delete unlinks and move retargets inbound links and both refresh affected
  index listings (command line and MCP), reporting touched files, and are rejected if new findings would remain; create validates self-links
  against the proposed bundle.
- Exact-text edits reject missing/ambiguous matches; create/move never overwrite.
- External edits appear on the next operation without reindexing.
- Graph results reflect external edits on the next call, include relations from
  link titles, and the command line and MCP return equivalent results.
- Reindex preserves authored prefixes/root version; failures report partial writes (also for delete/move).
- `serve` requires the address, defaults to `~/.brain`, and works with supplied
  loopback and all-interface addresses. Integration tests connect, discover/call tools, check
  errors and cancellation, and stop the server cleanly.

This is a specification only; application implementation is a separate task.
