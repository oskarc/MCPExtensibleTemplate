<#
.SYNOPSIS
  Writes the claude.ai publish fragment for the MCP Hardening Field Guide.

.DESCRIPTION
  index.html is the source of truth and opens directly in a browser.
  claude.ai wraps a published page in its own <html>/<head>/<body>, so the page it
  needs is index.html without those wrappers. This script writes that page to
  publish/page.html (git-ignored). guide.css, guide.js and the ch-*.js files are
  published unchanged, under the same names, next to it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1
#>
$ErrorActionPreference = 'Stop'

$src    = Join-Path $PSScriptRoot 'index.html'
$outDir = Join-Path $PSScriptRoot 'publish'
$out    = Join-Path $outDir 'page.html'

$html = [IO.File]::ReadAllText($src)
$head = [regex]::Match($html, '(?s)<head>(.*?)</head>').Groups[1].Value
$body = [regex]::Match($html, '(?s)<body>(.*?)</body>').Groups[1].Value
if (-not $head.Trim() -or -not $body.Trim()) {
    throw 'index.html must contain a <head>...</head> and a <body>...</body> section.'
}

# The host skeleton already sets charset and viewport. Everything else in <head> is kept,
# with <title> first: the host reads the page name from the first 8 KB.
$head = [regex]::Replace($head, '(?im)^[ \t]*<meta\s+(charset|name="viewport")[^>]*>[ \t]*\r?\n', '')

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText($out, $head.Trim() + "`n" + $body.Trim() + "`n", $utf8NoBom)
Write-Host "Wrote $out"
