# MCP Hardening Field Guide — source

The interactive guide to the design in [06-SECURITY-ROADMAP.md](../../06-SECURITY-ROADMAP.md).
Published copy: https://claude.ai/artifact/YDkjnGMGo6J6DSoXzD6dX4

## View it

Open `index.html` in a browser. There is no build step, no server and no package to install. Fonts load from Google Fonts and fall back to system fonts when offline.

## Files

| File | What it holds |
|------|---------------|
| `index.html` | The page shell. This is the source of truth. |
| `guide.css` | Theme tokens for light and dark, and every style. |
| `guide.js` | The framework: chapter registry, SVG builders, stepper, scenario player, SHA-256, page layout. |
| `ch-01-*.js` … `ch-07-*.js` | The chapters, in reading order. Each file calls `Guide.add()` once per chapter. |
| `build.ps1` | Writes `publish/page.html`, the version claude.ai needs. |

## Change it

- **Edit a chapter:** change its `ch-*.js` file and reload the page.
- **Add a chapter:** call `Guide.add({ id, part, title, lede, takeaway, refs, render(body) })` in the file where it belongs, or create a new `ch-*.js` and add its `<script>` tag to `index.html` in reading order. Chapters are numbered by load order, and parts are grouped in the order they first appear.
- **Keep it true:** the guide explains the roadmap. When the roadmap changes, change the guide in the same pull request.

## Republish to claude.ai

1. Run `powershell -ExecutionPolicy Bypass -File .\build.ps1`. It writes `publish/page.html`. (`Bypass` applies only to that one process; without it, a default Windows install refuses to run the script.)
2. Publish `publish/page.html` **to the existing URL above**, with `guide.css`, `guide.js` and every `ch-*.js` as supporting files under the same names. In Claude Code, ask to "republish the field guide to" that URL.

Publishing without the URL creates a separate page instead of updating this one.
