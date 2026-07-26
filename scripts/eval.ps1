<#
.SYNOPSIS
    Run SupportPilot's evaluation set and score correctness + groundedness.

.DESCRIPTION
    Reads docs/eval/testset.json (a fixed list of questions with known outcomes),
    sends each one through the running backend's GET /chat endpoint, parses the
    SSE stream (answer tokens plus the [CITATIONS] / [ESCALATE] sentinels), and
    scores two things per question:

      * Correctness  - does the answer contain the known facts (keywords)?  For
                       the unanswerable cases, "correct" means it ESCALATED
                       instead of guessing.
      * Groundedness - answerable questions must cite a source; unanswerable
                       questions must escalate. Either way the system is not
                       allowed to make something up.

    Prints a per-question table and an aggregate score, and writes a Markdown
    report to docs/eval/results.md (the table that goes in the README).

    This is the v1 harness: correctness is a deterministic keyword check - cheap,
    fast, and fully explainable. The LLM-as-judge upgrade is deferred to Day 19.

.PARAMETER ApiBase
    Base URL of the running SupportPilot backend. Default http://localhost:5254.

.PARAMETER TestSet
    Path to the test set JSON. Default docs/eval/testset.json (next to this repo).

.PARAMETER OutFile
    Where to write the Markdown report. Default docs/eval/results.md.

.PARAMETER TimeoutSec
    Per-question HTTP timeout. Local generation on a small GPU can be slow, so
    the default is generous (180s).

.EXAMPLE
    ./scripts/eval.ps1
    Run the full set against a locally running backend and write results.md.

.NOTES
    Prerequisites: Qdrant + Ollama up, the backend running, and the docs seeded
    (./scripts/seed-docs.ps1). Requires PowerShell 7+.
#>
[CmdletBinding()]
param(
    [string]$ApiBase = "http://localhost:5254",
    [string]$TestSet,
    [string]$OutFile,
    [int]$TimeoutSec = 180
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $TestSet) { $TestSet = Join-Path $repoRoot "docs/eval/testset.json" }
if (-not $OutFile) { $OutFile = Join-Path $repoRoot "docs/eval/results.md" }

if (-not (Test-Path $TestSet)) { throw "Test set not found: $TestSet" }

$suite = Get-Content $TestSet -Raw | ConvertFrom-Json
$cases = $suite.cases

# --- Send one question and parse the SSE stream into a flat result. ------------
# Returns: @{ answer=<string>; escalated=<bool>; citationCount=<int>; error=<string> }
function Invoke-Chat {
    param([string]$Question)

    $uri = "$ApiBase/chat?q=" + [uri]::EscapeDataString($Question)
    $raw = (Invoke-WebRequest -Uri $uri -TimeoutSec $TimeoutSec -UseBasicParsing).Content

    $answer = [System.Text.StringBuilder]::new()
    $escalated = $false
    $citationCount = 0
    $err = $null

    foreach ($line in ($raw -split "`n")) {
        if (-not $line.StartsWith("data:")) { continue }
        # Strip "data:" plus exactly ONE optional leading space (matches the SSE
        # spec and the frontend parser), preserving a token's own leading space.
        $payload = if ($line.StartsWith("data: ")) { $line.Substring(6) } else { $line.Substring(5) }

        if ($payload -eq "[DONE]") { break }
        if ($payload.StartsWith("[CITATIONS]")) {
            try { $citationCount = (($payload.Substring(11) | ConvertFrom-Json)).Count } catch {}
            continue
        }
        if ($payload.StartsWith("[ESCALATE]")) { $escalated = $true; continue }
        if ($payload.StartsWith("[error]")) { $err = $payload; continue }
        [void]$answer.Append($payload)
    }

    return @{
        answer        = $answer.ToString()
        escalated     = $escalated
        citationCount = $citationCount
        error         = $err
    }
}

# --- Score one case against its expected outcome. ------------------------------
function Test-Case {
    param($Case, $Result)

    $answerLower = $Result.answer.ToLowerInvariant()
    $refusedIdk = $answerLower.Contains("don't know") -or $answerLower.Contains("dont know")

    if ($Case.expect -eq "refuse") {
        # Not answerable from the docs. The only safe behaviour is to NOT fabricate:
        # either escalate to a human, or give a grounded "I don't know". Both pass;
        # a confident made-up answer fails.
        $safe = $Result.escalated -or $refusedIdk
        $correct = $safe
        $grounded = $safe
    }
    else {
        # Answerable: must contain the known fact(s) AND must not have escalated.
        $keywords = @($Case.keywords)
        if ($keywords.Count -eq 0) {
            $kwMatch = ($answerLower.Length -gt 0)
        }
        elseif ($Case.matchAll) {
            $kwMatch = -not ($keywords | Where-Object { -not $answerLower.Contains($_.ToLowerInvariant()) })
        }
        else {
            $kwMatch = [bool]($keywords | Where-Object { $answerLower.Contains($_.ToLowerInvariant()) })
        }
        $correct = $kwMatch -and (-not $Result.escalated)
        # Grounded = answered from a cited source (and didn't wrongly escalate).
        $grounded = ($Result.citationCount -gt 0) -and (-not $Result.escalated)
    }

    return @{ correct = [bool]$correct; grounded = [bool]$grounded }
}

# --- Run the suite. ------------------------------------------------------------
Write-Host "SupportPilot eval - $($cases.Count) case(s) against $ApiBase" -ForegroundColor Cyan
Write-Host ""

$rows = @()
$correctCount = 0
$groundedCount = 0

foreach ($case in $cases) {
    try {
        $result = Invoke-Chat -Question $case.question
    }
    catch {
        Write-Host "  ERROR  $($case.id): $($_.Exception.Message)" -ForegroundColor Red
        throw "Could not reach the backend. Is it running and are Qdrant/Ollama up? ($ApiBase)"
    }

    $score = Test-Case -Case $case -Result $result
    if ($score.correct) { $correctCount++ }
    if ($score.grounded) { $groundedCount++ }

    # How did the system actually behave? (for display)
    $refused = $result.answer.ToLowerInvariant().Contains("don't know") -or $result.answer.ToLowerInvariant().Contains("dont know")
    $behaviour =
        if ($result.escalated) { "escalated" }
        elseif ($refused) { "refused (I don't know)" }
        else { "answered ($($result.citationCount) cites)" }

    # Compact one-line-per-case console output.
    $mark = if ($score.correct -and $score.grounded) { "PASS" } else { "FAIL" }
    $color = if ($mark -eq "PASS") { "Green" } else { "Red" }
    Write-Host ("  {0}  {1,-26} {2}" -f $mark, $case.id, $behaviour) -ForegroundColor $color

    $rows += [pscustomobject]@{
        Id        = $case.id
        Question  = $case.question
        Expected  = if ($case.expect -eq "refuse") { "refuse (escalate / IDK)" } else { "answer + cite" }
        Behaviour = $behaviour
        Correct   = if ($score.correct) { "yes" } else { "NO" }
        Grounded  = if ($score.grounded) { "yes" } else { "NO" }
    }
}

$total = $cases.Count
$correctPct = [math]::Round(100.0 * $correctCount / $total, 1)
$groundedPct = [math]::Round(100.0 * $groundedCount / $total, 1)

Write-Host ""
Write-Host ("Correctness:  {0}/{1}  ({2}%)" -f $correctCount, $total, $correctPct) -ForegroundColor Cyan
Write-Host ("Groundedness: {0}/{1}  ({2}%)" -f $groundedCount, $total, $groundedPct) -ForegroundColor Cyan

# --- Write the Markdown report. ------------------------------------------------
$stamp = (Get-Date).ToString("yyyy-MM-dd HH:mm")
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# SupportPilot — Evaluation Results")
[void]$sb.AppendLine()
[void]$sb.AppendLine("_Generated by ``scripts/eval.ps1`` on $stamp. Model: ``llama3.2:3b`` (gen) + ``nomic-embed-text`` (embeddings)._")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Metric | Score |")
[void]$sb.AppendLine("|---|---|")
[void]$sb.AppendLine("| **Correctness** (answer contains known facts / correctly escalates) | **$correctCount / $total ($correctPct%)** |")
[void]$sb.AppendLine("| **Groundedness** (answered → cited a source; unanswerable → escalated) | **$groundedCount / $total ($groundedPct%)** |")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| # | Question | Expected | Behaviour | Correct | Grounded |")
[void]$sb.AppendLine("|---|---|---|---|---|---|")
$i = 1
foreach ($r in $rows) {
    [void]$sb.AppendLine("| $i | $($r.Question) | $($r.Expected) | $($r.Behaviour) | $($r.Correct) | $($r.Grounded) |")
    $i++
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("> Correctness is a deterministic keyword check (v1). The 3 unanswerable questions are correct only when the system **refuses to fabricate** — either escalating to a human or saying ""I don't know"" — which puts Day 10's low-confidence guardrail under test. An LLM-as-judge scorer is planned for Day 19.")

Set-Content -Path $OutFile -Value $sb.ToString() -Encoding UTF8
Write-Host ""
Write-Host "Wrote report -> $OutFile" -ForegroundColor DarkGray
