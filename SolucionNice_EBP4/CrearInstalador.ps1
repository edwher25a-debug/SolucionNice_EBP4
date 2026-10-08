# Genera los instaladores .msi de ProyectoNice para Revit 2024, 2025 y 2026.
# Requisitos: .NET SDK y Revit cerrado (la compilacion copia el add-in a la carpeta Addins).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$proyecto = "..\ProyectoNice\ProyectoNice.csproj"
$versiones = "R24", "R25", "R26"

# 01_Compilar el add-in en Release para cada version
foreach ($v in $versiones) {
    dotnet build $proyecto -c "Release $v"
    if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion de Release $v" }
}

# 02_Carpetas publicadas por version (bin\Release R2x\publish\Revit 20xx ...)
$carpetas = foreach ($v in $versiones) {
    Get-ChildItem "..\ProyectoNice\bin\Release $v\publish" -Directory -Filter "Revit*" | Select-Object -First 1 -ExpandProperty FullName
}

# 03_Compilar y ejecutar el instalador
dotnet build "install\Installer.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion del instalador" }

& "install\bin\Release\Installer.exe" $carpetas
if ($LASTEXITCODE -ne 0) { throw "Fallo la generacion de los .msi" }

Write-Host "Instaladores en: $(Resolve-Path output)"
