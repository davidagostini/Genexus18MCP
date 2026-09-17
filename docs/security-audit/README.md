# Auditoria de seguranca

Este diretorio guarda o relatorio sanitizado e o gerador deterministico usado
na revisao das cinco categorias de seguranca do Genexus18MCP.

## Regenerar

O gerador usa somente a biblioteca `reportlab` em Python e nao instala pacotes
globalmente. Se a biblioteca nao estiver disponivel, crie um ambiente virtual
temporario fora do repositorio, instale `reportlab` nele e execute:

```powershell
python docs/security-audit/generate_report.py --output docs/security-audit/relatorio-auditoria-seguranca.pdf
```

O relatorio usa evidencias anonimizadas, sem caminhos de KB do cliente, tokens
ou valores de credenciais. A auditoria de codigo deve ser refeita quando os
arquivos citados mudarem; atualize as linhas no gerador antes de regenerar.

## Gate antes de commit ou PR

Na copia de trabalho limpa, executar:

```powershell
git diff --check
dotnet test src\GxMcp.Worker.Tests\GxMcp.Worker.Tests.csproj --no-restore -p:GX_PATH=<caminho-do-sdk>
dotnet test src\GxMcp.Gateway.Tests\GxMcp.Gateway.Tests.csproj --no-restore
npm test
npm --prefix src\nexus-ide run check
python -m py_compile docs/security-audit/generate_report.py
python docs/security-audit/generate_report.py --output docs/security-audit/relatorio-auditoria-seguranca.pdf
```

Depois rasterize o PDF com `pdftoppm` e inspecione as paginas para confirmar
graficos, tabelas e rodape. O PR deve conter o relatorio, o script e a
documentacao da correcao; nao incluir `node_modules`, `.vscode-test`, `bin` ou
`obj`.
