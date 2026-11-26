# Remove o PhoneBox Janelas: para o programa, tira da inicialização, apaga a pasta e a grade guardada no PhoneBox.
#
#   powershell -ExecutionPolicy Bypass -File .\desinstalar.ps1
#
# Os atalhos janela-1 ... janela-9 e a página do tablet ficam no PhoneBox; apague-os lá se não for mais usar.

$dir = Join-Path $env:LOCALAPPDATA 'PhoneBox-Janelas'
$exe = Join-Path $dir 'janelas.exe'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'PhoneBox Janelas.lnk'

if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList 'parar' -Wait -WindowStyle Hidden
    Start-Sleep -Seconds 2
    Get-Process -Name 'janelas' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
}
Remove-Item -Force -ErrorAction SilentlyContinue $startup
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $dir
try { Invoke-RestMethod -Method Delete -Uri 'http://127.0.0.1:8765/api/push/janelas' -TimeoutSec 5 | Out-Null } catch { }
Write-Host 'PhoneBox Janelas removido.' -ForegroundColor Green
