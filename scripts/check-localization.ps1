$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDesktop = Join-Path $taskRoot 'src/CFLDetectools.Desktop'
$taskCatalog = Get-Content -Raw -LiteralPath (Join-Path $taskDesktop 'Localization/chinese.json') | ConvertFrom-Json -AsHashtable
$taskRequiredKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

$taskXaml = Get-Content -Raw -LiteralPath (Join-Path $taskDesktop 'MainWindow.xaml')
foreach ($taskMatch in [regex]::Matches($taskXaml, "local:Loc Key='([^']+)'")) {
    [void]$taskRequiredKeys.Add([Net.WebUtility]::HtmlDecode($taskMatch.Groups[1].Value))
}
foreach ($taskFile in @('MainWindow.xaml.cs', 'LinePlot.cs')) {
    $taskSource = Get-Content -Raw -LiteralPath (Join-Path $taskDesktop $taskFile)
    foreach ($taskMatch in [regex]::Matches($taskSource, '(?:\bT|\bShowMessage|\.Translate)\("([^"\\]*)"')) {
        [void]$taskRequiredKeys.Add($taskMatch.Groups[1].Value)
    }
}
foreach ($taskKey in $taskRequiredKeys) {
    if (-not $taskCatalog.ContainsKey($taskKey)) { throw "Missing Chinese translation: $taskKey" }
}
foreach ($taskEntry in $taskCatalog.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($taskEntry.Value)) { throw "Empty translation: $($taskEntry.Key)" }
    $taskEnglishArgs = @([regex]::Matches($taskEntry.Key, '\{\d+(?:[^}]*)\}') | ForEach-Object Value | Sort-Object) -join '|'
    $taskChineseArgs = @([regex]::Matches($taskEntry.Value, '\{\d+(?:[^}]*)\}') | ForEach-Object Value | Sort-Object) -join '|'
    if ($taskEnglishArgs -ne $taskChineseArgs) { throw "Mismatched format placeholders: $($taskEntry.Key)" }
}
Write-Output "Localization validated: $($taskCatalog.Count) translations; $($taskRequiredKeys.Count) direct UI keys covered."
