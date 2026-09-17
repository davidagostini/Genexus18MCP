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

## Revisao de Clean Code, SOLID e arquitetura

A revisao do patch identificou uma violacao de DIP: os servicos de I/O
construíam diretamente a politica concreta de caminhos, repetindo a decisao de
composicao em varios pontos. A correcao introduz a porta interna
`IUserFilePathPolicy`; o `CommandDispatcher`, que e o composition root do
Worker, cria uma unica `UserFilePathPolicy` e injeta essa abstracao em
`ObjectService`, `ObjectTextService`, `TransferService`,
`ApiIntrospectService` e `ProfileService`.

Os construtores publicos antigos continuam como adaptadores de compatibilidade
para testes e consumidores existentes; o fluxo principal usa a dependencia
injetada. A politica permanece isolada como detalhe de infraestrutura, e as
operacoes continuam com early returns, sem acoplamento novo ao SDK nos
servicos consumidores. O `CommandDispatcher` continua deliberadamente como
composition root; decompo-lo seria uma mudanca transversal sem beneficio
especifico para este achado.
