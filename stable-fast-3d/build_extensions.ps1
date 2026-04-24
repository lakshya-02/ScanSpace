Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectRoot

$vsInstalls = @(
    "C:\Program Files\Microsoft Visual Studio\18\Insiders",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools",
    "C:\Program Files\Microsoft Visual Studio\2022\Community",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise",
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools",
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\Community",
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\Professional",
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\Enterprise"
)

$vsInstall = $vsInstalls | Where-Object { Test-Path (Join-Path $_ "Common7\Tools\Microsoft.VisualStudio.DevShell.dll") } | Select-Object -First 1
if (-not $vsInstall) {
    throw "Could not find a supported Visual Studio install with DevShell tools."
}

$devShellDll = Join-Path $vsInstall "Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Import-Module $devShellDll
Enter-VsDevShell -VsInstallPath $vsInstall -DevCmdArguments "-arch=x64" -SkipAutomaticLocation

$venvPython = Join-Path $projectRoot ".venv312\Scripts\python.exe"
if (-not (Test-Path $venvPython)) {
    throw "Missing virtual environment at $venvPython"
}

$env:DISTUTILS_USE_SDK = "1"
$env:CUDA_HOME = ${env:CUDA_PATH}
$env:USE_CUDA = if ($env:USE_CUDA) { $env:USE_CUDA } else { "1" }
$env:TORCH_CUDA_ARCH_LIST = if ($env:TORCH_CUDA_ARCH_LIST) { $env:TORCH_CUDA_ARCH_LIST } else { "8.9" }

Write-Host "=== Visual Studio ==="
Write-Host $vsInstall
Write-Host "=== Building texture_baker ==="
& $venvPython -m pip install --force-reinstall --no-build-isolation --no-cache-dir ./texture_baker/

Write-Host "=== Building uv_unwrapper ==="
& $venvPython -m pip install --force-reinstall --no-build-isolation --no-cache-dir ./uv_unwrapper/

Write-Host "=== Done ==="
