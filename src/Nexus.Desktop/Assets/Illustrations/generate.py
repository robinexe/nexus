# Gera Hardware.xaml e Modules.xaml (DrawingImage 160x120, cores literais da "bancada de telemetria").
# Uso: python3 generate.py <pasta de saída> [previa.svg]   (a prévia SVG serve para conferir o desenho no Linux: rsvg-convert)
import sys, os, math
OUT = sys.argv[1]
BG="#0E1320"; PLATE="#131B2C"; PLATE_LINE="#1E2638"; BODY="#1F2840"; BODY_HI="#2A3550"; BODY_LO="#182033"; EDGE="#3A4868"
CY="#4FD1E8"; AM="#FFB547"; GR="#5EE6A0"; RD="#FF6B7A"; TX="#E8ECF5"; DIM="#5C6785"
CY_S="#334FD1E8"; AM_S="#33FFB547"; GR_S="#335EE6A0"; RD_S="#33FF6B7A"; SHADOW="#990A0E18"; NONE="#00000000"

def d(g, fill=None, stroke=None, w=2.0, dash=None, cap="Round"):
    return (g, fill, stroke, w, dash, cap)

def circle(cx, cy, r):
    return f"M{cx-r},{cy} a{r},{r} 0 1 0 {2*r},0 a{r},{r} 0 1 0 {-2*r},0"

def ellipse(cx, cy, rx, ry):
    return f"M{cx-rx},{cy} a{rx},{ry} 0 1 0 {2*rx},0 a{rx},{ry} 0 1 0 {-2*rx},0"

def rect(x1, y1, x2, y2):
    return f"M{x1},{y1} H{x2} V{y2} H{x1} Z"

def backdrop():
    return [
        d("M0,0 H160 V120 H0 Z", fill=NONE),
        d(ellipse(80,108,40,5), fill=SHADOW),
        d(circle(80,58,46), fill=PLATE, stroke=PLATE_LINE, w=1),
        d("M80,4 V9 M80,107 V112 M26,58 H31 M129,58 H134", stroke=BODY_HI, w=1.5),
    ]

def teeth(r, w, n):
    circ = 2*math.pi*r / w
    period = circ / n
    return f"{period/2:.3f} {period/2:.3f}"

I = {}

# ---------------------------------------------------------------- hardware
I["Illo.Cpu"] = [
    d(rect(55,33,105,83), fill=BODY, stroke=CY, w=2),
    d("M63,33 V25 M71.5,33 V25 M80,33 V25 M88.5,33 V25 M97,33 V25 M63,83 V91 M71.5,83 V91 M80,83 V91 M88.5,83 V91 M97,83 V91 "
      "M55,41 H47 M55,49.5 H47 M55,58 H47 M55,66.5 H47 M55,75 H47 M105,41 H113 M105,49.5 H113 M105,58 H113 M105,66.5 H113 M105,75 H113", stroke=CY, w=2),
    d(rect(66,44,94,72), fill=AM_S, stroke=AM, w=2),
    d("M80,44 V72 M66,58 H94", stroke=AM, w=1.2),
    d(circle(59.5,37.5,1.6), fill=TX),
]
I["Illo.Gpu"] = [
    d(rect(24,36,30,90), fill=BODY_HI, stroke=EDGE, w=1.5),
    d("M30,40 H130 A4,4 0 0 1 134,44 V76 A4,4 0 0 1 130,80 H30 Z", fill=BODY, stroke=CY, w=2),
    d(circle(60,60,14), fill=BODY_LO, stroke=CY, w=2),
    d(circle(102,60,14), fill=BODY_LO, stroke=CY, w=2),
    d("M60,60 Q66,50 60,47 M60,60 Q70,64 72,58 M60,60 Q52,68 55,72 M102,60 Q108,50 102,47 M102,60 Q112,64 114,58 M102,60 Q94,68 97,72", stroke=CY, w=1.6),
    d(circle(60,60,3.5) + " " + circle(102,60,3.5), fill=AM),
    d(rect(46,80,96,87), fill=AM_S, stroke=AM, w=1.5),
    d("M52,80 V87 M58,80 V87 M64,80 V87 M70,80 V87 M76,80 V87 M82,80 V87 M88,80 V87", stroke=AM, w=1),
    d("M122,46 H128", stroke=GR, w=2.5),
]
I["Illo.Ram"] = [
    d("M34,48 H126 M34,88 H126", stroke=CY, w=2),
    d("M34,44 V52 M126,44 V52 M34,84 V92 M126,84 V92", stroke=CY, w=2),
    d(rect(38,22,122,46), fill=BODY, stroke=CY, w=2),
    d(rect(44,27,56,38) + " " + rect(60,27,72,38) + " " + rect(88,27,100,38) + " " + rect(104,27,116,38), fill=BODY_HI, stroke=EDGE, w=1),
    d("M42,43 H76 M84,43 H118", stroke=AM, w=3, dash="0.5 0.6", cap="Flat"),
    d(rect(38,62,122,86), fill=NONE, stroke=EDGE, w=1.6, dash="3 2.5"),
    d("M80,68 V80 M74,74 H86", stroke=DIM, w=2),
    d(circle(29,34,3), fill=GR),
    d(circle(29,74,3), fill=NONE, stroke=AM, w=1.6),
]
I["Illo.Disk"] = [
    d(rect(40,30,120,37), fill=BODY_LO, stroke=EDGE, w=1),
    d(rect(40,30,94,37), fill=AM),
    d("M24,48 H132 A4,4 0 0 1 136,52 V68 A4,4 0 0 1 132,72 H24 Z", fill=BODY, stroke=CY, w=2),
    d("M24,51 V69", stroke=AM, w=4, dash="0.5 0.5", cap="Flat"),
    d(rect(38,53,54,67), fill=BODY_HI, stroke=CY, w=1.5),
    d(rect(62,52,88,68) + " " + rect(94,52,120,68), fill=BODY_HI, stroke=EDGE, w=1),
    d(circle(130,60,3.5), fill=BG, stroke=EDGE, w=1.5),
    d(circle(46,60,2), fill=GR),
]
I["Illo.Pc"] = [
    d(rect(22,30,86,76), fill=BODY, stroke=CY, w=2),
    d(rect(27,35,81,71), fill=BG),
    d("M30,62 H78", stroke=CY, w=1, dash="2 2"),
    d("M30,62 H42 L45,58 L48,62 H58 L61,44 L64,62 H78", stroke=AM, w=1.8),
    d("M50,76 L47,88 H61 L58,76", fill=BODY_HI, stroke=EDGE, w=1.5),
    d(rect(96,22,132,98), fill=BODY, stroke=CY, w=2),
    d("M101,27 V93", stroke=AM, w=2),
    d(circle(116,44,9) + " " + circle(116,70,9), fill=BODY_LO, stroke=CY, w=1.6),
    d(circle(116,44,2.5) + " " + circle(116,70,2.5), fill=AM),
    d("M110,88 H122", stroke=EDGE, w=2),
    d(circle(126,28,1.6), fill=GR),
]

# ---------------------------------------------------------------- módulos
I["Illo.Cleaning"] = [
    d("M112,18 L80,62", stroke=EDGE, w=6),
    d("M112,18 L80,62", stroke=CY, w=2),
    d("M66,60 L92,70 L84,94 L50,84 Z", fill=AM_S, stroke=AM, w=2),
    d("M60,66 L74,72 M58,74 L70,90 M66,77 L78,93 M76,76 L82,92", stroke=AM, w=1.4),
    d(circle(40,92,2.4) + " " + circle(32,84,1.6) + " " + circle(46,100,1.6), fill=CY),
    d(circle(38,74,1.4) + " " + circle(28,96,1.2), fill=GR),
    d("M118,74 V86 M112,80 H124 M126,60 V66 M123,63 H129", stroke=GR, w=1.6),
]
I["Illo.Programs"] = [
    d("M80,14 V30 M74,24 L80,30 L86,24", stroke=AM, w=2.2),
    d("M80,38 L106,51 L80,64 L54,51 Z", fill=BODY_HI, stroke=CY, w=2),
    d("M54,51 L80,64 V94 L54,81 Z", fill=BODY, stroke=CY, w=2),
    d("M80,64 L106,51 V81 L80,94 Z", fill=BODY_LO, stroke=CY, w=2),
    d("M67,44.5 L93,57.5", stroke=AM, w=3),
    d("M38,72 L50,78 L38,84 L26,78 Z", fill=BODY_HI, stroke=CY, w=1.5),
    d("M26,78 L38,84 V98 L26,92 Z", fill=BODY, stroke=CY, w=1.5),
    d("M38,84 L50,78 V92 L38,98 Z", fill=BODY_LO, stroke=CY, w=1.5),
    d("M120,74 L130,79 L120,84 L110,79 Z", fill=BODY_HI, stroke=EDGE, w=1.2),
    d("M110,79 L120,84 V95 L110,90 Z M120,84 L130,79 V90 L120,95 Z", fill=BODY, stroke=EDGE, w=1.2),
]
I["Illo.Startup"] = [
    d("M80,16 C93,29 95,50 91,70 H69 C65,50 67,29 80,16 Z", fill=BODY, stroke=CY, w=2),
    d(circle(80,42,6.5), fill=CY_S, stroke=CY, w=2),
    d("M69.5,56 L58,78 L70,73 Z M90.5,56 L102,78 L90,73 Z", fill=BODY_HI, stroke=CY, w=1.8),
    d("M73,72 Q80,100 87,72 Z", fill=AM_S, stroke=AM, w=2),
    d("M77,73 Q80,88 83,73 Z", fill=AM),
    d("M48,82 V96 M112,78 V92 M40,62 V70 M120,58 V66", stroke=CY, w=1.6),
    d(circle(80,42,2), fill=TX),
]
I["Illo.Services"] = [
    d(circle(66,66,22), fill=NONE, stroke=CY, w=7, dash=teeth(22,7,10), cap="Flat"),
    d(circle(66,66,19), fill=BODY, stroke=CY, w=2),
    d(circle(66,66,7), fill=BG, stroke=CY, w=2),
    d(circle(106,42,14), fill=NONE, stroke=AM, w=5, dash=teeth(14,5,8), cap="Flat"),
    d(circle(106,42,12), fill=BODY_HI, stroke=AM, w=2),
    d(circle(106,42,4), fill=BG, stroke=AM, w=1.6),
    d(circle(110,88,8), fill=NONE, stroke=EDGE, w=4, dash=teeth(8,4,6), cap="Flat"),
    d(circle(110,88,6.5), fill=BODY, stroke=EDGE, w=1.4),
]
I["Illo.Privacy"] = [
    d("M32,56 Q80,18 128,56 Q80,94 32,56 Z", fill=BODY, stroke=CY, w=2),
    d(circle(80,56,15), fill=CY_S, stroke=CY, w=2),
    d(circle(80,56,6.5), fill=BG),
    d(circle(84.5,51.5,2.2), fill=TX),
    d("M110,72 L124,77 V87 C124,95 117,99 110,101 C103,99 96,95 96,87 V77 Z", fill=BODY_HI, stroke=AM, w=2),
    d("M103.5,87 L108,91.5 L116.5,82.5", stroke=AM, w=2.2),
]
I["Illo.Security"] = [
    d("M30,60 A50,50 0 0 1 56,16", stroke=AM, w=2),
    d(circle(56,16,2.6), fill=AM),
    d("M80,18 L112,30 V58 C112,80 97,95 80,102 C63,95 48,80 48,58 V30 Z", fill=BODY, stroke=CY, w=2.2),
    d("M80,27 L104,36 V58 C104,75 93,87 80,93 C67,87 56,75 56,58 V36 Z", fill=NONE, stroke=BODY_HI, w=1.5),
    d("M66,60 L76,70 L96,48", stroke=GR, w=4),
]
I["Illo.Scanner"] = [
    d("M50,22 H90 L104,36 V98 H50 Z", fill=BODY, stroke=CY, w=2),
    d("M90,22 V36 H104", fill=BODY_HI, stroke=CY, w=2),
    d("M58,48 H94 M58,58 H88 M58,68 H80 M58,78 H76 M58,88 H72", stroke=EDGE, w=2.2),
    d(circle(104,76,17), fill=AM_S, stroke=AM, w=3),
    d("M91,76 H117", stroke=AM, w=1.4),
    d("M116.5,88.5 L131,103", stroke=AM, w=6),
    d(circle(98,70,2.6), fill=GR),
]
I["Illo.Network"] = [
    d("M50.3,48.3 A42,42 0 0 1 109.7,48.3", stroke=EDGE, w=4),
    d("M60.2,58.2 A28,28 0 0 1 99.8,58.2", stroke=CY, w=4),
    d("M70.1,68.1 A14,14 0 0 1 89.9,68.1", stroke=CY, w=4),
    d(circle(80,80,5), fill=AM),
    d(rect(56,90,104,100), fill=BODY, stroke=CY, w=1.8),
    d("M64,95 H66 M70,95 H72 M76,95 H78", stroke=GR, w=2),
    d("M96,95 H98", stroke=AM, w=2),
]
I["Illo.Drivers"] = [
    d(rect(46,46,90,90), fill=BODY, stroke=CY, w=2),
    d(rect(58,58,78,78), fill=BODY_HI, stroke=CY, w=1.5),
    d("M46,54 H38 M46,63 H38 M46,72 H38 M46,81 H38 M90,54 H98 M90,63 H98 M90,72 H98 M90,81 H98", stroke=CY, w=2),
    d("M98,54 C108,54 110,52 110,48", stroke=AM, w=1.5, dash="2 2"),
    d(circle(110,34,14), fill=AM_S, stroke=AM, w=2),
    d("M110,42 V26 M103,33 L110,26 L117,33", stroke=AM, w=2.4),
    d(circle(68,68,2.6), fill=GR),
]
I["Illo.Measure"] = [
    d("M38,80 A42,42 0 1 1 122,80", stroke=BODY_HI, w=8),
    d("M38,80 A42,42 0 0 1 104.7,46", stroke=AM, w=8),
    d("M44,80 H48 M112,80 H116 M80,44 V48 M54.5,54.5 L57.3,57.3 M105.5,54.5 L102.7,57.3", stroke=DIM, w=1.6),
    d("M80,80 L100,52", stroke=TX, w=2.6),
    d(circle(80,80,5.5), fill=BODY_HI, stroke=TX, w=1.6),
    d("M40,100 H58 L61,96 L64,100 H76 L79,91 L82,100 H120", stroke=CY, w=1.8),
]
I["Illo.Monitor"] = [
    d("M34,26 H126 A4,4 0 0 1 130,30 V78 A4,4 0 0 1 126,82 H34 A4,4 0 0 1 30,78 V30 A4,4 0 0 1 34,26 Z", fill=BODY, stroke=CY, w=2),
    d(rect(36,32,124,76), fill=BG),
    d("M36,46 H124 M36,61 H124", stroke=PLATE_LINE, w=1),
    d("M40,70 L52,62 L62,66 L72,50 L82,56 L92,42 L102,48 L116,38 V76 H40 Z", fill=AM_S),
    d("M40,70 L52,62 L62,66 L72,50 L82,56 L92,42 L102,48 L116,38", stroke=AM, w=2),
    d(circle(116,38,6), fill=AM_S),
    d(circle(116,38,2.8), fill=AM),
    d("M72,82 L68,96 H92 L88,82", fill=BODY_HI, stroke=EDGE, w=1.5),
]
I["Illo.Automation"] = [
    d("M46,98 L98,46", stroke=EDGE, w=8),
    d("M46,98 L90,54", stroke=CY, w=2.4),
    d("M90,54 L98,46", stroke=AM, w=8),
    d("M114,18 Q116,30 128,32 Q116,34 114,46 Q112,34 100,32 Q112,30 114,18 Z", fill=AM),
    d("M128,58 Q129,63 134,64 Q129,65 128,70 Q127,65 122,64 Q127,63 128,58 Z", fill=CY),
    d("M84,18 Q85,22 89,23 Q85,24 84,28 Q83,24 79,23 Q83,22 84,18 Z", fill=GR),
    d(circle(108,66,1.8) + " " + circle(70,34,1.4) + " " + circle(124,86,1.4), fill=TX),
]
I["Illo.Assistant"] = [
    d("M36,90 C52,86 52,74 66,72 C82,70 82,58 96,56 C110,54 112,40 124,34", stroke=EDGE, w=2, dash="2.5 2.5"),
    d(circle(36,90,8), fill=GR_S, stroke=GR, w=2),
    d("M32,90 L35,93 L40,87", stroke=GR, w=2),
    d(circle(66,72,8), fill=GR_S, stroke=GR, w=2),
    d("M62,72 L65,75 L70,69", stroke=GR, w=2),
    d(circle(96,56,10), fill=AM_S, stroke=AM, w=2.4),
    d(circle(96,56,4), fill=AM),
    d(circle(124,34,7), fill=BODY, stroke=EDGE, w=2),
    d("M124,27 V10", stroke=CY, w=2),
    d("M124,10 L138,14 L124,18 Z", fill=CY),
]
I["Illo.MemoryDoctor"] = [
    d("M32,98 H128 M32,98 V22", stroke=EDGE, w=2),
    d("M32,50 H128", stroke=AM, w=1.5, dash="3 2.5"),
    d("M36,90 L50,86 L62,82 L74,74 L86,68 L98,56 L110,46 L122,32 V98 H36 Z", fill=RD_S),
    d("M36,90 L50,86 L62,82 L74,74 L86,68 L98,56 L110,46 L122,32", stroke=RD, w=2.4),
    d(circle(122,32,6.5), fill=RD_S),
    d(circle(122,32,3), fill=RD),
    d(rect(42,28,70,40), fill=BODY, stroke=CY, w=1.5),
    d("M46,40 V43 M51,40 V43 M56,40 V43 M61,40 V43 M66,40 V43", stroke=AM, w=1.2),
]
I["Illo.Settings"] = [
    d("M40,38 H120 M40,60 H120 M40,82 H120", stroke=BODY_HI, w=4),
    d("M40,38 H96", stroke=AM, w=4),
    d("M40,60 H62", stroke=CY, w=4),
    d("M40,82 H106", stroke=CY, w=4),
    d(circle(96,38,8), fill=BG, stroke=AM, w=3),
    d(circle(62,60,8), fill=BG, stroke=CY, w=3),
    d(circle(106,82,8), fill=BG, stroke=CY, w=3),
]
I["Illo.Diagnostics"] = [
    d(circle(80,58,34), fill=BODY, stroke=CY, w=2),
    d("M36,60 H58 L64,48 L72,76 L80,38 L88,70 L94,60 H124", stroke=AM, w=2.6),
    d(circle(124,60,3), fill=AM),
    d("M98,30 L104,24 M62,30 L56,24", stroke=EDGE, w=2),
]
I["Illo.History"] = [
    d(circle(80,58,30), fill=BODY, stroke=CY, w=2),
    d("M80,34 V38 M80,78 V82 M56,58 H60 M100,58 H104", stroke=DIM, w=1.6),
    d("M80,58 V42 M80,58 L92,64", stroke=AM, w=3),
    d(circle(80,58,3), fill=AM),
    d("M38,74 A44,44 0 0 0 66,100", stroke=GR, w=2.2),
    d("M59,103 L66,100 L64.5,93", stroke=GR, w=2.2),
]

GROUPS = {
    "Hardware.xaml": ("ilustrações de hardware: PC, processador, placa de vídeo, memória RAM (1 de 2 slots ocupados) e SSD.",
                      ["Illo.Pc", "Illo.Cpu", "Illo.Gpu", "Illo.Ram", "Illo.Disk"]),
    "Modules.xaml": ("ilustrações dos módulos (limpeza, programas, inicialização, serviços, privacidade, segurança, verificar arquivos, rede, drivers, medir, monitor, automático, assistente, memória-diagnóstico, ajustes, diagnóstico, histórico).",
                     ["Illo.Cleaning", "Illo.Programs", "Illo.Startup", "Illo.Services", "Illo.Privacy", "Illo.Security", "Illo.Scanner",
                      "Illo.Network", "Illo.Drivers", "Illo.Measure", "Illo.Monitor", "Illo.Automation", "Illo.Assistant", "Illo.MemoryDoctor",
                      "Illo.Settings", "Illo.Diagnostics", "Illo.History"]),
}

def drawing(item, ind):
    g, fill, stroke, w, dash, cap = item
    attrs = f'Geometry="{g}"'
    if fill: attrs += f' Brush="{fill}"'
    if not stroke:
        return f'{ind}<GeometryDrawing {attrs}/>'
    dash_xml = f'<Pen.DashStyle><DashStyle Dashes="{dash}"/></Pen.DashStyle>' if dash else ""
    pen = (f'<Pen Brush="{stroke}" Thickness="{w}" StartLineCap="{cap}" EndLineCap="{cap}" DashCap="{cap}" LineJoin="Round">'
           f'{dash_xml}</Pen>')
    return f'{ind}<GeometryDrawing {attrs}><GeometryDrawing.Pen>{pen}</GeometryDrawing.Pen></GeometryDrawing>'

for fname, (desc, keys) in GROUPS.items():
    lines = ["<!--",
             f"  NEXUS — {desc}",
             "  Desenhos vetoriais próprios (nada baixado de terceiros), 160x120, no idioma da \"bancada de telemetria\": placa redonda",
             "  escura com marcas de mostrador, corpo grafite (#1F2840), traço ciano (#4FD1E8) = estrutura/sinal, âmbar (#FFB547) = a",
             "  parte medida/ativa, verde (#5EE6A0) = ok, vermelho (#FF6B7A) = problema. Cores literais de propósito: o dicionário não",
             "  depende da ordem de carga. Uso: <Image Source=\"{StaticResource Illo.Cpu}\" Width=\"160\"/>, ou nas propriedades",
             "  Illustration de HeroPanel/StatTile/EmptyState. Catálogo e mapa por módulo: docs/10-design-system.md.",
             "  Gerado por generate.py (mesma pasta): edite a lista de traços lá e rode de novo; não edite este arquivo à mão.",
             "-->",
             '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
             '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
             '                    xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options"',
             '                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"',
             '                    mc:Ignorable="po">']
    for k in keys:
        lines.append("")
        lines.append(f'  <DrawingImage x:Key="{k}" po:Freeze="True">')
        lines.append("    <DrawingImage.Drawing>")
        lines.append("      <DrawingGroup>")
        for item in backdrop() + I[k]:
            lines.append(drawing(item, "        "))
        lines.append("      </DrawingGroup>")
        lines.append("    </DrawingImage.Drawing>")
        lines.append("  </DrawingImage>")
    lines.append("</ResourceDictionary>")
    with open(os.path.join(OUT, fname), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

# Prévia SVG (só para conferir o desenho em Linux): mesma geometria, convertida.
if len(sys.argv) > 2:
    import re
    prev = sys.argv[2]
    def svgcolor(c):
        if c is None: return "none", 1
        if len(c) == 9: return "#" + c[3:], int(c[1:3], 16) / 255
        return c, 1
    tiles = []
    allkeys = [k for _, (_, ks) in GROUPS.items() for k in ks]
    for idx, k in enumerate(allkeys):
        x = (idx % 6) * 170; y = (idx // 6) * 150
        parts = [f'<g transform="translate({x},{y})"><rect width="160" height="120" fill="{BG}"/>']
        for g, fill, stroke, w, dash, cap in backdrop() + I[k]:
            f, fo = svgcolor(fill); s, so = svgcolor(stroke)
            capsvg = {"Round": "round", "Flat": "butt"}[cap]
            da = ""
            if dash: da = ' stroke-dasharray="' + " ".join(str(float(v) * w) for v in dash.split()) + '"'
            parts.append(f'<path d="{g}" fill="{f}" fill-opacity="{fo}" stroke="{s}" stroke-opacity="{so}" stroke-width="{w}" stroke-linecap="{capsvg}" stroke-linejoin="round"{da}/>')
        parts.append(f'<text x="80" y="134" fill="#8A96B3" font-size="11" text-anchor="middle" font-family="sans-serif">{k}</text></g>')
        tiles.append("".join(parts))
    rows = (len(allkeys) + 5) // 6
    with open(prev, "w") as f:
        f.write(f'<svg xmlns="http://www.w3.org/2000/svg" width="{6*170}" height="{rows*150}" style="background:#0E1320">' + "".join(tiles) + "</svg>")
print("ok")
