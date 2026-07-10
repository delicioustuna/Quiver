<#
.SYNOPSIS
    エージェント運用ガードレール: プロジェクト内トラック記号 (例 HYP-7 / ARCH-5b) や
    案A/案B が、許可地 (plans/・docs/design/) の外にあるコメント・識別子・公開 docs へ
    漏れていないかを検査する単一正本。

.DESCRIPTION
    設計方針は docs/design/development.md「エージェント運用ガードレール」節を正本とする。
    パターンヒットは verdict ではなく signal として扱い、呼び出し側は advisory に留める
    (ハードブロックしない)。許可地はパスで決定論的に除外し、誤検知の主因を消す。

    3 系統から同じロジックを呼べる:
      -Scan         src / tests / benchmarks / docs/spec + README を監査 (人間 / CI / Codex 用)。
                    候補があれば一覧して exit 1 (tests/benchmarks は既存ベースラインが多い点に注意)。
      -Hook         Claude Code の PostToolUse フックから stdin の JSON を受け取り、
                    その編集が新規追加したテキストのみ検査。検出時は stderr へ通知して exit 2
                    (advisory: 書き込みは既に完了。モデルへ助言を返すだけ)。
      <paths...>    指定ファイルを検査。違反があれば exit 1。

.NOTES
    トラック接頭辞の allowlist はここが正本。新トラック追加時は $MarkerPrefixes に足す。
    AVX-512 / CRC-32 / UTF-8 / IEEE-754 等の技術用語を誤検知しないよう、
    blanket な [A-Z]{2,4}-\d+ ではなく明示列挙にしている。
#>
[CmdletBinding()]
param(
    [switch]$Hook,
    [switch]$Scan,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Paths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- 検出定義 (allowlist が正本) ----------------------------------------------
$MarkerPrefixes = @(
    'ARCH', 'HYP', 'FTS', 'RAG', 'VEC', 'SIG', 'ZD', 'VP', 'CR',
    'BR', 'MT', 'WS', 'TS', 'PW', 'DOC', 'FT', 'GC', 'BA'
)
$TrackRegex  = [regex]('\b(' + ($MarkerPrefixes -join '|') + ')-\d+[a-z]?\b')
$AnsatzRegex = [regex]('案[A-Za-zＡ-Ｚ]')  # 案A / 案B / 案Ａ

# 許可地 / 対象外: ここでは記号を残してよい、または検査対象でない (正規化 / 区切りパスで判定)。
#   plans, docs/design         … 代替案定義の正当な置き場
#   scripts/agent-guardrails   … 本スクリプト自身 (接頭辞を data として保持)
#   .claude, .agents, .codex-* … エージェント内部ツール (skill 定義等でタスク番号は正当)
#   bin, obj, artifacts, .git  … ビルド出力・生成物 (走査コスト削減も兼ねる)
$ExcludedRegex = [regex]('(^|/)(plans|docs/design|scripts/agent-guardrails|\.claude|\.agents|\.codex-build-check|\.codex-temp|bin|obj|artifacts|\.git|\.vs)/')
$ScanExtensions = @('.cs', '.md')

# -Scan の対象ルート: 規約が対象とする「src / tests / benchmarks のコード + 公開 docs」に限定。
# 全ツリー走査は artifacts/ や tools/ の bin/obj でタイムアウトするため、ここで範囲を絞る。
$ScanRoots = @('src', 'tests', 'benchmarks', 'docs/spec')
$ScanRootFiles = @('README.md')

function Test-File {
    param([string]$Path)

    $findings = @()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $findings }

    try {
        $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8 -ErrorAction Stop)
    }
    catch {
        return $findings  # 読めない (バイナリ等) なら黙って諦める
    }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        foreach ($m in $TrackRegex.Matches($line))  { $findings += [pscustomobject]@{ Line = $i + 1; Marker = $m.Value; Text = $line.Trim() } }
        foreach ($m in $AnsatzRegex.Matches($line)) { $findings += [pscustomobject]@{ Line = $i + 1; Marker = $m.Value; Text = $line.Trim() } }
    }
    return $findings
}

function Get-NormalizedPath {
    param([string]$Path)
    return ($Path -replace '\\', '/')
}

function Test-Excluded {
    param([string]$NormalizedPath)
    return $ExcludedRegex.IsMatch($NormalizedPath)
}

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -ne $Object -and ($Object.PSObject.Properties.Name -contains $Name)) { return $Object.$Name }
    return $null
}

# --- モード: Hook (Claude Code PostToolUse) ------------------------------------
if ($Hook) {
    try {
        # stdin は必ず UTF-8 として読む。PS 5.1 の既定 InputEncoding は OEM/ANSI のことがあり、
        # Claude Code が UTF-8 で書く JSON 中の日本語 (案A/B 等) が化けて検知漏れするため。
        $reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), (New-Object System.Text.UTF8Encoding($false)))
        $raw = $reader.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
        $payload = $raw | ConvertFrom-Json
        $ti = Get-Prop $payload 'tool_input'
    }
    catch { exit 0 }  # 解釈できない入力でセッションを壊さない

    $fp = [string](Get-Prop $ti 'file_path')
    if ([string]::IsNullOrWhiteSpace($fp)) { exit 0 }
    if (Test-Excluded (Get-NormalizedPath $fp)) { exit 0 }

    # この編集が「新規に書き込んだテキスト」だけを検査する。ファイル全体を再検査すると
    # tests/benchmarks に既存する大量のマーカーを毎回再検知してしまい (誤検知の主因)、
    # 記事が警告する会話破綻を招く。差分に限定することで signal を変更部分へ絞る。
    $newText = ''
    $content = Get-Prop $ti 'content'
    $newStr  = Get-Prop $ti 'new_string'
    $edits   = Get-Prop $ti 'edits'
    if ($null -ne $content)     { $newText = [string]$content }
    elseif ($null -ne $newStr)  { $newText = [string]$newStr }
    elseif ($null -ne $edits)   { $newText = (($edits | ForEach-Object { [string](Get-Prop $_ 'new_string') }) -join "`n") }
    if ([string]::IsNullOrEmpty($newText)) { exit 0 }

    $markers = @()
    foreach ($m in $TrackRegex.Matches($newText))  { $markers += $m.Value }
    foreach ($m in $AnsatzRegex.Matches($newText)) { $markers += $m.Value }
    $markers = @($markers | Select-Object -Unique)
    if ($markers.Count -eq 0) { exit 0 }

    $msg = @(
        "[guardrail] $fp : この編集がトラック記号 / 案A・案B を新規追加した可能性 (advisory・誤検知の可能性あり)",
        "  検出: $($markers -join ', ')",
        "plans/・docs/design/ 以外の src コメント・識別子・公開 docs には残さない規約 (docs/design/development.md 参照)。",
        "正当な技術用語 (UTF-8 等) なら無視可。不要なら該当箇所から除去してください。"
    ) -join "`r`n"
    [Console]::Error.WriteLine($msg)
    exit 2  # PostToolUse: stderr をモデルへ返す (書き込みは既に完了・ブロックはしない)
}

# --- モード: Scan / Paths (人間 / CI / Codex) ----------------------------------
$targets = @()
if ($Scan) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $includeGlobs = $ScanExtensions | ForEach-Object { '*' + $_ }
    $collected = New-Object System.Collections.Generic.List[string]
    foreach ($rel in $ScanRoots) {
        $root = Join-Path $repoRoot $rel
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $root -Recurse -File -Include $includeGlobs -ErrorAction SilentlyContinue |
            Where-Object { -not (Test-Excluded (Get-NormalizedPath $_.FullName)) } |
            ForEach-Object { $collected.Add($_.FullName) }
    }
    foreach ($rel in $ScanRootFiles) {
        $f = Join-Path $repoRoot $rel
        if (Test-Path -LiteralPath $f -PathType Leaf) { $collected.Add($f) }
    }
    $targets = $collected.ToArray()
}
elseif ($Paths) {
    $targets = $Paths | Where-Object { -not (Test-Excluded (Get-NormalizedPath $_)) }
}
else {
    Write-Host "usage: check-track-markers.ps1 [-Scan] [-Hook] [<path> ...]"
    exit 2
}

$total = 0
foreach ($t in $targets) {
    $findings = @(Test-File -Path $t)
    if ($findings.Count -gt 0) {
        Write-Host $t -ForegroundColor Yellow
        foreach ($f in $findings) {
            Write-Host ("  L{0}: {1}  … {2}" -f $f.Line, $f.Marker, $f.Text)
            $total++
        }
    }
}

if ($total -gt 0) {
    Write-Host ""
    Write-Host "$total 件の候補。許可地 (plans/・docs/design/) 外の公開 docs / src コメント / 識別子から除去してください (正当な技術用語なら無視可)。"
    exit 1
}
Write-Host "OK: トラック記号 / 案A・案B の漏れは検出されませんでした。"
exit 0
