---
type: Specification
title: Validation and writes
description: The validation gate every write passes through.
tags: [okbrain, spec]
---
# Validation and writes

Validate [OKF v0.2](../okf/SPEC.md) conformance and applicable optional metadata rules. Strict
validation also treats optional metadata and local broken-link findings as
failures. Unknown types and metadata keys remain allowed. Check local bundle
links only; leave external URLs unchecked.

Links are found with a real CommonMark parser, not pattern matching. It MUST
handle inline links, reference-style links and definitions, and all three title
forms (`"t"`, `'t'`, `(t)`), so validation, graph, delete and move agree on what
a link is.

Before any MCP mutation, or command-line delete, move or reindex, read the current bundle baseline and
build the complete proposed bundle view in memory. Include new, replaced, moved
and deleted paths when resolving links. Apply whole-bundle strict validation to
both views and reject newly introduced strict findings.

Unchanged preexisting findings do not block valid edits, and repairs remain
possible. Compare actual findings, not just totals or line numbers. A rejected
validation leaves all original files unchanged. Delete and move may be rejected
if link handling still leaves new findings. Command-line delete, move and reindex use the same write behavior.

Serialize MCP mutation validation and commit. Detect concurrent changes rather
than silently overwriting them. Report commit I/O failures honestly; do not claim
that a multi-file reindex is an all-or-nothing transaction.

The validation gate applies only to writes made through this tool. Standard
editors and file tools remain unmediated.
