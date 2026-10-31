#!/usr/bin/env python3
"""Generate plain SVG diagrams and matching editable diagrams.net sources."""

from pathlib import Path
from html import escape
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


class Diagram:
    def __init__(self, name, width, height):
        self.name = name
        self.width = width
        self.height = height
        self.svg = [
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
            '<rect width="100%" height="100%" fill="white"/>',
            '<defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" fill="#222"/></marker></defs>',
            '<g font-family="Arial, Helvetica, sans-serif" fill="#111" stroke-width="1.5">',
        ]
        self.document = ET.Element("mxfile", host="app.diagrams.net")
        diagram = ET.SubElement(self.document, "diagram", name=name)
        model = ET.SubElement(diagram, "mxGraphModel", pageWidth=str(width), pageHeight=str(height))
        self.cells = ET.SubElement(model, "root")
        ET.SubElement(self.cells, "mxCell", id="0")
        ET.SubElement(self.cells, "mxCell", id="1", parent="0")
        self.sequence = 1

    def node(self, x, y, width, height, lines, size=17, box=True):
        self.sequence += 1
        if box:
            self.svg.append(
                f'<rect x="{x}" y="{y}" width="{width}" height="{height}" fill="white" stroke="#222"/>'
            )
        for index, line in enumerate(lines):
            baseline = y + height / 2 - (len(lines) - 1) * 12 + index * 24 + size * 0.35
            self.svg.append(
                f'<text x="{x + width / 2}" y="{baseline}" font-size="{size}" text-anchor="middle">{escape(line)}</text>'
            )
        style = (
            f"whiteSpace=wrap;html=0;fontFamily=Arial;fontSize={size};fontColor=#111111;fillColor=#ffffff;strokeColor="
            + ("#222222;" if box else "none;")
        )
        cell = ET.SubElement(
            self.cells,
            "mxCell",
            id=str(self.sequence),
            value="\n".join(lines),
            style=style,
            vertex="1",
            parent="1",
        )
        ET.SubElement(
            cell,
            "mxGeometry",
            x=str(x),
            y=str(y),
            width=str(width),
            height=str(height),
            attrib={"as": "geometry"},
        )

    def edge(self, points):
        coordinates = " ".join(f"{x},{y}" for x, y in points)
        self.svg.append(
            f'<polyline points="{coordinates}" fill="none" stroke="#222" marker-end="url(#arrow)"/>'
        )
        self.sequence += 1
        cell = ET.SubElement(
            self.cells,
            "mxCell",
            id=str(self.sequence),
            style="endArrow=block;endFill=1;strokeColor=#222222;strokeWidth=1.5;",
            edge="1",
            parent="1",
        )
        geometry = ET.SubElement(cell, "mxGeometry", relative="1", attrib={"as": "geometry"})
        for point, kind in [(points[0], "sourcePoint"), (points[-1], "targetPoint")]:
            ET.SubElement(
                geometry, "mxPoint", x=str(point[0]), y=str(point[1]), attrib={"as": kind}
            )
        if len(points) > 2:
            array = ET.SubElement(geometry, "Array", attrib={"as": "points"})
            for x, y in points[1:-1]:
                ET.SubElement(array, "mxPoint", x=str(x), y=str(y))

    def save(self):
        target = ROOT / "docs/images"
        target.mkdir(parents=True, exist_ok=True)
        (target / f"{self.name}.svg").write_text("".join(self.svg) + "</g></svg>\n")
        ET.indent(self.document)
        ET.ElementTree(self.document).write(
            target / f"{self.name}.drawio", encoding="utf-8", xml_declaration=True
        )


def architecture():
    diagram = Diagram("architecture", 1200, 500)
    diagram.node(0, 12, 1200, 40, ["Google Drive Clone — architecture"], 24, False)
    diagram.node(30, 170, 180, 80, ["Browser", "React + TypeScript"])
    diagram.node(290, 170, 180, 80, ["Web service", "Nginx / same origin"])
    diagram.node(550, 170, 250, 80, ["ASP.NET Core API", "Auth / files / uploads"])
    diagram.node(950, 70, 220, 80, ["PostgreSQL", "Metadata + permissions"])
    diagram.node(950, 300, 220, 80, ["MinIO", "Private S3 objects"])
    diagram.node(550, 320, 250, 65, ["Cleanup worker", "Expiry + deletion queue"])
    diagram.edge([(210, 210), (290, 210)])
    diagram.edge([(470, 210), (550, 210)])
    diagram.edge([(800, 190), (875, 190), (875, 110), (950, 110)])
    diagram.edge([(800, 230), (900, 230), (900, 330), (950, 330)])
    diagram.edge([(675, 320), (675, 250)])
    diagram.edge([(800, 355), (950, 355)])
    diagram.edge([(1060, 380), (1060, 440), (120, 440), (120, 250)])
    diagram.node(170, 395, 370, 30, ["Short-lived signed downloads"], 16, False)
    diagram.node(
        0,
        460,
        1200,
        30,
        ["Docker Compose / persistent database, object and cookie-key volumes"],
        17,
        False,
    )
    diagram.save()


def upload_flow():
    diagram = Diagram("upload-flow", 1200, 440)
    diagram.node(0, 12, 1200, 40, ["Resumable upload — from file to published version"], 24, False)
    nodes = [
        (30, ["Browser hashes file", "SHA-256 in a worker"]),
        (330, ["Reserve quota", "Persist upload session"]),
        (630, ["Upload 8 MiB parts", "Hash + S3 + saved ETag"]),
        (930, ["Complete + verify", "Check whole-file SHA-256"]),
    ]
    for x, lines in nodes:
        diagram.node(x, 110, 240, 85, lines)
    for start, end in [(270, 330), (570, 630), (870, 930)]:
        diagram.edge([(start, 152), (end, 152)])
    diagram.node(330, 290, 240, 80, ["Resume original file", "Skip confirmed parts"])
    diagram.node(930, 290, 240, 80, ["Publish metadata", "One file version + object"])
    diagram.edge([(450, 195), (450, 290)])
    diagram.edge([(570, 330), (750, 330), (750, 195)])
    diagram.edge([(1050, 195), (1050, 290)])
    diagram.node(
        0,
        390,
        1200,
        30,
        [
            "Repeated completion returns the same file; a checksum mismatch never publishes a version."
        ],
        17,
        False,
    )
    diagram.save()


if __name__ == "__main__":
    architecture()
    upload_flow()
