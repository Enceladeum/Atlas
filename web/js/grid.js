// Virtualized data grid: fixed 28px rows, windowed <tbody>, sticky header +
// sticky first column. Renders only the visible slice, so 40k-row sheets stay smooth.
import { el } from "./ui.js";

const ROW_H = 28;
const OVERSCAN = 12;

export class VGrid {
  constructor(container) {
    this.container = container;
    this.header = [];
    this.rows = [];
    this.colW = [];
    this.wrap = el("div", { class: "grid-wrap" });
    this.inner = el("div", { class: "grid-inner" });
    this.table = el("table", { class: "grid" });
    this.thead = el("thead");
    this.tbody = el("tbody");
    this.table.append(this.thead, this.tbody);
    this.inner.append(this.table);
    this.wrap.append(this.inner);
    container.append(this.wrap);
    this.wrap.addEventListener("scroll", () => this.renderWindow());
    this._first = -1; this._last = -1;
    this.linkFor = null;   // optional (colIndex, value) => ({ href, title }) | null
    this.hlRow = -1;       // highlighted row index (scrollToRow)
  }

  scrollToRow(r) {
    this.hlRow = r;
    const vh = this.wrap.clientHeight || 400;
    this.wrap.scrollTop = Math.max(0, r * ROW_H - vh / 2 + ROW_H);
    this.renderWindow(true);
  }

  setData(header, rows) {
    this.header = header;
    this.rows = rows;
    // column widths: header + sample of first 120 rows, clamped
    this.colW = header.map((h, c) => {
      let w = h.length;
      const lim = Math.min(rows.length, 120);
      for (let r = 0; r < lim; r++) {
        const v = rows[r][c];
        if (v && v.length > w) w = v.length;
      }
      return Math.min(Math.max(w * 7.4 + 26, 60), 360);
    });
    this.thead.innerHTML = "";
    const tr = el("tr");
    header.forEach((h, c) => tr.append(el("th", { style: `width:${this.colW[c]}px; min-width:${this.colW[c]}px`, title: h }, h)));
    this.thead.append(tr);
    this.inner.style.height = (rows.length * ROW_H + ROW_H + 2) + "px";
    this.table.style.width = this.colW.reduce((a, b) => a + b, 0) + "px";
    this._first = -1; this._last = -1;
    this.hlRow = -1;
    this.wrap.scrollTop = 0;
    this.renderWindow(true);
  }

  renderWindow(force = false) {
    const top = this.wrap.scrollTop;
    const vh = this.wrap.clientHeight;
    let first = Math.max(0, Math.floor(top / ROW_H) - OVERSCAN);
    let last = Math.min(this.rows.length, Math.ceil((top + vh) / ROW_H) + OVERSCAN);
    if (!force && first === this._first && last === this._last) return;
    this._first = first; this._last = last;
    this.tbody.innerHTML = "";
    // translate the tbody to the window position via a spacer row
    const spacer = el("tr", { style: `height:${first * ROW_H}px` });
    this.tbody.append(spacer);
    for (let r = first; r < last; r++) {
      const tr = el("tr", r === this.hlRow ? { class: "hl" } : {});
      const row = this.rows[r];
      for (let c = 0; c < this.header.length; c++) {
        const v = row[c] ?? "";
        const cls = v === "" ? "empty" : /^-?[\d.]+$/.test(v) ? "num" : "";
        const td = el("td", { class: cls, style: `width:${this.colW[c]}px; min-width:${this.colW[c]}px`, title: v.length > 40 ? v : null });
        const lk = v !== "" && this.linkFor ? this.linkFor(c, v) : null;
        if (lk) td.append(el("a", { class: "cell-link", href: lk.href, title: lk.title }, v));
        else td.textContent = v === "" ? "\u00b7" : v;
        tr.append(td);
      }
      this.tbody.append(tr);
    }
  }
}
