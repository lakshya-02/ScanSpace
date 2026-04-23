Write-Host "Hugging Face Login for Stable Fast 3D" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Step 1: Go to https://huggingface.co/stabilityai/stable-fast-3d"
Write-Host "        Click 'Agree and access repository'"
Write-Host ""
Write-Host "Step 2: Go to https://huggingface.co/settings/tokens"
Write-Host "        Create/copy your token"
Write-Host ""
Write-Host "Step 3: Paste your token below when prompted"
Write-Host ""

Set-Location $PSScriptRoot
& .\venv311\Scripts\Activate.ps1
python -c "from huggingface_hub import login; login()"

Write-Host ""
Write-Host "Login complete! You can now run start_server_lan.bat" -ForegroundColor Green
pause
