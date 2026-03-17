$ErrorActionPreference = "Stop"

$src = "C:\Users\Lalit Chitoshiya\Downloads\PIPES_EXPORT.csv"
$dst = "d:\WSPtoCIVIL\C3DPlugin\Pipe_Export_2_full.csv"

$rows = Import-Csv -Path $src

foreach ($r in $rows) {
    if ($null -ne $r.DIAMETER -and $r.DIAMETER -ne "") {
        $d = [double]$r.DIAMETER
        $r.DIAMETER = ([math]::Round($d / 50) * 50).ToString([Globalization.CultureInfo]::InvariantCulture)
    }
}

$rows | Export-Csv -Path $dst -NoTypeInformation -Encoding UTF8

Write-Host "Wrote: $dst"

