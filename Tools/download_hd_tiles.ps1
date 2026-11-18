$ErrorActionPreference = "Continue"
$cache = "E:\UNITYproject\HK132CWIN7\Assets\Pictures\_hd_tile_cache"
$list = Join-Path $cache "tiles.txt"
$curl = "curl.exe"
$ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36"
$lines = Get-Content $list | Where-Object { $_.Trim() -ne "" }
$total = $lines.Count
$done = 0
$ok = 0
$t0 = Get-Date
foreach ($line in $lines) {
    $p = $line.Trim() -split "\s+"
    $z = $p[0]; $tx = $p[1]; $ty = $p[2]
    $out = Join-Path $cache "${z}_${tx}_${ty}.jpg"
    $done++
    if ((Test-Path $out) -and ((Get-Item $out).Length -gt 800)) {
        $ok++
        if ($done % 20 -eq 0 -or $done -eq $total) {
            $sec = ((Get-Date) - $t0).TotalSeconds
            Write-Host ("  {0}/{1} ok={2} {3:n1}s" -f $done, $total, $ok, $sec)
        }
        continue
    }
    $url = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/$z/$ty/$tx"
    $got = $false
    foreach ($try in 1..2) {
        if (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
        $proc = Start-Process -FilePath $curl -ArgumentList @("-sS","--connect-timeout","6","--max-time","10","-A",$ua,"-o",$out,$url) -PassThru -WindowStyle Hidden
        $deadline = (Get-Date).AddSeconds(11)
        while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 200
        }
        if (-not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 200
            & taskkill.exe /F /PID $proc.Id 2>$null | Out-Null
        }
        if ((Test-Path $out) -and ((Get-Item $out).Length -gt 800)) {
            $bytes = [System.IO.File]::ReadAllBytes($out)
            if ($bytes.Length -gt 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xD8) {
                $got = $true
                break
            }
        }
    }
    if ($got) { $ok++ }
    elseif (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
    if ($done % 20 -eq 0 -or $done -eq $total) {
        $sec = ((Get-Date) - $t0).TotalSeconds
        Write-Host ("  {0}/{1} ok={2} {3:n1}s" -f $done, $total, $ok, $sec)
    }
}
Write-Host ("DONE ok={0}/{1}" -f $ok, $total)
exit 0
