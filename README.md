# OKBrain

OKBrain reads [Open Knowledge Format v0.2](.brain/okf/SPEC.md) bundles from
ordinary Markdown files. It ships as a .NET tool (`okbrain`) with the `validate`,
`search`, `stats`, `reindex`, `graph`, `delete`, `move` and `serve` commands
described in [the spec](.brain/spec/overview.md), which is itself an OKF bundle
in [`.brain/`](.brain/) that you can check with `okbrain validate .brain --strict`. Each operation reads the bundle again, so edits made by
other programs are visible on the next call.

## Install

.NET 10 SDK is required.

```sh
dotnet tool install --global OKBrain
```

Source: <https://github.com/pramin/okbrain>. Licensed under Apache-2.0.

## Build from source

From the repository root:

```sh
dotnet test
dotnet pack src/OKBrain.Tool/OKBrain.Tool.csproj -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts OKBrain
```

This installs the `okbrain` command globally. For a local directory instead, use
`--tool-path ./.tools` and run `./.tools/okbrain`:

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
okbrain --help
```

All results are JSON. Validation reports each finding's rule, bundle-relative
file, one-based line and column, message, and kind. A normal validation fails
for conformance findings; `--strict` also fails for optional metadata and
broken local Markdown links. External URLs are not checked. Validation and
bundle read failures exit 1, invalid arguments exit 2, and successful commands
exit 0. A missing bundle path is always an argument error.

Reindex adds or refreshes sections delimited by `<!-- okbrain:generated -->`
and `<!-- /okbrain:generated -->` in directory indexes, preserving text outside
those markers. It compares strict validation findings before writing and reports
files written if a commit fails partway through. On Linux, writes use directory
handles to anchor destination lookups. A non-cooperating process can still change
a target or move its parent between the final comparison and rename. On other
platforms, writes use path-based checks that refuse symbolic-link components and
recheck containment and the baseline before writing; those checks have weaker
race protection because paths can change between verification and use.

Delete and move propose link and index changes through the strict validation gate.
Move refuses an existing destination. Both report touched files, including affected indexes. Delete preserves link labels;
move preserves link titles and adjusts relative links in the moved document.
Graph without options exports nodes, edges, and relation counts. With `--doc`, it
returns backlinks and neighbors in both directions, up to `--depth` hops (default
1), optionally filtered by `--relation`. `--findings` reports orphans, dead ends,
and broken local links separately from validation findings. Link titles provide
relation names; links without titles use `links-to`. The core also exposes
directional neighbor queries via `BrainGraph.Neighbors`.

## Creating a brain

`okbrain create <path>` makes a base brain (a valid `index.md`, plus `git init` and an
initial commit unless `--no-git`). `okbrain create ~/.brain` sets up the common brain.

## MCP server

A session can add a project brain: call the `set_project` tool with a project
directory (containing `.brain/`). Then `read` and `search` look in the project
brain first and fall back to the common brain, results name their `source`, and
writes must pass `scope` (`project` or `common`).

Start the Streamable HTTP MCP server with an explicit listener address:

```sh
okbrain serve http://127.0.0.1:8080
okbrain serve http://127.0.0.1:8080 --bundle /path/to/bundle
```

The MCP endpoint is the listener's root URL. Without `--bundle`, it reads
`~/.brain`, which must already exist. Listener addresses may use an explicit
interface such as `0.0.0.0` or an IPv6 address such as `[::1]`. The server
exposes validation, search, stats, reindex, graph queries and the editing
operations in [the spec](.brain/spec/overview.md). Tool results contain JSON; write rejections
are MCP tool errors with the new findings. All writes use the shared strict
validation gate. `frontmatter_set` accepts a YAML value, and `line_edit`'s
expected text includes the selected lines' line endings.

Search is case-insensitive and returns the first matching location per
Markdown file; without a query it lists all Markdown documents. Stats count
concepts, reserved indexes and logs, and the types and tags on concepts. The
core library exposes `Brain.LoadBundle`, `Brain.Validate`, `Brain.Search`, and
`Brain.Stats`; the loaded document exposes recognized concept fields and the
original YAML metadata, including producer-defined keys. The `.git` directory
is excluded; symbolic links within the bundle are followed, while paths that
escape the bundle or create directory cycles are reported as errors.

The application and vendored upstream specification are Apache-2.0 licensed.
The runtime dependencies include YamlDotNet (MIT), Markdig (BSD-2-Clause), and the official ModelContextProtocol C# SDK (MIT).
