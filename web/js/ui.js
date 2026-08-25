// Small DOM + UX helpers. No framework, on purpose.
export function el(tag, attrs = {}, ...children) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") e.className = v;
    else if (k === "html") e.innerHTML = v;
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else if (v !== null && v !== undefined) e.setAttribute(k, v);
  }
  for (const c of children.flat()) {
    if (c == null) continue;
    e.append(c.nodeType ? c : document.createTextNode(c));
  }
  return e;
}

export function debounce(fn, ms = 150) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

export function toast(msg, kind = "") {
  const t = el("div", { class: `toast ${kind}` }, msg);
  document.getElementById("toasts").append(t);
  setTimeout(() => { t.style.opacity = "0"; t.style.transition = "opacity .3s"; setTimeout(() => t.remove(), 350); }, 2600);
}

export function spinner(label = "Loading…") {
  return el("div", { class: "loading" }, el("div", { class: "spinner" }), label);
}

export function crumbs(...parts) {
  const c = document.getElementById("crumbs");
  c.innerHTML = "";
  parts.forEach((p, i) => {
    if (i) c.append(el("span", { class: "sep" }, "›"));
    c.append(i === parts.length - 1 ? el("b", {}, p) : el("span", {}, p));
  });
}

export const fmtBytes = (n) =>
  n >= 1 << 20 ? (n / (1 << 20)).toFixed(1) + " MB"
  : n >= 1024 ? (n / 1024).toFixed(1) + " KB" : n + " B";
