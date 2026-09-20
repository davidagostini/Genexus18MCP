# Issue #248 — limpeza determinística do stub Gateway no Windows

## Sintoma

Em algumas execuções no Windows, toda a suíte Node passava, mas o `test.after`
falhava ao remover o diretório temporário com `EPERM`. O processo
`GxMcp.Gateway.exe` usado pelos probes ainda podia estar vivo depois do sinal de
terminação, mantendo a imagem do executável aberta.

## Correção

O fallback de teardown continua limitado ao diretório temporário criado pela
própria suíte e ao executável `GxMcp.Gateway.exe`. Depois de enviar a
terminação, ele agora aguarda explicitamente o PID observado por uma janela
limitada antes de tentar remover o diretório. A remoção permanece com retries
somente para erros transitórios do Windows; processos fora desse diretório nunca
são selecionados.

## Garantias

- nenhuma varredura ou finalização global por nome de processo;
- nenhuma alteração de KB, SDK, configuração ou ciclo de vida GeneXus;
- o caminho de produção não muda: a proteção é somente do cleanup dos testes;
- as regressões verificam o escopo do processo, a sintaxe do pipeline e, no
  Windows, a finalização de um stub real antes da remoção.

## Validação

Executar `npm test` no Windows. O teste unitário do cleanup também pode ser
executado isoladamente com:

```text
node --test cli/run.test.js
```

Se o runner estiver sem os pré-requisitos GeneXus, a validação live deve ser
reportada como indisponível, sem transformar essa ausência ambiental em bypass.
