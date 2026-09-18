from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.units import mm
from reportlab.graphics.shapes import Drawing
from reportlab.graphics.charts.barcharts import VerticalBarChart
from reportlab.platypus import KeepTogether, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle


OUTPUT = Path(__file__).with_name("relatorio-auditoria-seguranca.pdf")


def build_report():
    styles = getSampleStyleSheet()
    styles.add(ParagraphStyle(name="TitlePt", parent=styles["Title"], alignment=TA_CENTER, fontSize=20, leading=24, textColor=colors.HexColor("#17324D"), spaceAfter=8))
    styles.add(ParagraphStyle(name="SubtitlePt", parent=styles["Normal"], alignment=TA_CENTER, fontSize=9, leading=12, textColor=colors.HexColor("#536273"), spaceAfter=16))
    styles.add(ParagraphStyle(name="HeadingPt", parent=styles["Heading2"], fontSize=13, leading=16, textColor=colors.HexColor("#17324D"), spaceBefore=10, spaceAfter=6))
    styles.add(ParagraphStyle(name="BodyPt", parent=styles["BodyText"], fontSize=9.2, leading=13, textColor=colors.HexColor("#202A33"), spaceAfter=6))
    styles.add(ParagraphStyle(name="SmallPt", parent=styles["BodyText"], fontSize=7.8, leading=10, textColor=colors.HexColor("#536273")))
    styles.add(ParagraphStyle(name="CodePt", parent=styles["Code"], fontSize=7.4, leading=9, leftIndent=6, rightIndent=6, backColor=colors.HexColor("#F2F5F7"), borderColor=colors.HexColor("#D5DEE6"), borderWidth=0.5, borderPadding=5))

    doc = SimpleDocTemplate(str(OUTPUT), pagesize=A4, rightMargin=17 * mm, leftMargin=17 * mm, topMargin=15 * mm, bottomMargin=15 * mm, title="Relatorio de auditoria de seguranca - Genexus18MCP", author="Codex e Claude Opus")
    p = lambda text, style: Paragraph(text, styles[style])
    story = [
        p("Relatorio de auditoria de seguranca", "TitlePt"),
        p("Genexus18MCP - revisao estatica com dados anonimizados - 17/09/2026", "SubtitlePt"),
        p("Conclusao executiva", "HeadingPt"),
        p("Nenhuma vulnerabilidade foi confirmada nesta rodada. O gateway possui controles de isolamento por sessao e KB, autenticacao opcional por token para HTTP, verificacao de Host e Origin, limites de payload, redacao de segredos em logs e filas limitadas. Dois pontos foram registrados como melhoria documental, nao como falhas exploraveis: atualizar a tabela de versoes suportadas em SECURITY.md e explicitar o endpoint HTTP legado no modelo de ameacas.", "BodyPt"),
        p("Cobertura da revisao", "HeadingPt"),
        p("A revisao foi feita sobre a revisao de referencia do upstream e incluiu SECURITY.md, CONTRIBUTING.md, Program.cs, Program.Http.cs, protocolos HTTP MCP, registro de subscriptions, resolucao de KB, routers, dispatcher e CLI. A verificacao cruzada foi feita com Claude Opus em modo somente leitura. Nenhum segredo, token, caminho privado de KB ou dado de cliente foi incluido neste documento.", "BodyPt"),
    ]

    cell = lambda text: Paragraph(text, styles["SmallPt"])
    data = [
        [p("Area", "SmallPt"), p("Resultado", "SmallPt"), p("Evidencia resumida", "SmallPt")],
        [cell("Isolamento de sessao e KB"), cell("Sem achado"), cell("Contexto AsyncLocal, leases e resolucao explicita de KB; testes de isolamento existentes.")],
        [cell("Autenticacao e browser"), cell("Sem achado"), cell("Token HTTP, Host loopback, Origin allowlist e bloqueio de bind nao-loopback sem token.")],
        [cell("IDOR e selecao de objeto"), cell("Sem achado"), cell("Routers delegam a contexto de KB e a operacoes com verificacao de escopo; nenhum bypass reproduzivel.")],
        [cell("Segredos e logs"), cell("Sem achado"), cell("Redacao de password, token, secret, API key, authorization e credential; token apenas por ambiente.")],
        [cell("XSS, eval e bundles"), cell("Sem achado"), cell("Nenhum eval ou sink comprovado no caminho MCP auditado; bundle visual de terceiro tratado como dependencia.")],
    ]
    table = Table(data, colWidths=[39 * mm, 27 * mm, 105 * mm], repeatRows=1)
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#17324D")),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("FONTNAME", (0, 0), (-1, 0), "Helvetica-Bold"),
        ("FONTSIZE", (0, 0), (-1, -1), 7.8),
        ("LEADING", (0, 0), (-1, -1), 10),
        ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor("#C8D2DB")),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, colors.HexColor("#F2F5F7")]),
        ("LEFTPADDING", (0, 0), (-1, -1), 5),
        ("RIGHTPADDING", (0, 0), (-1, -1), 5),
        ("TOPPADDING", (0, 0), (-1, -1), 5),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
    ]))
    story.extend([table, Spacer(1, 8)])

    story.append(p("Mapa de cobertura", "HeadingPt"))
    chart = VerticalBarChart()
    chart.width = 150 * mm
    chart.height = 48 * mm
    chart.data = [[1, 1, 1, 1, 1]]
    chart.categoryAxis.categoryNames = ["Sessao", "Browser", "IDOR", "Segredos", "XSS/eval"]
    chart.valueAxis.valueMin = 0
    chart.valueAxis.valueMax = 1
    chart.valueAxis.valueStep = 1
    chart.valueAxis.labels.fontSize = 7
    chart.categoryAxis.labels.fontSize = 7
    chart.bars[0].fillColor = colors.HexColor("#2A9D8F")
    chart.bars[0].strokeColor = colors.HexColor("#2A9D8F")
    drawing = Drawing(150 * mm, 48 * mm)
    drawing.add(chart)
    story.extend([Spacer(1, 4), drawing, p("1 = area revisada sem vulnerabilidade comprovada nesta rodada.", "SmallPt")])

    story.extend([
        p("Pontos que exigem acompanhamento", "HeadingPt"),
        p("1. Atualizar SECURITY.md para refletir a versao/minor realmente suportada, sem declarar uma versao que nao tenha sido validada pelo mantenedor. 2. Documentar explicitamente o endpoint HTTP legado 127.0.0.1:5000/mcp no threat model e no procedimento de hardening. 3. Manter o preflight completo antes de cada push e executar a lane Worker quando o SDK GeneXus estiver instalado no ambiente de validacao.", "BodyPt"),
        p("Limitacoes e honestidade da evidencia", "HeadingPt"),
        p("A auditoria estatica nao substitui teste dinamico contra uma instancia HTTP configurada, um cliente MCP hostil ou uma KB real. A lane Worker nao foi executada localmente porque o SDK GeneXus nao esta instalado neste ambiente. Por isso o resultado e: nenhum achado comprovado, com validacao operacional do Gateway e pontos documentais pendentes.", "BodyPt"),
        KeepTogether([
            p("Anexo - issue GitHub", "HeadingPt"),
            p("Nao abrir issue de vulnerabilidade: nao houve achado reproduzivel. Registro de melhoria documental para triagem interna:", "BodyPt"),
            p("Titulo: docs(security): atualizar threat model e versao suportada<br/><br/>## Contexto<br/>A auditoria estatica de 17/09/2026 encontrou dois pontos documentais: a tabela de versoes de SECURITY.md esta fixa em 3.0.x e o modelo de ameacas nao nomeia o endpoint HTTP legado.<br/><br/>## Resultado<br/>Nenhuma vulnerabilidade foi confirmada. O ajuste deve alinhar a documentacao com a configuracao real do release e explicar o hardening do endpoint legado.<br/><br/>## Criterios de aceite<br/>- A versao suportada e derivada da politica de release vigente.<br/>- O endpoint legado, token, bind loopback e Origin/Host estao documentados.<br/>- Nenhum segredo ou dado de cliente e incluido.<br/>- Markdown/link checks e preflight passam.", "CodePt"),
        ]),
    ])

    def footer(canvas, document):
        canvas.saveState()
        canvas.setFont("Helvetica", 7)
        canvas.setFillColor(colors.HexColor("#536273"))
        canvas.drawString(17 * mm, 8 * mm, "Genexus18MCP - auditoria estatica")
        canvas.drawRightString(193 * mm, 8 * mm, f"Pagina {document.page}")
        canvas.restoreState()

    doc.build(story, onFirstPage=footer, onLaterPages=footer)


if __name__ == "__main__":
    build_report()
    print(OUTPUT)
