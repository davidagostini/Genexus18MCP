# Agent Retro Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tornar a validação live do contrato de salvamento de Events reproduzível, diagnosticável e protegida contra executar um Gateway errado, além de preservar os campos de edição no caminho Gateway → Worker.

**Architecture:** Extrair a validação/hash de fixtures para um helper compartilhado e adicionar um gerador explícito de manifestos. O script live passará a controlar o executável, porta, logs, timeout e filtro de testes; o harness C# confirmará a identidade do processo e emitirá diagnóstico útil. Um smoke test dedicado a Events ficará separado da suíte live ampla, e um teste de contrato cobrirá o envelope RPC.

**Tech Stack:** PowerShell, .NET 8/10 test tooling already used by the repository, xUnit, Newtonsoft.Json, Node/npm scripts, Markdown.

---

## 1. Fixtures reproduzíveis

- [x] Extrair hash e validação de fixture para `scripts/live-fixture.ps1` sem alterar o contrato existente.
- [x] Criar `scripts/new-live-fixture-manifest.ps1`, exigindo confirmação explícita de isolamento e calculando as hashes reais dos arquivos da KB.
- [x] Adicionar teste de script para geração, validação, detecção de alteração e bloqueio sem confirmação.
- [x] Fazer `test-live.ps1` e `live-build-all.ps1` usarem o helper compartilhado.

## 2. Harness live determinístico

- [x] Adicionar `scripts/live-harness.ps1` para confirmar no log que o Gateway iniciou como master, rejeitando proxy/duplicata.
- [x] Fazer o script live definir executável, diretório de log, porta e timeout RPC de forma isolada e restaurável.
- [x] Adicionar filtro de teste e diagnósticos de timeout ao script live e propagar o filtro pelo teste de matriz.
- [x] Cobrir as novas garantias nos testes de scripts e no harness C#.

## 3. Contrato e smoke focado

- [x] Generalizar o teste de roteamento para verificar que os campos de verificação e salvamento chegam ao envelope Worker.
- [x] Adicionar smoke live dedicado para `Events` com `requireObjectSave`, evidência de persistência, leitura fresca e rejeição de versão obsoleta.
- [x] Tornar pré-condições Team Development skips de descoberta, evitando que um teste opcional apareça como falha.

## 4. Documentação e validação

- [x] Documentar geração de manifestos, filtro focado, isolamento e diagnóstico.
- [x] Registrar a melhoria em `CHANGELOG.md` sob `Unreleased`.
- [x] Executar testes estreitos, testes dos scripts, builds/testes relevantes e o smoke live dos dois manifestos/SDKs.
- [x] Revisar diff, status e processos do workspace antes do relatório final.
