#!/usr/bin/env python3
"""Generate a deterministic, sanitized security audit PDF."""

from __future__ import annotations

import argparse
import os
from xml.sax.saxutils import escape

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import cm
from reportlab.platypus import BaseDocTemplate, Flowable, Frame, HRFlowable, KeepTogether, PageBreak, PageTemplate, Paragraph, Spacer, Table, TableStyle


PROJECT = "Genexus18MCP"
DATE = "17/09/2026"
BASE = "a4600828 (origin/main)"
COLOR = {
    "critical": "#B91C1C",
    "high": "#EA580C",
    "medium": "#D97706",
    "low": "#2563EB",
    "strong": "#059669",
    "ink": "#172033",
    "muted": "#5B6475",
    "line": "#D9DEE8",
    "soft": "#F5F7FA",
}


FINDINGS = [
    {
        "id": "SEC-001",
        "severity": "high",
        "title": "Restringir caminhos de troca de arquivos",
        "baseline": [
            ("src/GxMcp.Worker/Services/ObjectService.cs", "3989", "fullPath = Path.GetFullPath(outputPath);"),
            ("src/GxMcp.Worker/Services/ObjectService.cs", "4161", "string fullPath = Path.GetFullPath(outputPath);"),
            ("src/GxMcp.Worker/Services/ObjectService.cs", "4197", "string fullPath = Path.GetFullPath(inputPath);"),
            ("src/GxMcp.Worker/Services/ObjectTextService.cs", "71", "try { root = Path.GetFullPath(outputPath); }"),
            ("src/GxMcp.Worker/Services/ObjectTextService.cs", "523", "try { full = Path.GetFullPath(inputPath); }"),
            ("src/GxMcp.Worker/Services/ApiIntrospectService.cs", "584-585", "if (Path.IsPathRooted(baselineArg) && File.Exists(baselineArg))"),
            ("src/GxMcp.Worker/Services/ProfileService.cs", "43", "if (!File.Exists(path))"),
            ("src/GxMcp.Worker/Services/TransferService.cs", "81-82", "string outputFile = args?[\"outputFile\"]?.ToString();"),
            ("src/GxMcp.Worker/Services/TransferService.cs", "191", "if (!System.IO.File.Exists(file))"),
            ("src/GxMcp.Worker/Services/TransferService.cs", "223", "if (!System.IO.File.Exists(file))"),
        ],
        "fixed": [
            ("src/GxMcp.Worker/Utils/IUserFilePathPolicy.cs", "1-13", "small port for file-path consumers"),
            ("src/GxMcp.Worker/Utils/UserFilePathPolicy.cs", "29-91", "normalization, root resolution, and containment diagnostics"),
            ("src/GxMcp.Worker/Services/CommandDispatcher.cs", "186", "composition root creates one shared policy"),
            ("src/GxMcp.Worker/Services/ObjectService.cs", "3998;4169;4215", "policy before blob, part export, and part import I/O"),
            ("src/GxMcp.Worker/Services/ObjectTextService.cs", "82;535", "policy on batch output and input"),
            ("src/GxMcp.Worker/Services/ApiIntrospectService.cs", "596", "baseline restricted and JSON extension checked"),
            ("src/GxMcp.Worker/Services/ProfileService.cs", "58", "profile restricted before XML read"),
            ("src/GxMcp.Worker/Services/TransferService.cs", "96;207;243", "XPZ export, inspect, and import restricted"),
        ],
        "why": "The MCP caller controlled a filesystem path that was normalized but not checked against an authorized root. The tools could read local files or write exports outside the active KB.",
        "impact": "A caller able to invoke the local MCP surface could access or overwrite unrelated files within the account filesystem permissions.",
        "condition": "The caller must reach the MCP tool and the process keeps the same local account permissions. This is not a cross-account sandbox escape.",
        "recommendation": "Centralize normalization and containment. Anchor relative paths at the active KB; allow the configured GeneXus installation and an explicit GXMCP_EXTERNAL_IO_ROOT staging directory; reject traversal and report effective roots.",
        "acceptance": [
            "Relative, quoted, spaced, and mixed-slash Windows paths resolve from the active KB.",
            "Absolute paths and traversal outside roots return PathOutsideAllowedRoots before I/O.",
            "Blob, part, Object Text, baseline, profile, and XPZ paths use the same policy.",
            "Diagnostics report target and roots without secrets or customer KB values.",
        ],
    },
    {
        "id": "SEC-002",
        "severity": "medium",
        "title": "Escapar nome de objeto no titulo da Indexes webview",
        "baseline": [
            ("src/nexus-ide/src/webviews/IndexView.ts", "89", "toolbar interpolated objName directly into HTML"),
        ],
        "fixed": [
            ("src/nexus-ide/src/webviews/IndexView.ts", "4", "import escapeHtml from the shared HTML helper"),
            ("src/nexus-ide/src/webviews/IndexView.ts", "90", "Indexes for escaped objName"),
            ("src/nexus-ide/src/test/suite/htmlEscape.test.ts", "1-38", "tag and attribute breakout regression coverage"),
        ],
        "why": "The object name came from a workspace URI and was interpolated into the initial HTML document. A crafted name could terminate the text context and inject markup or script content.",
        "impact": "Opening the Indexes view for a maliciously named object could execute attacker-controlled webview markup in the extension context.",
        "condition": "The attacker must control or cause the object name to be present in the workspace URI and the user must open the Indexes view.",
        "recommendation": "Escape untrusted text before HTML interpolation and keep dynamic table cells on the existing escapeHtml path.",
        "acceptance": [
            "The title is escaped before assignment to webview HTML.",
            "Existing index names, attributes, and error messages remain escaped.",
            "The webview and HTML escape regression tests pass.",
        ],
    },
]


def html(value):
    return escape(str(value)).replace("\n", "<br/>")


def para(value, style):
    return Paragraph(html(value), style)


class Donut(Flowable):
    def __init__(self, values, labels, width=8.2 * cm, height=5.2 * cm):
        super().__init__()
        self.values = values
        self.labels = labels
        self.width = width
        self.height = height

    def wrap(self, avail_width, avail_height):
        return self.width, self.height

    def draw(self):
        total = sum(self.values) or 1
        cx, cy = 2.8 * cm, self.height / 2
        radius = 1.65 * cm
        start = 90
        for value, label in zip(self.values, self.labels):
            extent = 360 * value / total
            self.canv.setFillColor(colors.HexColor(COLOR[label]))
            self.canv.wedge(cx - radius, cy - radius, cx + radius, cy + radius, start, -extent, stroke=0, fill=1)
            start -= extent
        self.canv.setFillColor(colors.white)
        self.canv.circle(cx, cy, 0.92 * cm, stroke=0, fill=1)
        self.canv.setFillColor(colors.HexColor(COLOR["ink"]))
        self.canv.setFont("Helvetica-Bold", 15)
        self.canv.drawCentredString(cx, cy - 5, str(sum(self.values)))
        for index, (value, label) in enumerate(zip(self.values, self.labels)):
            y = self.height - 0.75 * cm - index * 0.65 * cm
            self.canv.setFillColor(colors.HexColor(COLOR[label]))
            self.canv.rect(5.3 * cm, y - 0.1 * cm, 0.3 * cm, 0.3 * cm, stroke=0, fill=1)
            self.canv.setFillColor(colors.HexColor(COLOR["ink"]))
            self.canv.setFont("Helvetica", 8.5)
            self.canv.drawString(5.8 * cm, y, "{} - {}".format(label, value))


class Bars(Flowable):
    def __init__(self, rows, width=8.2 * cm, height=5.2 * cm):
        super().__init__()
        self.rows = rows
        self.width = width
        self.height = height

    def wrap(self, avail_width, avail_height):
        return self.width, self.height

    def draw(self):
        left, bar_width = 3.9 * cm, 3.55 * cm
        for index, (label, value, kind) in enumerate(self.rows):
            y = self.height - 0.58 * cm - index * 0.72 * cm
            self.canv.setFillColor(colors.HexColor(COLOR["ink"]))
            self.canv.setFont("Helvetica", 7.3)
            self.canv.drawRightString(left - 0.18 * cm, y, label)
            self.canv.setFillColor(colors.HexColor(COLOR[kind]))
            self.canv.roundRect(left, y - 0.12 * cm, max(0.12 * cm, bar_width * value), 0.28 * cm, 2, stroke=0, fill=1)
            self.canv.setFillColor(colors.HexColor(COLOR["muted"]))
            self.canv.setFont("Helvetica", 7)
            self.canv.drawString(left + bar_width + 0.1 * cm, y, str(value))


def make_styles():
    base = getSampleStyleSheet()
    base.add(ParagraphStyle(name="CoverTitle", parent=base["Title"], fontName="Helvetica-Bold", fontSize=24, leading=29, textColor=colors.HexColor(COLOR["ink"]), alignment=TA_CENTER, spaceAfter=18))
    base.add(ParagraphStyle(name="CoverSub", parent=base["Normal"], fontName="Helvetica", fontSize=10.5, leading=15, textColor=colors.HexColor(COLOR["muted"]), alignment=TA_CENTER))
    base.add(ParagraphStyle(name="Section", parent=base["Heading1"], fontName="Helvetica-Bold", fontSize=16, leading=20, textColor=colors.HexColor(COLOR["ink"]), spaceBefore=5, spaceAfter=10))
    base.add(ParagraphStyle(name="Subsection", parent=base["Heading2"], fontName="Helvetica-Bold", fontSize=11, leading=14, textColor=colors.HexColor(COLOR["ink"]), spaceBefore=8, spaceAfter=5))
    base.add(ParagraphStyle(name="Body", parent=base["BodyText"], fontName="Helvetica", fontSize=9, leading=13, textColor=colors.HexColor(COLOR["ink"]), spaceAfter=6))
    base.add(ParagraphStyle(name="Small", parent=base["BodyText"], fontName="Helvetica", fontSize=7.5, leading=10, textColor=colors.HexColor(COLOR["muted"])))
    base.add(ParagraphStyle(name="AuditCode", parent=base["BodyText"], fontName="Courier", fontSize=6.7, leading=8.5, textColor=colors.HexColor(COLOR["ink"]), backColor=colors.HexColor(COLOR["soft"]), borderPadding=5, spaceAfter=5))
    base.add(ParagraphStyle(name="Issue", parent=base["BodyText"], fontName="Courier", fontSize=6.4, leading=8.0, textColor=colors.HexColor(COLOR["ink"]), leftIndent=4, rightIndent=4))
    base.add(ParagraphStyle(name="Table", parent=base["BodyText"], fontName="Helvetica", fontSize=7.3, leading=9.1, textColor=colors.HexColor(COLOR["ink"])))
    base.add(ParagraphStyle(name="TableHead", parent=base["BodyText"], fontName="Helvetica-Bold", fontSize=7.3, leading=9, textColor=colors.white))
    return base


def chip(severity, style):
    label = {"critical": "CRITICA", "high": "ALTA", "medium": "MEDIA", "low": "BAIXA"}[severity]
    result = Table([[para(label, style["TableHead"])]], colWidths=[1.45 * cm])
    result.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, -1), colors.HexColor(COLOR[severity])), ("LEFTPADDING", (0, 0), (-1, -1), 3), ("RIGHTPADDING", (0, 0), (-1, -1), 3), ("TOPPADDING", (0, 0), (-1, -1), 3), ("BOTTOMPADDING", (0, 0), (-1, -1), 3)]))
    return result


def issue(finding):
    severity = {"critical": "critica", "high": "alta", "medium": "media", "low": "baixa"}[finding["severity"]]
    evidence = "\n".join("- {}:{} - {}".format(path, line, snippet) for path, line, snippet in finding["baseline"])
    checks = "\n".join("- [ ] " + item for item in finding["acceptance"])
    return (
        "--- ISSUE {} ---\n".format(finding["id"])
        + "# [Seguranca] {}\n\n".format(finding["title"])
        + "Labels sugeridas: security, severity:{}\n\n".format(severity)
        + "## Descricao\n{}\n\n".format(finding["why"])
        + "## Evidencia\n{}\n\n".format(evidence)
        + "## Impacto\n{}\n\n".format(finding["impact"])
        + "## Condicao de explorabilidade\n{}\n\n".format(finding["condition"])
        + "## Sugestao de correcao\n{}\n\n".format(finding["recommendation"])
        + "## Criterios de aceite\n{}\n".format(checks)
        + "--- FIM ISSUE {} ---".format(finding["id"])
    )


def header_footer(canvas, doc):
    canvas.saveState()
    width, height = A4
    if doc.page > 1:
        canvas.setStrokeColor(colors.HexColor(COLOR["line"]))
        canvas.line(2 * cm, height - 1.35 * cm, width - 2 * cm, height - 1.35 * cm)
        canvas.setFont("Helvetica", 7.5)
        canvas.setFillColor(colors.HexColor(COLOR["muted"]))
        canvas.drawString(2 * cm, height - 1.1 * cm, "Auditoria de seguranca - Genexus18MCP")
    canvas.setStrokeColor(colors.HexColor(COLOR["line"]))
    canvas.line(2 * cm, 1.45 * cm, width - 2 * cm, 1.45 * cm)
    canvas.setFont("Helvetica", 7.5)
    canvas.setFillColor(colors.HexColor(COLOR["muted"]))
    canvas.drawString(2 * cm, 0.95 * cm, "Relatorio sanitizado - evidencias de codigo, sem dados de cliente")
    canvas.drawRightString(width - 2 * cm, 0.95 * cm, "Pagina {}".format(doc.page))
    canvas.restoreState()


def build_story(style):
    s = [
        Spacer(1, 2.0 * cm),
        para("Relatorio de Auditoria de Seguranca - Genexus18MCP", style["CoverTitle"]),
        para("Revisao das cinco categorias de seguranca adaptadas a C#/.NET, Node.js, TypeScript webview e MCP local.", style["CoverSub"]),
        Spacer(1, 1.1 * cm),
        HRFlowable(width="65%", thickness=2, color=colors.HexColor(COLOR["strong"]), hAlign="CENTER"),
        Spacer(1, 1.0 * cm),
        para("Data da auditoria: " + DATE, style["CoverSub"]),
        para("Base analisada: " + BASE, style["CoverSub"]),
        para("Escopo: Gateway .NET 10, Worker .NET Framework 4.8, CLI Node.js, extensao Nexus IDE, configuracoes, testes e historico Git.", style["CoverSub"]),
        Spacer(1, 1.0 * cm),
        para("Nota metodologica: isolamento e IDOR foram mapeados para leases de sessao e selecao de KB; permissao no navegador foi mapeada para a fronteira Gateway/Worker; chaves foram verificadas no codigo, configuracao, bundle e historico; XSS foi mapeado para sinks HTML dos webviews.", style["Body"]),
        PageBreak(),
    ]
    s += [
        para("Resumo executivo", style["Section"]),
        para("Foram confirmados dois achados: um de severidade alta envolvendo caminhos de filesystem e um de severidade media no titulo inicial da Indexes webview. Ambos foram corrigidos nesta branch e cobertos por validacao automatizada. Nao foram confirmados achados nas categorias de isolamento, permissao definida no navegador, IDOR ou chaves expostas.", style["Body"]),
    ]
    summary_rows = [
        [para("Severidade", style["TableHead"]), para("Quantidade", style["TableHead"]), para("Situacao", style["TableHead"])],
        [para("Alta", style["Table"]), para("1", style["Table"]), para("Corrigida", style["Table"])],
        [para("Media", style["Table"]), para("1", style["Table"]), para("Corrigida", style["Table"])],
        [para("Critica / baixa", style["Table"]), para("0", style["Table"]), para("Nenhuma confirmada", style["Table"])],
    ]
    summary = Table(summary_rows, colWidths=[5.3 * cm, 3 * cm, 6 * cm])
    summary.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, 0), colors.HexColor(COLOR["ink"])), ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor(COLOR["line"])), ("VALIGN", (0, 0), (-1, -1), "MIDDLE"), ("LEFTPADDING", (0, 0), (-1, -1), 6), ("RIGHTPADDING", (0, 0), (-1, -1), 6), ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5)]))
    s += [summary, Spacer(1, 0.35 * cm)]
    chart = Table([[Donut([1, 1], ["high", "medium"]), Bars([
        ("Isolamento", 0, "strong"), ("Permissoes", 0, "strong"), ("IDOR", 0, "strong"), ("Chaves", 0, "strong"), ("XSS", 1, "medium"), ("Filesystem", 1, "high")
    ])]], colWidths=[8.8 * cm, 8.8 * cm])
    chart.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "TOP"), ("BOX", (0, 0), (-1, -1), 0.4, colors.HexColor(COLOR["line"])), ("BACKGROUND", (0, 0), (-1, -1), colors.HexColor(COLOR["soft"])), ("LEFTPADDING", (0, 0), (-1, -1), 8), ("RIGHTPADDING", (0, 0), (-1, -1), 8), ("TOPPADDING", (0, 0), (-1, -1), 8), ("BOTTOMPADDING", (0, 0), (-1, -1), 8)]))
    s += [chart, Spacer(1, 0.2 * cm), para("Filesystem aparece como hardening adicional porque a lista original trata XSS como input frontend, enquanto este risco pertence a fronteira de I/O do MCP.", style["Small"])]

    s.append(para("Stack e controles verificados", style["Section"]))
    stack = [
        [para("Superficie", style["TableHead"]), para("Mapeamento e evidencia", style["TableHead"])],
        [para("Gateway e transporte", style["Table"]), para("ASP.NET/Kestrel; origem, Host, loopback e token validados em Program.Http.cs:619-678.", style["Table"])],
        [para("Isolamento de KB", style["Table"]), para("Leases vinculados a ownerScopeId, kbId, identity e contextGeneration em KbUseLeaseRegistry.cs:221-241; WorkerPool exige owner em :176-187.", style["Table"])],
        [para("Webviews", style["Table"]), para("TypeScript com escapeHtml nos dados dinamicos; LayoutView usa CSP e iframe sandbox em LayoutView.ts:14-18, 53-57.", style["Table"])],
        [para("Segredos", style["Table"]), para("Tokens ficam em ambiente; varredura de chave privada, AWS e JWT nao encontrou segredo real no codigo, bundle ou historico.", style["Table"])],
    ]
    st = Table(stack, colWidths=[4.2 * cm, 13.4 * cm])
    st.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, 0), colors.HexColor(COLOR["ink"])), ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor(COLOR["line"])), ("VALIGN", (0, 0), (-1, -1), "TOP"), ("LEFTPADDING", (0, 0), (-1, -1), 6), ("RIGHTPADDING", (0, 0), (-1, -1), 6), ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5)]))
    s += [st, PageBreak()]

    s.append(para("Cobertura por categoria", style["Section"]))
    categories = [
        [para("Categoria", style["TableHead"]), para("Resultado", style["TableHead"]), para("Evidencia de cobertura", style["TableHead"])],
        [para("1. Banco sem tranca", style["Table"]), para("Sem achado confirmado", style["Table"]), para("MCP local sem tenant remoto; a fronteira de KB e owner-bound por lease e a selecao e validada no Gateway.", style["Table"])],
        [para("2. Permissao no navegador", style["Table"]), para("Sem achado confirmado", style["Table"]), para("Nao ha papel administrativo confiado ao frontend; a superficie privilegiada passa pelo Gateway e guards de estado.", style["Table"])],
        [para("3. IDOR", style["Table"]), para("Sem achado confirmado", style["Table"]), para("Handlers percorridos por rotas e servicos; targets sao resolvidos na KB selecionada e o modelo local nao possui tenant separado do owner.", style["Table"])],
        [para("4. Chaves expostas", style["Table"]), para("Sem segredo real confirmado", style["Table"]), para("Tokens em ambiente; codigo, historico e bundle sem padrao confirmado de chave privada, AWS ou JWT.", style["Table"])],
        [para("5. Inputs sem tratamento (XSS)", style["Table"]), para("1 media - corrigida", style["Table"]), para("IndexView.ts:89 no baseline interpolava objName sem escape; a correcao esta em :90 e a suite htmlEscape cobre tag/atributo.", style["Table"])],
        [para("Hardening: filesystem", style["Table"]), para("1 alta - corrigida", style["Table"]), para("Export/import/blob/Object Text/baseline/profile/XPZ aceitavam Path.GetFullPath sem containment; a policy centraliza raizes.", style["Table"])],
    ]
    ct = Table(categories, colWidths=[4.25 * cm, 3.3 * cm, 10.05 * cm], repeatRows=1)
    ct.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, 0), colors.HexColor(COLOR["ink"])), ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor(COLOR["line"])), ("VALIGN", (0, 0), (-1, -1), "TOP"), ("BACKGROUND", (0, 5), (-1, 6), colors.HexColor("#FFF7ED")), ("LEFTPADDING", (0, 0), (-1, -1), 5), ("RIGHTPADDING", (0, 0), (-1, -1), 5), ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5)]))
    s += [ct, Spacer(1, 0.3 * cm), para("Nao aplicavel: RLS de Supabase, middleware de tenant remoto e papeis administrativos no navegador nao fazem parte desta aplicacao local.", style["Small"])]

    s.append(para("Pontos fortes", style["Section"]))
    for item in [
        "HTTP recusa bind nao-loopback sem GXMCP_HTTP_TOKEN e verifica Origin/Host (Program.Http.cs:594-640, 661-678).",
        "Leases validam owner, KB, identity e generation antes de operacoes stateful (WorkerPool.cs:176-187; KbUseLeaseRegistry.cs:221-241).",
        "Structure, History e Indexes escapam dados dinamicos; Layout e Diagram usam CSP e recursos locais.",
        "Suites executadas: 2.824 Worker, 1.704 Gateway, 135 CLI/live-contract e 111 extensao IDE, sem falhas.",
    ]:
        s.append(para("- " + item, style["Body"]))
    s += [para("Pontos fracos corrigidos", style["Section"]), para("Path.GetFullPath removia apenas ambiguidade de sintaxe, nao limitava o alvo. A nova politica comum protege leitura e escrita. A interpolacao direta no HTML inicial da Indexes webview agora usa escapeHtml. A selecao de KB nao foi alterada.", style["Body"]), PageBreak()]

    s.append(para("Achados detalhados", style["Section"]))
    detail = [[para("Severidade", style["TableHead"]), para("Arquivo:linha no baseline", style["TableHead"]), para("Descricao verificada", style["TableHead"])]]
    for finding in FINDINGS:
        locations = "\n".join("{}:{}".format(path, line) for path, line, _ in finding["baseline"])
        detail.append([chip(finding["severity"], style), para(locations, style["Table"]), para(finding["why"], style["Table"])])
    dt = Table(detail, colWidths=[2.1 * cm, 6.2 * cm, 9.3 * cm], repeatRows=1)
    dt.setStyle(TableStyle([("BACKGROUND", (0, 0), (-1, 0), colors.HexColor(COLOR["ink"])), ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor(COLOR["line"])), ("VALIGN", (0, 0), (-1, -1), "TOP"), ("LEFTPADDING", (0, 0), (-1, -1), 5), ("RIGHTPADDING", (0, 0), (-1, -1), 5), ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5)]))
    s.append(dt)
    for finding in FINDINGS:
        s.append(para("{} - {}".format(finding["id"], finding["title"]), style["Subsection"]))
        s += [para("Por que e exploravel: " + finding["why"], style["Body"]), para("Impacto: " + finding["impact"], style["Body"]), para("Condicao: " + finding["condition"], style["Body"])]
        baseline = "\n".join("{}:{} - {}".format(a, b, c) for a, b, c in finding["baseline"])
        fixed = "\n".join("{}:{} - {}".format(a, b, c) for a, b, c in finding["fixed"])
        s += [para("Evidencia do baseline:\n" + baseline, style["AuditCode"]), para("Correcao verificada:\n" + fixed, style["AuditCode"])]

    s.append(para("Recomendacoes priorizadas", style["Section"]))
    for item in [
        "P1 - manter IUserFilePathPolicy como unica porta de entrada para arquivos fornecidos por MCP, com UserFilePathPolicy criada no composition root, e adicionar um teste para cada novo parametro de path.",
        "P2 - executar o gate de PR deste diretorio em copia limpa baseada no origin/main, com SDK explicitamente selecionado.",
        "P3 - repetir a varredura de segredos em codigo, historico e bundle; manter tokens apenas em ambiente.",
        "P4 - acompanhar os sete avisos de npm audit somente na arvore de desenvolvimento do IDE; npm audit --omit=dev nao encontrou vulnerabilidades de runtime.",
    ]:
        s.append(para(item, style["Body"]))

    s += [PageBreak(), para("ISSUES PARA O GITHUB", style["Section"]), para("Blocos completos, sanitizados e prontos para copiar. Os achados foram corrigidos nesta branch; mantenha-os como registro ou rastreie a correcao no repositorio upstream.", style["Body"])]
    for finding in FINDINGS:
        s.append(KeepTogether([para(issue(finding), style["Issue"]), Spacer(1, 0.35 * cm)]))
    s += [para("Reprodutibilidade e limites", style["Section"]), para("Auditoria feita na revisao " + BASE + ". Nenhuma KB real foi aberta, alterada, gerada ou publicada durante a leitura; testes usam dados temporarios e anonimizados. O Worker foi compilado contra GeneXus 18 U16 local. Sem SDK configurado, o preflight retorna GXMCP_SDK_PATH_MISSING.", style["Body"])]
    return s


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default=os.path.join(os.path.dirname(__file__), "relatorio-auditoria-seguranca.pdf"))
    args = parser.parse_args()
    output = os.path.abspath(args.output)
    os.makedirs(os.path.dirname(output), exist_ok=True)
    style = make_styles()
    doc = BaseDocTemplate(output, pagesize=A4, leftMargin=2 * cm, rightMargin=2 * cm, topMargin=1.75 * cm, bottomMargin=1.8 * cm, title="Relatorio de Auditoria de Seguranca - Genexus18MCP", author="Codex")
    frame = Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height, id="normal")
    doc.addPageTemplates([PageTemplate(id="audit", frames=[frame], onPage=header_footer)])
    doc.build(build_story(style))
    print(output)


if __name__ == "__main__":
    main()
