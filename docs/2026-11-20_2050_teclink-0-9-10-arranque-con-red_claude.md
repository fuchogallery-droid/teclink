# Relevo 2026-11-20 2050 - TecLink 0.9.10 (claude)

## Problema
La otra PC no conectaba con jenav. Causa en el codigo: el commit 4c6e36d (0.6.5) hizo que TecLink arrancara SIN red
(no escucha, no busca) hasta pulsar un boton. Ninguna PC quedaba esperando sola.

## Arreglo (0.9.10)
src/TecLink.App/TrayAppContext.cs: el constructor llama StartNetworking() al abrir la app (anfitrion escucha; invitado busca).
Version subida a 0.9.10 en TecLink.App.csproj y installer/TecLink.iss.
Instalador: out/TecLink-Setup-0.9.10.exe (compilar con ISCC /DSrc=<carpeta de dotnet publish>). Instalado en jenav; la otra PC tambien.

## Estado al cerrar
- jenav (192.168.12.113): 0.9.10, Role=1 invitado, JoinCode 7FYGWG, busca a la otra PC cada pocos segundos.
- Otra PC (RAFAELNAVARRO): ahora 192.168.12.184 (antes .183). NO responde: ping, 51777, 445, 135, 3389 todos cerrados.
  Sospecha: red en perfil Publico o sin regla de Firewall para TecLink. No esta en Tailscale; no hay acceso remoto.
- Fix pendiente EN LA OTRA PC (PowerShell admin):
  Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory Private
  New-NetFirewallRule -DisplayName "TecLink TCP" -Direction Inbound -Protocol TCP -LocalPort 51777 -Action Allow -Profile Any
  New-NetFirewallRule -DisplayName "TecLink UDP" -Direction Inbound -Protocol UDP -LocalPort 51660 -Action Allow -Profile Any
- Si sigue sin conectar: leer el texto del panel de la otra PC y %AppData%\TecLink\connection.log.
- NO publicado en GitHub Releases ni en technocrazy.org hasta confirmar que conecta.