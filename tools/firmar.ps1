# Firma digital de TecLink (Authenticode). Con la firma, Windows muestra "Editor verificado: TechnoCrazy" en lugar de
# "Editor desconocido", y SmartScreen deja de asustar al descargar.
#
# Hace falta UN certificado de firma de codigo a nombre de TechnoCrazy (de una autoridad reconocida: DigiCert, Sectigo, SSL.com,
# o el servicio Azure Artifact Signing). Un certificado "autofirmado" NO sirve: Windows no lo reconoce.
#
#   Con certificado en un archivo .pfx:   .\tools\firmar.ps1 -Archivo out\TecLink-Setup-0.9.1.exe -Pfx C:\ruta\cert.pfx -Clave (Read-Host -AsSecureString)
#   Con certificado instalado en Windows: .\tools\firmar.ps1 -Archivo out\TecLink-Setup-0.9.1.exe -Huella <huella del certificado>
param([Parameter(Mandatory)][string]$Archivo, [string]$Pfx, [securestring]$Clave, [string]$Huella)
$signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source
if (-not $signtool) { $signtool = (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object FullName -like '*x64*' | Select-Object -First 1)?.FullName }
if (-not $signtool) { throw 'No encuentro signtool.exe (viene con el Windows SDK).' }
$args2 = @('sign', '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', '/d', 'TecLink', '/du', 'https://technocrazy.org')
if ($Pfx) { $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($Clave)); $args2 += @('/f', $Pfx, '/p', $plain) }
elseif ($Huella) { $args2 += @('/sha1', $Huella) }
else { throw 'Indica -Pfx y -Clave, o -Huella.' }
& $signtool @args2 $Archivo
& $signtool verify /pa /v $Archivo | Select-Object -First 6
