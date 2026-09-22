# -*- coding: utf-8 -*-
"""Sinh file DOCX preview trang bia CoverPage (de user phe duyet truoc khi code).
Chay: python tools/gen_cover_preview.py
"""
import copy
from docx import Document
from docx.shared import Pt, Mm, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.oxml.ns import qn
from docx.oxml import OxmlElement

TAB_COLOR = "1B4F72"   # xanh dam — in mono ra khoi den dac
HEAD_BG = "E8E8E8"
TOTAL_BG = "F2F2F2"
FONT = "Segoe UI"
LOGO = r"C:\Users\scraw\Projects\Printonator\printonatorLogo.png"


def shade(cell, hexcolor):
    el = OxmlElement("w:shd")
    el.set(qn("w:val"), "clear")
    el.set(qn("w:color"), "auto")
    el.set(qn("w:fill"), hexcolor)
    cell._tc.get_or_add_tcPr().append(el)


def no_borders(table):
    tblPr = table._tbl.tblPr
    borders = OxmlElement("w:tblBorders")
    for edge in ("top", "left", "bottom", "right", "insideH", "insideV"):
        e = OxmlElement(f"w:{edge}")
        e.set(qn("w:val"), "none")
        e.set(qn("w:sz"), "0")
        borders.append(e)
    tblPr.append(borders)


def box(table):
    """Vien don 0.5pt quanh bang (khung khoi thong tin)."""
    tblPr = table._tbl.tblPr
    borders = OxmlElement("w:tblBorders")
    for edge in ("top", "left", "bottom", "right", "insideH", "insideV"):
        e = OxmlElement(f"w:{edge}")
        e.set(qn("w:val"), "single")
        e.set(qn("w:sz"), "6")
        e.set(qn("w:color"), "000000")
        borders.append(e)
    tblPr.append(borders)


def cell_margins(table, top=40, bottom=40, left=80, right=80):
    tblPr = table._tbl.tblPr
    mar = OxmlElement("w:tblCellMar")
    for name, val in (("top", top), ("left", left), ("bottom", bottom), ("right", right)):
        e = OxmlElement(f"w:{name}")
        e.set(qn("w:w"), str(val))
        e.set(qn("w:type"), "dxa")
        mar.append(e)
    tblPr.append(mar)


def keep_together(table):
    """Khong ngat bang qua trang."""
    for row in table.rows:
        trPr = row._tr.get_or_add_trPr()
        el = OxmlElement("w:cantSplit")
        trPr.append(el)


def repeat_header(row):
    trPr = row._tr.get_or_add_trPr()
    el = OxmlElement("w:tblHeader")
    trPr.append(el)


def txt(par, text, size=10.5, bold=False, color=None, font=FONT, italic=False):
    run = par.add_run(text)
    run.font.size = Pt(size)
    run.bold = bold
    run.italic = italic
    run.font.name = font
    if color:
        run.font.color.rgb = RGBColor.from_string(color)
    return run


def para(container, text="", size=10.5, bold=False, color=None, align=None,
         space_before=0, space_after=0, italic=False):
    p = container.add_paragraph()
    p.paragraph_format.space_before = Pt(space_before)
    p.paragraph_format.space_after = Pt(space_after)
    p.paragraph_format.line_spacing = 1.0
    if align is not None:
        p.alignment = align
    if text:
        txt(p, text, size=size, bold=bold, color=color, italic=italic)
    return p


def block_title(cell, text):
    p = cell.paragraphs[0]
    p.paragraph_format.space_after = Pt(2)
    txt(p, text, size=9.5, bold=True, color="1B4F72")


def build():
    doc = Document()

    # ---- Trang A4 doc, le 10mm (CDP margin 0.4in ~ 10mm) ----
    sec = doc.sections[0]
    sec.page_width = Mm(210)
    sec.page_height = Mm(297)
    for attr in ("top_margin", "bottom_margin", "left_margin", "right_margin"):
        setattr(sec, attr, Mm(10))

    style = doc.styles["Normal"]
    style.font.name = FONT
    style.font.size = Pt(10.5)
    style.element.rPr.rFonts.set(qn("w:eastAsia"), FONT)

    # ================= TAB NHAN DIEN GOC TREN-PHAI =================
    tab = doc.add_table(rows=1, cols=3)
    tab.autofit = False
    no_borders(tab)
    cell_margins(tab, top=0, bottom=0, left=0, right=0)
    for i, w in enumerate((Mm(11), Mm(109), Mm(70))):
        tab.columns[i].width = w
        tab.rows[0].cells[i].width = w

    # Logo app (cung asset voi icon cua app)
    logo_cell = tab.rows[0].cells[0]
    lp = logo_cell.paragraphs[0]
    lp.paragraph_format.space_after = Pt(0)
    lp.add_run().add_picture(LOGO, width=Mm(9.5))

    left = tab.rows[0].cells[1]
    lp2 = left.paragraphs[0]
    lp2.paragraph_format.space_after = Pt(0)
    txt(lp2, "Printonator", size=13, bold=True, color="1B4F72")
    para(left, "Phần mềm in hàng loạt", size=9, color="444444")

    right = tab.rows[0].cells[2]
    shade(right, TAB_COLOR)
    p0 = right.paragraphs[0]
    p0.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p0.paragraph_format.space_before = Pt(3)
    p0.paragraph_format.space_after = Pt(0)
    txt(p0, "TRANG BÌA", size=13, bold=True, color="FFFFFF")
    p1 = para(right, "12/09/2026  14:30", size=9, color="FFFFFF",
              align=WD_ALIGN_PARAGRAPH.CENTER, space_after=3)
    keep_together(tab)

    para(doc, "", size=6, space_after=0)

    # ================= TIEU DE =================
    para(doc, "DANH SÁCH FILE IN", size=17, bold=True, space_after=1)
    para(doc, "Lô in — Hồ sơ nghiệm thu", size=10, color="444444", space_after=6)

    # ================= KHOI 1: PHAN MEM =================
    t1 = doc.add_table(rows=1, cols=1)
    box(t1)
    cell_margins(t1)
    c = t1.rows[0].cells[0]
    block_title(c, "PHẦN MỀM")
    para(c, "Printonator 0.2.7 — phần mềm in hàng loạt cho Windows", size=10, space_after=1)
    para(c, "github.com/nanowind/Printonator  ·  Giấy in được tạo tự động, không cần kiểm tra tay",
         size=9, color="444444")
    keep_together(t1)

    para(doc, "", size=6, space_after=0)

    # ================= KHOI 2: MAY YEU CAU IN =================
    t2 = doc.add_table(rows=1, cols=1)
    box(t2)
    cell_margins(t2)
    c = t2.rows[0].cells[0]
    block_title(c, "MÁY YÊU CẦU IN")
    for label, value in (
        ("Máy tính", "NANOWINDPC"),
        ("Máy in đích", "Canon LBP151 (222)"),
        ("Thời điểm in", "12/09/2026 14:30"),
    ):
        p = c.add_paragraph()
        p.paragraph_format.space_after = Pt(0)
        p.paragraph_format.line_spacing = 1.0
        txt(p, f"{label}: ", size=10, bold=True)
        txt(p, value, size=10)
    keep_together(t2)

    para(doc, "", size=6, space_after=0)

    # ================= KHOI 3: TONG HOP =================
    t3 = doc.add_table(rows=1, cols=1)
    box(t3)
    cell_margins(t3)
    c = t3.rows[0].cells[0]
    block_title(c, "TỔNG HỢP")
    p = c.add_paragraph()
    p.paragraph_format.space_after = Pt(0)
    txt(p, "Số file: ", size=10, bold=True)
    txt(p, "4", size=10)
    txt(p, "   ·   Tổng trang: ", size=10, bold=True)
    txt(p, "24", size=10)
    txt(p, "   ·   Tổng tờ (ước tính, đã nhân số bản): ", size=10, bold=True)
    txt(p, "24", size=10)
    txt(p, "   ·   Khổ giấy: ", size=10, bold=True)
    txt(p, "A4", size=10)
    keep_together(t3)

    para(doc, "", size=6, space_after=0)

    # ================= BANG DANH SACH FILE =================
    rows = [
        ("1", "01_Bien_ban_nghiem_thu.pdf", "3", "1x · A4 · 2 mặt theo máy · màu theo máy · 1-tr/tờ", ""),
        ("2", "02_Bang_ke_khoi_luong.xlsx", "5", "1x · A4 · 1 mặt · B&W · 1-tr/tờ", ""),
        ("3", "03_Ban_ve_hoan_cong.pdf", "12", "2x · A3 · 1 mặt · B&W · 1-tr/tờ", ""),
        ("4", "04_Bien_ban_ban_giao.docx", "4", "1x · A4 · 2 mặt · màu theo máy · 1-tr/tờ", "LBP242/243"),
    ]
    tb = doc.add_table(rows=1 + len(rows) + 1, cols=5)
    box(tb)
    tb.autofit = False
    widths = (Mm(10), Mm(72), Mm(14), Mm(70), Mm(24))
    for i, w in enumerate(widths):
        tb.columns[i].width = w

    hdr = tb.rows[0]
    repeat_header(hdr)
    for i, name in enumerate(("STT", "Tên file", "Trang", "Cấu hình in", "Máy in riêng")):
        cell = hdr.cells[i]
        shade(cell, HEAD_BG)
        p = cell.paragraphs[0]
        p.paragraph_format.space_after = Pt(0)
        p.paragraph_format.line_spacing = 1.0
        txt(p, name, size=9.5, bold=True)

    for r, data in enumerate(rows, start=1):
        for i, val in enumerate(data):
            cell = tb.rows[r].cells[i]
            p = cell.paragraphs[0]
            p.paragraph_format.space_after = Pt(0)
            p.paragraph_format.line_spacing = 1.0
            if i == 0:
                p.alignment = WD_ALIGN_PARAGRAPH.CENTER
            txt(p, val, size=10)

    tot = tb.rows[-1]
    a = tot.cells[0].merge(tot.cells[1])
    b = tot.cells[3].merge(tot.cells[4])
    for cell, val, center in ((a, "TỔNG CỘNG — 4 file", False),
                              (tot.cells[2], "24", True),
                              (b, "24 tờ (ước tính)", False)):
        shade(cell, TOTAL_BG)
        p = cell.paragraphs[0]
        p.paragraph_format.space_after = Pt(0)
        p.paragraph_format.line_spacing = 1.0
        if center:
            p.alignment = WD_ALIGN_PARAGRAPH.CENTER
        txt(p, val, size=10, bold=True)
    keep_together(tb)

    para(doc, "Số trang ghi trên đây là số trang của từng file; số tờ thực tế phụ thuộc cấu hình in và máy in.",
         size=8.5, color="444444", space_before=4)

    doc.save(OUT)


OUT = r"C:\Users\scraw\Projects\Printonator\docs\preview_trang_bia.docx"
build()
print("OK ->", OUT)
