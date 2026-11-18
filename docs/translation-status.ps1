# Draws docs/images/translation-status.svg from the gStr resx files: for every language of the AgOpenGPS language menu,
# how many of the program's texts are translated. Run it again after the translations change:
#   powershell -ExecutionPolicy Bypass -File docs\translation-status.ps1
$root = Split-Path -Parent $PSScriptRoot
$res = Join-Path $root 'SourceCode\AgOpenGPS.Core\Translations'
$out = Join-Path $PSScriptRoot 'images\translation-status.svg'

# the languages of the language menu (SourceCode/GPS/Forms/Controls.Designer.cs), in menu order
$languages = @(
    @('bg', 'Български'), @('cs', 'Čeština'), @('da', 'Dansk'), @('de', 'Deutsch'), @('et', 'Eesti'),
    @('es', 'Español'), @('fr', 'Français'), @('hr', 'Hrvatski'), @('it', 'Italiano'), @('lv', 'Latviski'),
    @('lt', 'Lietuvių'), @('hu', 'Magyar'), @('nl', 'Nederlands'), @('no', 'Norsk'), @('pl', 'Polski'),
    @('pt', 'Português'), @('ro', 'Română'), @('ru', 'Русский'), @('fi', 'Suomi'), @('sk', 'Slovenčina'),
    @('sr', 'Srpski'), @('sv', 'Svenska'), @('tr', 'Türkçe'), @('uk', 'Українська'), @('zh-CHS', '中文 (简体)'),
    @('ja', '日本語'), @('ko', '한국어')
)

function Read-Resx($path) {
    $h = @{}
    [xml]$x = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    foreach ($d in $x.root.data) { if ($d.value -ne $null -and -not $d.type) { $h[$d.name] = [string]$d.value } }
    $h
}
function Esc([string]$s) { $s.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;') }

$en = Read-Resx (Join-Path $res 'gStr.resx')
$total = $en.Count
$rows = foreach ($l in $languages) {
    $loc = Read-Resx (Join-Path $res "gStr.$($l[0]).resx")
    $done = @($en.Keys | Where-Object { $loc.ContainsKey($_) -and -not [string]::IsNullOrWhiteSpace($loc[$_]) }).Count
    [pscustomobject]@{ Code = $l[0]; Name = $l[1]; Done = $done; Percent = [Math]::Floor(1000.0 * $done / $total) / 10 }
}

# two columns of rows
$perColumn = [Math]::Ceiling($rows.Count / 2)
$rowH = 26; $top = 64; $colW = 380; $barX = 150; $barW = 150
$width = 2 * $colW + 20
$height = $top + $perColumn * $rowH + 16
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine("<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$width`" height=`"$height`" viewBox=`"0 0 $width $height`" role=`"img`" aria-label=`"AgOpenGPS translation status`">")
[void]$sb.AppendLine('  <style>')
[void]$sb.AppendLine('    text { font-family: -apple-system, "Segoe UI", Helvetica, Arial, sans-serif; fill: #1f2328; }')
[void]$sb.AppendLine('    .title { font-size: 18px; font-weight: 600; } .sub { font-size: 12px; fill: #59636e; }')
[void]$sb.AppendLine('    .name { font-size: 13px; } .code { font-size: 11px; fill: #59636e; } .pct { font-size: 12px; font-weight: 600; }')
[void]$sb.AppendLine('    .track { fill: #e5e8eb; } .full { fill: #2da44e; } .part { fill: #d4a72c; } .low { fill: #cf222e; }')
[void]$sb.AppendLine('    @media (prefers-color-scheme: dark) {')
[void]$sb.AppendLine('      text { fill: #e6edf3; } .sub, .code { fill: #9198a1; } .track { fill: #30363d; }')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('  </style>')
[void]$sb.AppendLine("  <text class=`"title`" x=`"10`" y=`"28`">Translation status</text>")
$complete = @($rows | Where-Object { $_.Done -eq $total }).Count
[void]$sb.AppendLine("  <text class=`"sub`" x=`"10`" y=`"48`">$($rows.Count) languages, $total texts each (main program and helper apps) — $complete complete</text>")
for ($i = 0; $i -lt $rows.Count; $i++) {
    $r = $rows[$i]
    $x = 10 + [Math]::Floor($i / $perColumn) * $colW
    $y = $top + ($i % $perColumn) * $rowH
    $cls = if ($r.Percent -ge 100) { 'full' } elseif ($r.Percent -ge 50) { 'part' } else { 'low' }
    $fill = [Math]::Round($barW * $r.Percent / 100)
    [void]$sb.AppendLine("  <text class=`"name`" x=`"$x`" y=`"$($y + 15)`">$(Esc $r.Name)</text>")
    [void]$sb.AppendLine("  <text class=`"code`" x=`"$($x + 110)`" y=`"$($y + 15)`">$(Esc $r.Code)</text>")
    [void]$sb.AppendLine("  <rect class=`"track`" x=`"$($x + $barX)`" y=`"$($y + 5)`" width=`"$barW`" height=`"12`" rx=`"6`"/>")
    if ($fill -gt 0) { [void]$sb.AppendLine("  <rect class=`"$cls`" x=`"$($x + $barX)`" y=`"$($y + 5)`" width=`"$fill`" height=`"12`" rx=`"6`"/>") }
    [void]$sb.AppendLine("  <text class=`"pct`" x=`"$($x + $barX + $barW + 10)`" y=`"$($y + 15)`">$($r.Percent.ToString('0.#', [Globalization.CultureInfo]::InvariantCulture))%</text>")
}
[void]$sb.AppendLine('</svg>')
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
[IO.File]::WriteAllText($out, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
"$out : $($rows.Count) languages, $complete complete, $total texts"
