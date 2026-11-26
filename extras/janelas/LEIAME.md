# PhoneBox Janelas

Mostra no tablet uma grade com as miniaturas das janelas abertas no PC (como o Alt+Tab). Tocar numa miniatura traz
aquela janela para frente. Funciona com o PhoneBox oficial, sem recompilar o agente nem o app: usa só o widget
**HTML personalizado**, os **valores enviados** (`POST /api/push`) e **atalhos**.

## Como funciona

- `janelas.exe` roda em segundo plano no Windows. A cada 3 s ele lista as janelas, atualiza até 4 miniaturas
  (a janela ativa sempre) e envia tudo para `http://127.0.0.1:8765/api/push/janelas?ttl=30`, cabendo nos 64 KB que o
  PhoneBox aceita (a qualidade e o tamanho das miniaturas baixam sozinhos quando há muitas janelas).
- Cada janela fica num quadro fixo (1 a 9) enquanto estiver aberta, para a grade não pular.
- O widget (`widget.html`) mostra a grade e, no toque, chama `phonebox.run('janela-N')`.
- O atalho `janela-N` roda `janelas.exe focar N`, que traz a janela do quadro N para frente.
- Se o programa parar, o valor vence em 30 s e a grade some do tablet.

## Instalar

No PowerShell normal (não como administrador), na pasta destes arquivos:

```powershell
powershell -ExecutionPolicy Bypass -File .\instalar.ps1
```

O instalador compila `janelas.cs` com o `csc.exe` do .NET Framework 4 que já vem no Windows, copia para
`%LOCALAPPDATA%\PhoneBox-Janelas`, coloca um atalho na pasta Inicializar e inicia o programa.

Depois, no PhoneBox:

1. **Atalhos**: crie `janela-1` … `janela-9`, cada um com *Executar um programa*, comando
   `%LOCALAPPDATA%\PhoneBox-Janelas\janelas.exe` (caminho completo) e argumentos `focar 1` … `focar 9`.
   Deixe-os fora da grade de atalhos (na grade, marque só os atalhos que você quer ver).
2. **Páginas**: crie uma página com o widget **HTML personalizado** em tela cheia e cole o conteúdo de
   `widget.html` no campo HTML.

## Privacidade

As miniaturas vão só para o PhoneBox deste PC e, dele, para o tablet pareado (HTTPS com certificado fixado). O
PhoneBox guarda os valores enviados em `%APPDATA%\PhoneBox\data.json` enquanto não vencem. Programas listados em
`%LOCALAPPDATA%\PhoneBox-Janelas\ignorar.txt` (um por linha, sem `.exe`) nunca aparecem; o instalador já coloca
alguns gerenciadores de senha.

## Remover

```powershell
powershell -ExecutionPolicy Bypass -File .\desinstalar.ps1
```

Depois apague os atalhos `janela-N` e a página no PhoneBox.
