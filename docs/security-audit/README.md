# Auditoria de seguranca

Este diretorio guarda o relatorio reproduzivel da auditoria estatica solicitada
para o Genexus18MCP. O relatorio nao inclui segredos, tokens, caminhos privados
de KB ou dados de cliente.

## Geracao

```powershell
python docs/security-audit/generate_report.py
```

O script gera `relatorio-auditoria-seguranca.pdf` no mesmo diretorio. O PDF deve
ser renderizado e inspecionado antes de cada alteracao relevante.

## Resultado desta rodada

- Nenhuma vulnerabilidade reproduzivel foi confirmada.
- A revisao cobriu isolamento de sessao/KB, browser auth, IDOR, segredos/logs e
  XSS/eval.
- Foram registrados dois acompanhamentos documentais: atualizar a versao
  suportada em `SECURITY.md` e nomear explicitamente o endpoint HTTP legado no
  threat model.
- A lane Worker depende do SDK GeneXus e nao foi executada neste ambiente.

Consulte o PDF para a matriz de cobertura, limitacoes e o texto de triagem para
uma possivel issue documental.
