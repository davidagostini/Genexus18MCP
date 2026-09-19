# Guía de inicio — GeneXus MCP

Esta guía te lleva de cero a "el asistente de IA está editando mi KB de GeneXus" en unos 5-10 minutos.

> Para problemas durante o después de la instalación, consultá [TROUBLESHOOTING.md](../TROUBLESHOOTING.md).

---

## ¿Qué es esto, en una frase?

Es un puente entre tu **IA asistente** (Claude, Cursor, Antigravity, etc.) y una **Knowledge Base de un major soportado de GeneXus**. Una vez instalado, podés pedirle a la IA cosas como *"agregale una regla a la transacción Pedido que valide el total"* y la IA usa el SDK nativo de GeneXus para hacerlo de verdad en tu KB.

---

## Requisitos previos

Antes de empezar, asegurate de tener:

- ✅ **Windows** (GeneXus es solo Windows)
- ✅ **Un SDK soportado de GeneXus** instalado (consultá los caminos en [`docs/generated/supported-versions.md`](generated/supported-versions.md))
- ✅ **Una KB creada con un major soportado de GeneXus** que ya hayas abierto al menos una vez en el IDE (para que esté inicializada)
- ✅ **Node.js 22 o superior** — verificalo con `node --version` en una terminal; instalalo desde [nodejs.org](https://nodejs.org/) si te falta
- ✅ **Un cliente de IA compatible con MCP** — [Claude Desktop](https://claude.ai/download), [Claude Code](https://claude.com/claude-code), Cursor, Antigravity, etc.

**No** necesitás clonar el repositorio. **No** necesitás instalar nada globalmente con `npm`. Todo se maneja con `npx`.

**¿Nunca usaste una terminal?** Apretá `Win+R`, escribí `powershell`, Enter. Esa es tu terminal.

---

## Paso 1 — Identificá tus dos rutas

Antes de correr el instalador, anotá:

1. **Ruta de instalación de GeneXus** — la carpeta donde está `GeneXus.exe`.
   Ejemplo: `C:\Program Files (x86)\GeneXus\GeneXus18`

2. **Ruta de tu KB** — la carpeta raíz de tu Knowledge Base (la que contiene el archivo `.gx` y carpetas como `Model/`, `WebSpa/`).
   Ejemplo: `C:\KBs\MiKnowledgeBase`

El major del SDK debe ser el mismo major con el que se creó la KB. Para una KB
de GeneXus 17, usá la instalación `GeneXus17Trial`; para una KB de GeneXus 18,
usá `GeneXus18`. El instalador valida esta combinación antes de guardar la
configuración.

Si no estás seguro de la ruta de tu KB, abrila en GeneXus y mirá la barra de título o el menú File → Recent.

---

## Paso 2 — Correr el instalador

Abrí una **terminal nueva** (PowerShell o CMD) y pegá este comando, **reemplazando las rutas con las tuyas**:

```bash
npx genexus-mcp@latest init --kb "C:\KBs\MiKnowledgeBase" --gx "C:\Program Files (x86)\GeneXus\GeneXus18"
```

Ejemplo para una KB de GeneXus 17:

```bash
npx genexus-mcp@latest init --kb "C:\KBs\KBTeste17" --gx "C:\Program Files (x86)\GeneXus\GeneXus17Trial"
```

Lo que vas a ver:

1. `npx` descarga el paquete (la primera vez tarda 10-30 segundos).
2. El instalador verifica que las rutas existan y que GeneXus esté presente.
3. **Detecta automáticamente** qué clientes de IA tenés instalados (Claude Desktop, Claude Code, Cursor, Antigravity) y agrega la configuración del MCP en cada uno.
4. Imprime un bloque JSON al final — guardalo por si necesitás configurar manualmente algún cliente que no fue detectado.
5. Termina con `🎉 You are all set!`.

> **¿Preferís el asistente interactivo?** Corré `npx genexus-mcp@latest init --interactive` y respondé las preguntas.

---

## Paso 3 — Reiniciar el cliente de IA

Esto es importante: **cerrá completamente** tu cliente de IA y volvelo a abrir.

- **Claude Desktop**: clic derecho en el ícono de la bandeja del sistema → Quit. Después abrilo de nuevo. (Cerrar la ventana **no alcanza**.)
- **Claude Code**: cerrá la sesión y abrila de nuevo.
- **Cursor / Antigravity**: cerrá todas las ventanas y reabrí.

El cliente necesita reiniciarse para descubrir el nuevo MCP server.

---

## Paso 4 — Probar que funciona

En tu cliente de IA, pegá este prompt:

> *"Usando el GeneXus MCP, listame los primeros 5 objetos de mi KB y mostrame el nombre y tipo de cada uno."*

**¿Qué debería pasar?**

- La IA debería invocar la herramienta `genexus_list_objects` (a veces lo verás en la UI como "calling tool…").
- En unos segundos, te devuelve una lista de objetos de tu KB.

Si esto funciona, **¡terminaste!** Pasá a la siguiente sección para ver qué más podés hacer.

Si **no** ves la lista o la IA dice que no tiene la herramienta de GeneXus, andá a [Troubleshooting](../TROUBLESHOOTING.md).

---

## Tus primeros prompts útiles

Una vez que el "list 5 objects" funcionó, probá estos:

### Explorar la KB

> *"Mostrame el código fuente del procedure CalcularTotalFactura."*

> *"Buscá todas las transacciones que usen el atributo IdCliente."*

> *"¿Qué objetos tengo del tipo WebPanel?"*

### Analizar código

> *"Explicame paso a paso qué hace el procedure ProcesarEnvio."*

> *"¿Qué SQL genera el query del WebPanel ListaClientes?"*

> *"Resumime la estructura del módulo Ventas."*

### Editar la KB

> *"Agregale a la transacción Pedido una regla que dispare un error si Total es menor a 0."*

> *"Creame una nueva transacción llamada Categoria con los atributos IdCategoria, NombreCategoria y Activa."*

> *"En el procedure CrearPedido, renombrá la variable &cant a &cantidad."*

### Editar pantallas WorkWithPlus (patterns)

El MCP edita el XML completo de `PatternInstance` / `PatternVirtual`: agregar, sacar y reordenar controles (textBlock, atributos, botones, grupos, órdenes, filtros), aplicar clases de tema (`themeClass`, `buttonClass`, `groupThemeClass`), y reorganizar las vistas **Transaction** y **Selection** de forma independiente.

> *"En WorkWithPlusPedido, agregá un botón 'Duplicar' en la vista de transacción al lado de Guardar/Cancelar/Eliminar."*

> *"Agrupá los atributos de la transacción Cliente en una sección 'Datos de contacto' con groupThemeClass='GroupTelaResp'."*

> *"En la lista de WorkWithPlusFactura, agregá una ordenación nueva por FechaFactura descendente."*

> *"Aplicale buttonClass='btn ButtonGreen' al botón Guardar de WorkWithPlusPedido y poné el header del formulario con themeClass='BigTitle'."*

> *"Sacá la acción Exportar de la grilla de Selection en WorkWithPlusReporte."*

> *"Leeme la parte Documentation de la transacción Cliente y reescribila en markdown."*

El MCP recalcula automáticamente el atributo `childrenOrderedList` que el IDE usa para renderizar el orden — vos solo decís *dónde* va el elemento en el XML, el MCP se encarga de que aparezca ahí en el IDE. La respuesta incluye un bloque `childrenOrderedListReconciliation` con lo que se reescribió y por qué.

Para descubrir las clases de tema disponibles en tu KB:

> *"Listame todos los ThemeClass cuyo nombre contenga 'Button'."*

(En el fondo eso llama `genexus_list_objects --typeFilter ThemeClass --nameFilter Button`.)

**Detalle clave**: los botones custom (Duplicar, Auditar, Exportar…) deben ser `<userAction>`, no `<standardAction>`. Solo `Trn_Enter` / `Trn_Cancel` / `Trn_Delete` son acciones estándar registradas en una transaction WorkWithPlus. El reconciler trata `<userAction>` como par de `<standardAction>` y conviven en la misma fila de TableActions.

Si querés que tus overrides de pattern sobrevivan al engine de WorkWithPlus (que normaliza algunos campos como el `title` en cada save), apagá el "Apply on save" así:

> *"Setá la property SDPlus_Editor_Apply_On_Save de WorkWithPlusPedido a False."*

Internamente eso es `genexus_properties --action set --name WorkWithPlus<Objeto> --propertyName SDPlus_Editor_Apply_On_Save --value False` (acepta `True | False | Default`).

Detalles completos del workflow, la matriz de capacidades verificadas y orientaciones sobre el pattern-engine de WorkWithPlus: ver [README — WorkWithPlus pattern editing](../README.md#workwithplus-pattern-editing--what-you-can-actually-do).

### Build y testing

> *"Compilá la KB y reportame los errores si hay alguno."*

> *"Corré los tests unitarios y mostrame cuáles fallaron."*

---

## Consejos para sacarle el jugo

**1. Sé específico con los nombres de objetos.** La IA es buena buscando, pero si le decís *"editá la transacción de pedidos"* puede preguntar cuál (Pedido, PedidoDetalle, OrdenPedido…). Decile el nombre exacto cuando lo sepas.

**2. Pedí `dryRun` cuando estés probando.** Para ediciones, podés pedirle: *"hacé esto en modo dryRun primero y mostrame el preview"*. El MCP devuelve qué iba a cambiar **sin tocar la KB**. Útil cuando estás aprendiendo o cuando el cambio es delicado.

**3. La primera petición después de un rato es lenta.** El "worker" (la parte que habla con GeneXus) se apaga después de 5 minutos sin uso para no bloquear archivos. La primera llamada después de eso tarda 3-8 segundos en arrancar. Es por diseño, no es un bug.

**4. Si vas a buildear la KB desde el IDE de GeneXus, pará el worker primero usando la tool MCP:**

```bash
genexus_worker_reload mode=soft
```

Si no, podés tener conflictos de archivos bloqueados.

**5. Mirá la lista completa de herramientas:**

```bash
npx genexus-mcp tools list
```

Te muestra los 30+ tools que la IA tiene disponibles. No necesitás saberlos de memoria — la IA los elige sola — pero ayuda saber qué es posible.

---

## ¿Algo no funciona?

1. Corré el diagnóstico:
   ```bash
   npx genexus-mcp doctor --mcp-smoke
   ```
2. Buscá tu problema en [TROUBLESHOOTING.md](../TROUBLESHOOTING.md).
3. Si nada de eso funciona, [abrí un issue](https://github.com/lennix1337/Genexus18MCP/issues) con la salida del comando anterior y los logs de tu cliente de IA.

---

## ¿Qué sigue?

- **Lista completa de herramientas y modos de edición**: [README principal](../README.md#tool-surface)
- **Contrato CLI para agentes (AXI)**: [`docs/axi_cli_contract.md`](axi_cli_contract.md)
- **Playbook de mejores prácticas LLM**: [`docs/llm_cli_mcp_playbook.md`](llm_cli_mcp_playbook.md)
- **Configuración avanzada** (timeouts, networking, shadow paths): [README principal — Advanced Configuration](../README.md#advanced-configuration)

¡Bienvenido al proyecto! Si encontrás bugs o tenés ideas, **abrir issues es la forma más útil de contribuir** — queda registrado y ayuda a los próximos usuarios.
