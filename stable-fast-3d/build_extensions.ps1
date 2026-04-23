Import-Module "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools" -DevCmdArguments "-arch=x64" -SkipAutomaticLocation

$env:DISTUTILS_USE_SDK = "1"
$env:USE_CUDA = "0"

Set-Location "C:\Users\Lakshya\Desktop\stable-fast-3d"

Write-Host "=== Building texture_baker ==="
& .\venv\Scripts\pip.exe install --no-build-isolation ./texture_baker/

Write-Host "=== Building uv_unwrapper ==="
& .\venv\Scripts\pip.exe install --no-build-isolation ./uv_unwrapper/

Write-Host "=== Done ==="
