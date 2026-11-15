# Arranca el servidor puente de TecLink en ESTA PC, lo saca a internet con un tunel de Cloudflare (que solo hace conexiones hacia afuera,
# asi funciona detras de T-Mobile Home Internet u otros routers cerrados) y publica la direccion vigente en relay.json (repositorio publico),
# que es donde la app la busca sola. Sirve para dejarlo funcionando hoy; para algo permanente se sube el mismo servidor a Railway (ver Dockerfile).
#   Uso normal:       powershell -ExecutionPolicy Bypass -File iniciar-puente.ps1
#   Inicio con Windows:  ... -Registrar     (crea una tarea del usuario que lo arranca al iniciar sesion)
#   Quitar el inicio:    ... -Quitar
param([int]$Port = 18765, [switch]$Registrar, [switch]$Quitar)

$dir = Join-Path $env:LOCALAPPDATA 'TecLinkRelay'
$taskName = 'TecLink puente'
New-Item -ItemType Directory $dir -Force | Out-Null

if ($Quitar) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue; "Tarea quitada."; return }
if ($Registrar) {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Description 'Arranca el servidor puente de TecLink' -Force | Out-Null
    "Tarea creada: el puente arrancara solo cuando inicies sesion."; return
}

$env:DOTNET_ROLL_FORWARD = 'Major'; $env:PORT = "$Port"

# 1) el servidor puente (si no esta ya escuchando)
if (-not (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath dotnet -ArgumentList "`"$dir\app\TecLink.Relay.dll`"" -WorkingDirectory "$dir\app" -WindowStyle Hidden
    Start-Sleep 4
}

# 2) el tunel: solo se reinicia el MIO (el de otras carpetas, como el de jarvis, no se toca)
Get-CimInstance Win32_Process -Filter "Name='cloudflared.exe'" | Where-Object { $_.CommandLine -like '*TecLinkRelay*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep 2
'# configuracion propia del puente: vacia a proposito, para no mezclarse con otros tuneles' | Set-Content "$dir\relay-tunnel.yml"
Remove-Item "$dir\tunnel.log" -ErrorAction SilentlyContinue
Start-Process -FilePath "$dir\cloudflared.exe" -WindowStyle Hidden -ArgumentList 'tunnel', '--no-autoupdate', '--config', "$dir\relay-tunnel.yml", '--url', "http://127.0.0.1:$Port", '--logfile', "$dir\tunnel.log"
$url = $null
for ($i = 0; $i -lt 40 -and -not $url; $i++) {
    Start-Sleep 2
    if (Test-Path "$dir\tunnel.log") { $m = Select-String -Path "$dir\tunnel.log" -Pattern 'https://[a-z0-9-]+\.trycloudflare\.com' | Select-Object -First 1; if ($m) { $url = $m.Matches[0].Value } }
}
if (-not $url) { Write-Error 'No salio la direccion publica del tunel.'; return }
Set-Content "$dir\url.txt" $url
$hostName = ([Uri]$url).Host

# 3) esperar a que responda desde internet y publicar la direccion para que las apps la encuentren
for ($i = 0; $i -lt 20; $i++) { if ((curl.exe -s -m 10 -o NUL -w '%{http_code}' "$url/health") -eq '200') { break }; Start-Sleep 3 }
$repo = 'fuchogallery-droid/teclink'; $path = 'relay.json'
$body = (@{ host = $hostName; updated = (Get-Date).ToUniversalTime().ToString('s') + 'Z' } | ConvertTo-Json -Compress)
$sha = (gh api "repos/$repo/contents/${path}?ref=master" --jq .sha 2>$null)
$args2 = @('api', '-X', 'PUT', "repos/$repo/contents/$path", '-f', "message=puente: direccion vigente $hostName", '-f', "content=$([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($body)))", '-f', 'branch=master')
if ($sha) { $args2 += @('-f', "sha=$sha") }
gh @args2 | Out-Null
"Puente en linea: $hostName (publicado en relay.json)"
