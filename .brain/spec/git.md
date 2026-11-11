---
type: Specification
title: Git
description: How the common brain relates to git.
tags: [okbrain, spec]
---
# Git

The common brain `~/.brain` is a git repository. The tool does not require git
for other bundles. Git history is the audit trail for the common brain; the
tool's writes are plain file changes that git sees like any other edit, and
the tool does not rewrite or discard git history. If the default bundle is
missing, `serve` reports it rather than creating it silently.
