# Instala (ou atualiza) o PhoneBox Janelas para o seu usuário. Não precisa de administrador.
#
#   powershell -ExecutionPolicy Bypass -File .\instalar.ps1
#
# O que faz: compila janelas.cs com o compilador do .NET Framework que já vem no Windows, copia o programa para
# %LOCALAPPDATA%\PhoneBox-Janelas, coloca um atalho na pasta Inicializar (para abrir junto com o Windows) e inicia.
# Rodar de novo atualiza sem perder a lista de apps ignorados.

$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$dir = Join-Path $env:LOCALAPPDATA 'PhoneBox-Janelas'
$exe = Join-Path $dir 'janelas.exe'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'PhoneBox Janelas.lnk'

function Passo($texto) { Write-Host "==> $texto" -ForegroundColor Cyan }
function Ok($texto) { Write-Host "    OK  $texto" -ForegroundColor Green }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($admin) {
    Write-Host 'Rode numa janela do PowerShell normal, não como administrador: o PhoneBox roda como você.' -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path (Join-Path $src 'janelas.cs'))) { throw "janelas.cs não está em $src" }

Passo 'Parando a versão anterior (se houver)'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList 'parar' -Wait -WindowStyle Hidden
    $limite = (Get-Date).AddSeconds(6)
    while ((Get-Process -Name 'janelas' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) -and (Get-Date) -lt $limite) {
        Start-Sleep -Milliseconds 200
    }
    Get-Process -Name 'janelas' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
}
Ok 'pronto'

Passo 'Compilando'
$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'Não achei o csc.exe do .NET Framework 4 (ele vem com o Windows 10/11).' }

# 'Continue' só aqui: no Windows PowerShell 5.1, uma linha em stderr de um programa vira erro fatal com 'Stop'
$ErrorActionPreference = 'Continue'
$saida = & $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
    "/out:$exe" /reference:System.Drawing.dll (Join-Path $src 'janelas.cs') 2>&1
$codigo = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($codigo -ne 0) {
    $saida | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw 'A compilação falhou. Copie as linhas vermelhas acima e mande para quem te passou o script.'
}
Ok $exe

Passo 'Lista de apps que nunca aparecem no tablet'
$ignorar = Join-Path $dir 'ignorar.txt'
if (-not (Test-Path $ignorar)) {
    @(
        '# Um nome de programa por linha (sem .exe). As janelas desses programas não vão para o tablet.',
        '# Bom para gerenciadores de senha e qualquer coisa que você não quer ver numa miniatura.',
        'KeePass',
        'KeePassXC',
        '1Password',
        'Bitwarden'
    ) | Set-Content -Path $ignorar -Encoding UTF8
}
Ok $ignorar

Passo 'Abrir junto com o Windows'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startup)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dir
$lnk.Description = 'PhoneBox Janelas: miniaturas das janelas no tablet'
$lnk.Save()
Ok $startup

Passo 'Iniciando'
Start-Process -FilePath $exe -WorkingDirectory $dir
Start-Sleep -Seconds 4
$rodando = Get-Process -Name 'janelas' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if (-not $rodando) { throw "O programa não ficou rodando. Veja $dir\log.txt" }
try {
    $push = Invoke-RestMethod -Uri 'http://127.0.0.1:8765/api/push' -TimeoutSec 5
    if ($push.items.janelas) { Ok 'o PhoneBox já está recebendo as miniaturas' }
    else { Write-Host '    !   o PhoneBox ainda não recebeu nada; espere alguns segundos ou veja o log.txt' -ForegroundColor Yellow }
} catch {
    Write-Host '    !   não consegui falar com o PhoneBox em 127.0.0.1:8765. Ele está aberto?' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Para os atalhos janela-1 ... janela-9 do PhoneBox:' -ForegroundColor Cyan
Write-Host "    Comando:     $exe"
Write-Host '    Argumentos:  focar 1   (focar 2, focar 3 ... focar 9)'
