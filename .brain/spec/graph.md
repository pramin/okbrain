---
type: Specification
title: Graph
description: Read-only knowledge graph derived from links; typed relations in link titles.
tags: [okbrain, spec]
---
# Graph

The knowledge graph is derived, read-only, from current bundle content on every
call. Nothing is stored, so external edits are visible immediately. Nodes are
Markdown documents; edges are local Markdown links.

**Typed relations:** a link MAY carry a relation in the standard Markdown link
title: `[Old design](old.md "supersedes")`. A link without a title is an
untyped `links-to` edge. The relation vocabulary is open: any title is a valid
relation and the tool does not validate or restrict names. Relations live only
in the link, so there is no second copy to drift, standard editors and agents
write them naturally, and delete and move handle them with the link (unlink
removes the whole link, retarget keeps the title).

**Operations** (`okbrain graph` and MCP tools, same behavior):

| Operation | Contract |
|---|---|
| Backlinks | Documents linking to a document, with relation and location. |
| Neighbors | Documents within N hops of a document, in/out/both, optionally filtered by relation. |
| Findings | Orphans (no inbound or outbound links), dead ends and broken local links, distinguished from existing validation findings. |
| Export | The whole graph as nodes and edges (id, path, type, title; source, target, relation). |
| Relations | The relation names in use, with counts. |

Graph operations never write. The agent decides what to link and which relation
to use; the service only reports.
