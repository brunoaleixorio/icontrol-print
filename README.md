# iControl Print (servidor de impressão)

Serviço do Windows em .NET 8 que imprime os PrintJob do iControl Food nas ilhas (cozinha, bar, caixa).

- `src/IControlPrint`: Worker Service (Microsoft.Extensions.Hosting.WindowsServices), self-contained win-x64, arquivo único.
- `installer/iControlPrint.iss`: instalador Inno Setup 6 (pede o código, confirma a empresa, registra o serviço com início automático e reinício em falha).
- `.github/workflows/print-server.yml`: gera o instalador a cada tag `print-vX.Y.Z` (ou manualmente em Actions) e publica na Release.

## Fluxo
1. Sistema: Configurações > PDV > Impressão > Adicionar servidor (`print_pair_create`, código de 8 caracteres, 10 min, uso único).
2. Instalador: `IControlPrint.exe --preview CÓDIGO` mostra empresa e CNPJ; com a confirmação, `--claim CÓDIGO` troca por token (no banco só o hash; no Windows, DPAPI LocalMachine em `C:\ProgramData\iControlPrint\token.bin`).
3. Serviço: `print_server_heartbeat` (30 s, impressoras e status), Realtime Broadcast no canal secreto do servidor + `print_server_pull` a cada 15 s (lock, sem duplicar entre principal e reserva), impressão RAW (winspool ou IP:9100, ESC/POS), até 3 tentativas + impressora reserva, `print_server_ack`.
4. Atualização: o heartbeat devolve a última versão de `print_server_releases`; o serviço baixa, confere o SHA-256 e roda o instalador em modo silencioso.

## Comandos úteis (prompt como administrador)
- `IControlPrint.exe --status` · `--printers` · `--test "Nome da impressora"` (ou `192.168.0.50:9100`) · `--unpair`
- Logs: `C:\ProgramData\iControlPrint\logs` (um arquivo por dia, 14 dias).
- Serviço: `sc query iControlPrint`, `sc stop/start iControlPrint`.

## Gerar uma versão
`git tag print-v1.0.1 && git push --tags`. Para a atualização automática, cadastre o segredo `SUPABASE_SERVICE_ROLE_KEY` no repositório (o workflow grava a versão em `print_server_releases`). O repositório (ou as Releases) precisa ser público para o serviço baixar o instalador.
