# Fills the landing page placeholders from the newest GitHub release and deploys it to Cloudflare Pages.
# Usage: pwsh site/deploy.ps1 [-Version 0.1.0]
param([string]$Version)
$ErrorActionPreference = "Stop"
$repo = "ReinaryuIlyen/kitsune"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = (gh release view --repo $repo --json tagName --jq .tagName).TrimStart("v") }
$asset = "Kitsune-$Version-win-x64.zip"
$url = "https://github.com/$repo/releases/download/v$Version/$asset"
$size = gh release view "v$Version" --repo $repo --json assets --jq ".assets[] | select(.name==\"$asset\") | .size"
$sizeText = "{0:F0} MB" -f ([double]$size / 1MB)
$sha = (gh release download "v$Version" --repo $repo --pattern "$asset.sha256" --output - ).Split(' ')[0]
New-Item -ItemType Directory -Force (Join-Path $root "site-build") | Out-Null
(Get-Content (Join-Path $PSScriptRoot "index.html") -Raw).
    Replace('__DOWNLOAD_URL__', $url).Replace('__VERSION__', $Version).Replace('__SIZE__', $sizeText).
    Replace('__SHA256__', $sha).Replace('__REPO_URL__', "https://github.com/$repo") |
    Set-Content (Join-Path $root "site-build\index.html") -NoNewline
Push-Location $root
try { npx --yes wrangler@latest pages deploy site-build --project-name kitsune-emulator --branch main --commit-dirty=true }
finally { Pop-Location }
