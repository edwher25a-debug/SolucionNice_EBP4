# Genera los instaladores .msi de ProyectoNice para Revit 2024, 2025 y 2026.
# Requisitos: .NET SDK y Revit cerrado (la compilacion copia el add-in a la carpeta Addins).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$addin = "..\ProyectoNice"
$versiones = @{ "R24" = "2024"; "R25" = "2025"; "R26" = "2026" }
$staging = Join-Path $PSScriptRoot "output\staging"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

$carpetas = foreach ($v in $versiones.Keys | Sort-Object) {
    # 01_Compilar el add-in en Release
    # Out-Host: la salida de dotnet se muestra y no se mezcla con la lista de carpetas
    dotnet build "$addin\ProyectoNice.csproj" -c "Release $v" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion de Release $v" }

    # 02_Carpeta de salida de la compilacion (donde quedo ProyectoNice.dll)
    $dll = Get-ChildItem "$addin\bin\Release $v" -Recurse -Filter "ProyectoNice.dll" |
        Where-Object { $_.FullName -notmatch "\\publish\\" } | Select-Object -First 1
    if (-not $dll) { throw "No se encontro ProyectoNice.dll en $addin\bin\Release $v" }

    # 03_Armar la carpeta del instalador: Revit 20xx\ProyectoNice.addin + Revit 20xx\ProyectoNice\*.dll
    $destino = Join-Path $staging "Revit $($versiones[$v])"
    New-Item "$destino\ProyectoNice" -ItemType Directory -Force | Out-Null
    Copy-Item "$addin\ProyectoNice.addin" $destino
    Get-ChildItem $dll.DirectoryName | Where-Object { $_.Name -ne "publish" -and $_.Extension -ne ".pdb" } |
        Copy-Item -Destination "$destino\ProyectoNice" -Recurse

    $destino
}

# 04_Compilar y ejecutar el instalador
dotnet build "install\Installer.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion del instalador" }

& "install\bin\Release\Installer.exe" $carpetas
if ($LASTEXITCODE -ne 0) { throw "Fallo la generacion de los .msi" }

Remove-Item $staging -Recurse -Force
Write-Host "Instaladores en: $(Resolve-Path output)"
