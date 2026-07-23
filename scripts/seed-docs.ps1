<#
.SYNOPSIS
    Rebuild SupportPilot's demo knowledge base from the files in sample-docs/.

.DESCRIPTION
    Ingests every .md / .txt / .pdf in sample-docs/ through the running backend's
    POST /ingest endpoint (extract -> chunk -> embed -> store in Qdrant).

    By default it first DELETES the Qdrant 'supportpilot_docs' collection so the
    seed is idempotent: run it as many times as you like and you always end up
    with exactly one copy of each document (no duplicate chunks / citations).
    The backend re-creates the collection automatically on the first ingest.

.PARAMETER ApiBase
    Base URL of the running SupportPilot backend. Default http://localhost:5254.

.PARAMETER QdrantBase
    Base URL of Qdrant (used only for the reset). Default http://localhost:6333.

.PARAMETER KeepExisting
    Skip the collection reset and just add the sample docs on top of whatever is
    already stored. Handy for topping up; omit it for a clean rebuild.

.EXAMPLE
    ./scripts/seed-docs.ps1
    Wipe the docs collection and ingest every file in sample-docs/.

.EXAMPLE
    ./scripts/seed-docs.ps1 -KeepExisting
    Ingest the sample docs without clearing what's already there.

.NOTES
    Prerequisites: Qdrant (Docker) and Ollama up, and the backend running
    (dotnet run in backend/SupportPilot.Api). Requires PowerShell 7+ for -Form.
#>
[CmdletBinding()]
param(
    [string]$ApiBase = "http://localhost:5254",
    [string]$QdrantBase = "http://localhost:6333",
    [switch]$KeepExisting
)

$ErrorActionPreference = "Stop"
$collection = "supportpilot_docs"

# sample-docs/ lives next to this script's parent (repo root).
$repoRoot = Split-Path -Parent $PSScriptRoot
$docsDir = Join-Path $repoRoot "sample-docs"

if (-not (Test-Path $docsDir)) {
    throw "Could not find sample-docs/ at $docsDir"
}

# 1. RESET (unless -KeepExisting): drop the collection so we don't stack duplicate
#    chunks on top of a previous run. A 404 just means it didn't exist yet — fine.
if (-not $KeepExisting) {
    Write-Host "Resetting Qdrant collection '$collection'..." -ForegroundColor Cyan
    try {
        Invoke-RestMethod -Uri "$QdrantBase/collections/$collection" -Method Delete | Out-Null
        Write-Host "  collection cleared." -ForegroundColor DarkGray
    }
    catch {
        Write-Host "  nothing to clear (collection did not exist)." -ForegroundColor DarkGray
    }
}

# 2. INGEST every supported file. -Form builds the multipart/form-data body that
#    the [FromForm] IFormFile 'file' parameter on POST /ingest expects.
$files = Get-ChildItem -Path $docsDir -File |
    Where-Object { $_.Extension -in ".md", ".txt", ".pdf" } |
    Sort-Object Name

if ($files.Count -eq 0) {
    throw "No .md/.txt/.pdf files found in $docsDir"
}

Write-Host "Ingesting $($files.Count) document(s) from sample-docs/..." -ForegroundColor Cyan

$totalChunks = 0
foreach ($file in $files) {
    try {
        $result = Invoke-RestMethod -Uri "$ApiBase/ingest" -Method Post -Form @{
            file = Get-Item $file.FullName
        }
        $totalChunks += $result.chunks
        Write-Host ("  {0,-34} {1,3} chunk(s), {2} page(s)" -f $file.Name, $result.chunks, $result.pages) -ForegroundColor Green
    }
    catch {
        Write-Host "  FAILED: $($file.Name) -> $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

Write-Host ""
Write-Host "Done. Seeded $($files.Count) document(s), $totalChunks chunk(s) into '$collection'." -ForegroundColor Cyan
Write-Host "Try it: curl -N `"$ApiBase/chat?q=how+long+do+refunds+take`"" -ForegroundColor DarkGray
