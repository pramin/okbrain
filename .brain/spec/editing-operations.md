---
type: Specification
title: Editing operations
description: MCP-only editing operations and their contracts.
tags: [okbrain, spec]
---
# Editing operations

MCP only, served by `okbrain serve`:

| Operation | Contract |
|---|---|
| Read | Return the file's raw text and available content diagnostics. |
| Create | Write a new file; reject an existing destination. |
| Replace | Replace the whole content of an existing file. |
| Exact-text edit | Replace one exact text match; reject absent or ambiguous matches. |
| Delete | Delete the named file and unlink its local inbound links, as command-line delete. Reports touched files. Updates affected index listings. |
| Move | Move an existing file to an absent destination; never overwrite. Retargets inbound links and adjusts the moved file's own relative links, as command-line move. Reports touched files. Updates affected index listings. |
| Append | Add text to the end of an existing file. |
| Prepend | Add text to the start of an existing file, after any frontmatter. |
| Insert | Insert text before or after one exact anchor text match; reject absent or ambiguous anchors. |
| Line-range edit | Replace a 1-based inclusive line range, guarded by the expected current text of that range. |
| Multi-edit | Apply an ordered list of exact-text edits to one file atomically; any failure changes nothing. |
| Frontmatter set | Set or add one metadata key in a file's frontmatter, preserving other keys and formatting. |
| Frontmatter remove | Remove one metadata key from a file's frontmatter. |
| Copy | Copy an existing file to an absent destination; never overwrite. |
| Make directory | Create a bundle-relative directory. |
| Remove directory | Remove an empty directory. |

File operations use bundle-relative Markdown paths, including reserved indexes
and logs. Prevent escapes outside the bundle. Content is UTF-8 Markdown. Every
editing operation passes through the validation gate below. Do not
automatically rewrite links, metadata or authored formatting beyond what the
operation names; delete and move name link handling.
