# build.ps1 - compila CAPCOM (net48, un solo exe sin dependencias) en dist\
#   .\build.ps1           -> compila
#   .\build.ps1 -Run      -> compila y lo abre
#   .\build.ps1 -Probar   -> compila y corre las pruebas de a bordo
#   .\build.ps1 -Icono    -> regenera assets\app.ico (desde assets\galaxia.png) antes de compilar
param([switch]$Run, [switch]$Probar, [switch]$Icono)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'dist\capcom.exe'

if ($Icono -or -not (Test-Path (Join-Path $root 'assets\app.ico'))) {
    python (Join-Path $root 'assets\make-icon.py')
    if ($LASTEXITCODE -ne 0) { Write-Host 'no pude armar assets\app.ico (hace falta python con Pillow, numpy y scipy)' -ForegroundColor Red; exit 1 }
}

# Una instancia abierta traba dist\capcom.exe y el build muere en el copy. Se cierra SOLO capcom, nada mas,
# y se vuelve a abrir al final como estaba: en la bandeja si estaba guardada, con la ventana si estaba abierta.
$estaba = @(Get-Process -Name 'capcom' -ErrorAction SilentlyContinue)
$abierta = @($estaba | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }).Count -gt 0
if ($estaba.Count -gt 0) {
    Write-Host ("cerrando {0} instancia(s) de capcom para reemplazar el exe" -f $estaba.Count) -ForegroundColor DarkYellow
    # --salir la cierra DE VERDAD y por las buenas: guarda, baja su servidor y saca el icono de la bandeja.
    # Matarla deja el icono fantasma en la bandeja (hasta pasarle el mouse) y el llama-server huerfano.
    try { Start-Process -FilePath $exe -ArgumentList '--salir' -Wait -WindowStyle Hidden } catch { }
    for ($i = 0; $i -lt 30 -and @(Get-Process -Name 'capcom' -ErrorAction SilentlyContinue).Count -gt 0; $i++) { Start-Sleep -Milliseconds 100 }
    $quedan = @(Get-Process -Name 'capcom' -ErrorAction SilentlyContinue)
    if ($quedan.Count -gt 0) {
        Write-Host 'no salio por las buenas: la cierro a la fuerza' -ForegroundColor DarkYellow
        $quedan | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }
}

dotnet build (Join-Path $root 'capcom.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'build FALLO' -ForegroundColor Red; exit 1 }

$fi = Get-Item $exe
Write-Host ("OK -> {0}  {1:N0} bytes  {2}" -f $fi.FullName, $fi.Length, $fi.LastWriteTime) -ForegroundColor Green

if ($Probar) { & $exe --probar }
if ($Run -or ($abierta -and -not $Probar)) { Start-Process $exe -ArgumentList '--mostrar' }
elseif ($estaba.Count -gt 0 -and -not $Probar) { Start-Process $exe -ArgumentList '--min' }
