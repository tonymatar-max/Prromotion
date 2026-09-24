// Nexus Promotions admin app. Plain JS, no build step; served by the promotion API.
// Reads and writes the @APE_PROMO UDO through /api/v1/admin/*.

const API = "/api/v1/admin";
const main = document.getElementById("main");

const TYPES = [
  { id: "ItemDiscount", code: "P01", name: "Item discount", eg: "15% off all Nivea" },
  { id: "FixedPrice", code: "P02", name: "Fixed price", eg: "Cola 1.5 L at 0.500" },
  { id: "QuantityTier", code: "P03", name: "Quantity tiers", eg: "6+ pcs 8%, 12+ pcs 12%" },
  { id: "BuyXGetXFree", code: "P04", name: "Buy X get X free", eg: "Buy 2 get 1 free" },
  { id: "BuyXGetY", code: "P05", name: "Buy X get Y", eg: "Buy a phone, case at 50%" },
  { id: "MixAndMatch", code: "P06", name: "Mix and match", eg: "Any 3 snacks for 1.000" },
  { id: "BasketThreshold", code: "P08/09", name: "Spend threshold", eg: "Spend 50, get 5%; 100, get 10%" },
];
const STATUS = { D: "Draft", P: "Pending approval", A: "Active", S: "Paused", E: "Expired", C: "Cancelled" };
const REWARD = { P: "% off", A: "Amount off", F: "Fixed price", X: "Free" };
const SCOPE_TYPES = { I: "Item", G: "Item group", M: "Manufacturer", P: "Item property (1–64)" };
const AUDIENCE = { GRP: "Customer group", CARD: "Customer", PL: "Price list", CH: "Channel", BR: "Branch" };
const DAYS = [["1", "Mon"], ["2", "Tue"], ["3", "Wed"], ["4", "Thu"], ["5", "Fri"], ["6", "Sat"], ["7", "Sun"]];
const SCREENS = [["OQUT", "Quotation"], ["ORDR", "Sales Order"], ["ODLN", "Delivery"], ["OINV", "Invoice"]];

// Several companies on one server: promotions are defined once, in the master company, and ticked per company.
const multi = () => !!info.multiCompany && (info.companiesList?.length ?? 0) > 1;
const masterCompany = () => info.companiesList?.find(c => c.master);
const companyName = db => info.companiesList?.find(c => c.db.toLowerCase() === String(db).toLowerCase())?.name ?? db;
const hasCompany = (p, db) => (p.companies ?? "").split(",").some(x => x.trim().toLowerCase() === db.toLowerCase());

function companiesHint(p) {
  const chosen = info.companiesList.filter(c => hasCompany(p, c.db));
  const master = masterCompany();
  if (chosen.length === 0) return `None selected: applies only in ${esc(master.name)}, the master company.`;
  const names = chosen.map(c => esc(c.name)).join(", ");
  return hasCompany(p, master.db)
    ? `Applies in: ${names}.`
    : `Applies in: ${names}. It does not apply in ${esc(master.name)}, the master company, because that is not selected.`;
}

function companiesCell(p) {
  const names = (p.companies ?? "").split(",").map(x => x.trim()).filter(Boolean);
  return names.length === 0 ? `<span class="muted">${esc(masterCompany()?.name ?? "")} only</span>` : esc(names.map(companyName).join(", "));
}

// Where the item groups, manufacturers, items and customer groups a promotion names mean something different in another company.
async function runCompanyCheck(s) {
  const others = info.companiesList.filter(c => !c.master && hasCompany(s.p, c.db));
  if (others.length === 0) { s.notes = undefined; return false; }
  try {
    s.notes = (await call("/promotions/check", { method: "POST", body: JSON.stringify(payload(s.p)) })).notes;
  } catch { /* the check is a convenience */ }
  return true;
}

function autoApplyBanner() {
  if (multi()) {
    if (!info.companiesList.some(c => c.modeASettable)) return "";
    return `<div class="panel"><div class="body">
      <div class="hint" style="margin:0 0 8px">Whether each company's add-ons apply promotions automatically on Add/Update, on every workstation of that company. The Apply Promotions button always works either way.</div>
      ${info.companiesList.map(c => c.modeASettable ? `
      <div style="display:flex;align-items:center;gap:12px;padding:4px 0">
        <strong style="min-width:180px">${esc(c.name)}${c.master ? ` <span class="muted">(master)</span>` : ""}</strong>
        <span class="status ${c.modeAEnabled ? "A" : "C"}">${c.modeAEnabled ? "Auto-apply ON" : "Auto-apply OFF"}</span>
        <span class="spacer"></span>
        <button data-mode-a="${esc(c.db)}" class="${c.modeAEnabled ? "danger" : "primary"}">${c.modeAEnabled ? "Turn off" : "Turn on"}</button>
      </div>` : `<div class="muted" style="padding:4px 0">${esc(c.name)}: no SQL connection is configured for it, so its switch cannot be set here.</div>`).join("")}
    </div></div>`;
  }
  if (!info.modeASettable) return "";
  return `<div class="panel"><div class="body" style="display:flex;align-items:center;gap:12px">
      <span class="status ${info.modeAEnabled ? "A" : "C"}">${info.modeAEnabled ? "Auto-apply ON" : "Auto-apply OFF"}</span>
      <span class="hint" style="margin:0">Whether the add-on applies promotions automatically on Add/Update, on every workstation. The Apply Promotions button always works either way.</span>
      <span class="spacer"></span>
      <button data-mode-a="" class="${info.modeAEnabled ? "danger" : "primary"}">${info.modeAEnabled ? "Turn off for everyone" : "Turn on for everyone"}</button>
    </div></div>`;
}

let info = { editable: false };
let lookups = {};          // kind -> [{code, name}]
let listFilter = "all";
let listSearch = "";

// ── helpers ──────────────────────────────────────────────────────────────
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const num = v => { const n = parseFloat(String(v).replace(",", ".")); return isNaN(n) ? 0 : n; };
const money = v => Number(v ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 3 });

async function call(path, options = {}) {
  const res = await fetch(API + path, { headers: { "Content-Type": "application/json" }, ...options });
  const text = await res.text();
  const body = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const errors = body?.errors ?? [body?.detail ?? body?.title ?? `HTTP ${res.status}`];
    throw Object.assign(new Error(errors.join(" ")), { errors });
  }
  return body;
}

function toast(message, error = false) {
  const t = document.getElementById("toast");
  t.textContent = message;
  t.className = "toast" + (error ? " error" : "");
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.hidden = true), error ? 6000 : 3000);
}

async function lookup(kind) {
  if (!lookups[kind]) {
    try { lookups[kind] = await call(`/lookups/${kind}`); } catch { lookups[kind] = []; }
  }
  return lookups[kind];
}

function datalist(id, rows) {
  return `<datalist id="${id}">${rows.map(r => `<option value="${esc(r.code)}">${esc(r.name)}</option>`).join("")}</datalist>`;
}

function setNav(view) {
  document.querySelectorAll(".nav-item").forEach(b => b.classList.toggle("active", b.dataset.view === view));
}

// ── routing ──────────────────────────────────────────────────────────────
document.querySelectorAll(".nav-item").forEach(b => b.addEventListener("click", () => {
  location.hash = b.dataset.view === "list" ? "" : b.dataset.view;
}));
window.addEventListener("hashchange", route);

async function route() {
  const h = decodeURIComponent(location.hash.slice(1));
  if (h === "new") return editor(null);
  if (h === "simulator") return simulator();
  if (h.startsWith("edit/")) return editor(h.slice(5));
  return list();
}

// ── list ─────────────────────────────────────────────────────────────────
async function list() {
  setNav("list");
  main.innerHTML = `<div class="page-head"><h1>Promotions</h1></div><div class="panel"><div class="empty">Loading…</div></div>`;
  let rows;
  try { rows = await call("/promotions"); }
  catch (e) { main.innerHTML = `<div class="alert error">Could not load promotions: ${esc(e.message)}</div>`; return; }
  if (info.multiCompany) { try { info.companiesList = await call("/companies"); } catch { /* keep the last list */ } }

  const counts = rows.reduce((c, p) => (c[p.status] = (c[p.status] ?? 0) + 1, c), {});
  const filters = [["all", `All ${rows.length}`], ...Object.entries(STATUS).filter(([k]) => counts[k]).map(([k, v]) => [k, `${v} ${counts[k]}`])];
  const q = listSearch.toLowerCase();
  const shown = rows.filter(p => (listFilter === "all" || p.status === listFilter)
    && (!q || p.code.toLowerCase().includes(q) || p.name.toLowerCase().includes(q)));

  main.innerHTML = `
    <div class="page-head">
      <h1>Promotions</h1><span class="spacer"></span>
      <input id="search" placeholder="Search code or name" style="width:220px" value="${esc(listSearch)}">
      ${info.editable ? `<button class="primary" id="new">New promotion</button>` : ""}
    </div>
    ${info.editable ? "" : `<div class="alert note">Read-only: the API reads promotions from files. Start it with Promotions:Source=ServiceLayer to edit promotions in B1.</div>`}
    ${autoApplyBanner()}
    <div class="chips" style="margin-bottom:12px">${filters.map(([k, label]) =>
      `<button class="chip ${listFilter === k ? "on" : ""}" data-f="${k}">${esc(label)}</button>`).join("")}</div>
    <div class="panel">
      ${shown.length === 0 ? `<div class="empty"><div class="brand-mark large"><span class="brand-node"></span></div>
          ${rows.length === 0 ? "No promotions yet. Create the first one." : "Nothing matches this filter."}</div>` : `
      <table>
        <thead><tr><th>Code</th><th>Name</th><th>Type</th><th>Status</th><th>Valid</th><th>Screens</th>${multi() ? "<th>Companies</th>" : ""}<th class="num">Priority</th><th>Stacking</th></tr></thead>
        <tbody>${shown.map(p => `
          <tr class="click" data-code="${esc(p.code)}">
            <td><strong>${esc(p.code)}</strong></td>
            <td>${esc(p.name)}${p.nameAr ? `<div class="muted" dir="rtl">${esc(p.nameAr)}</div>` : ""}</td>
            <td>${esc(TYPES.find(t => t.id === p.type)?.name ?? p.type)}</td>
            <td><span class="status ${esc(p.status)}">${esc(STATUS[p.status] ?? p.status)}</span></td>
            <td>${validity(p)}</td>
            <td>${screens(p)}</td>
            ${multi() ? `<td>${companiesCell(p)}</td>` : ""}
            <td class="num">${p.priority}</td>
            <td>${p.stacking === "E" ? "Exclusive" : "Stackable"}</td>
          </tr>`).join("")}</tbody>
      </table>`}
    </div>`;

  main.querySelector("#search").addEventListener("input", e => { listSearch = e.target.value; list(); main.querySelector("#search")?.focus(); });
  main.querySelector("#new")?.addEventListener("click", () => location.hash = "new");
  main.querySelectorAll("[data-f]").forEach(b => b.addEventListener("click", () => { listFilter = b.dataset.f; list(); }));
  main.querySelectorAll("[data-mode-a]").forEach(b => b.addEventListener("click", async () => {
    const db = b.dataset.modeA;
    const row = db ? info.companiesList.find(c => c.db === db) : null;
    const turningOff = row ? row.modeAEnabled : info.modeAEnabled;
    const who = row ? row.name : "every workstation";
    if (turningOff && !confirm(`Turn off automatic apply-on-save for ${who}? The Apply Promotions button will still work; nothing will fire automatically until this is turned back on.`)) return;
    try {
      const r = await call("/mode-a", { method: "POST", body: JSON.stringify({ enabled: !turningOff, company: db || null }) });
      if (row) row.modeAEnabled = r.modeAEnabled; else info.modeAEnabled = r.modeAEnabled;
      toast(`Automatic apply is ${r.modeAEnabled ? "on" : "off"} for ${who}.`);
      list();
    } catch (e) { toast(e.message, true); }
  }));
  main.querySelectorAll("tr[data-code]").forEach(r => r.addEventListener("click", () => location.hash = "edit/" + encodeURIComponent(r.dataset.code)));
}

function screens(p) {
  const codes = (p.documents ?? "").split(",").map(x => x.trim()).filter(Boolean);
  return codes.length === 0 ? `<span class="muted">All</span>` : esc(codes.map(c => SCREENS.find(([x]) => x === c)?.[1] ?? c).join(", "));
}

function validity(p) {
  if (!p.validFrom && !p.validTo) return `<span class="muted">Always</span>`;
  return `${esc(p.validFrom ?? "…")} → ${esc(p.validTo ?? "…")}`;
}

// ── editor ───────────────────────────────────────────────────────────────
const blank = () => ({
  code: "", name: "", nameAr: "", version: 1, type: "ItemDiscount", status: "D", priority: 100, stacking: "S",
  validFrom: "", validFromTime: "", validTo: "", validToTime: "", weekdays: "", timeFrom: "", timeTo: "",
  coupon: "", buyQty: 0, getQty: 0, rewardItem: "", freeMode: "B", rewardUnits: "C", rewardKind: "P", rewardValue: 0,
  maxApps: null, maxDisc: 0, budget: 0, budgetUsed: 0, allowBelow: false, campaign: "", documents: "", companies: "",
  scopes: [], tiers: [], audience: [],
});

async function editor(code) {
  setNav(code ? "list" : "new");
  let p = blank(), isNew = !code;
  // Lookups come from B1 and can take a few seconds the first time: say so instead of leaving the old page up.
  main.innerHTML = `<div class="panel"><div class="empty">Loading${code ? " " + esc(code) : ""}…</div></div>`;
  if (code) {
    try { p = { ...blank(), ...await call(`/promotions/${encodeURIComponent(code)}`) }; }
    catch (e) { main.innerHTML = `<div class="alert error">Could not load ${esc(code)}: ${esc(e.message)}</div>`; return; }
  }
  const [items, groups, makers, custGroups, priceLists] = await Promise.all(
    ["items", "itemgroups", "manufacturers", "customergroups", "pricelists"].map(lookup));
  const state = { p, isNew, errors: [], result: null, basket: [{ itemCode: "", quantity: 1, unitPrice: "" }], cardCode: "", company: masterCompany()?.db };
  renderEditor(state, { items, groups, makers, custGroups, priceLists });
}

function renderEditor(s, lk) {
  const p = s.p;
  let stepNo = 0;
  const stepNo_ = () => ++stepNo;   // step numbers follow the sections shown: the Companies step exists only with several companies
  const t = p.type;
  const readOnly = !info.editable;
  const dis = readOnly ? "disabled" : "";
  const needsTiers = t === "QuantityTier" || t === "BasketThreshold";
  const needsBuyGet = t === "BuyXGetXFree" || t === "BuyXGetY";
  const rewardRole = t === "BuyXGetY" && p.freeMode === "B";

  const rewardKinds = {
    ItemDiscount: ["P", "A", "F"], FixedPrice: [], QuantityTier: ["P", "A", "F"], BuyXGetXFree: [],
    BuyXGetY: ["X", "P", "A", "F"], MixAndMatch: ["F", "P", "A"], BasketThreshold: ["P", "A"],
  }[t];
  if (rewardKinds.length && !rewardKinds.includes(p.rewardKind)) p.rewardKind = rewardKinds[0];

  const valueLabel = t === "FixedPrice" ? "Promotional unit price"
    : t === "MixAndMatch" && p.rewardKind === "F" ? "Set price"
    : p.rewardKind === "P" ? "Discount %" : p.rewardKind === "A" ? "Amount off" : p.rewardKind === "F" ? "Fixed price" : "Value";

  const scopeRows = role => p.scopes.map((x, i) => [x, i]).filter(([x]) => x.role === role).map(([x, i]) => `
    <tr>
      <td style="width:190px"><select data-scope="${i}" data-k="scopeType" ${dis}>
        ${Object.entries(SCOPE_TYPES).map(([k, v]) => `<option value="${k}" ${x.scopeType === k ? "selected" : ""}>${v}</option>`).join("")}</select></td>
      <td><input data-scope="${i}" data-k="value" value="${esc(x.value)}" ${dis}
        list="${{ I: "dl-items", G: "dl-groups", M: "dl-makers" }[x.scopeType] ?? ""}" placeholder="${{ I: "Item code", G: "Group number", M: "Manufacturer code", P: "Property number 1–64" }[x.scopeType]}"></td>
      <td style="width:110px">${x.scopeType === "I" ? `<label class="check"><input type="checkbox" data-scope="${i}" data-k="exclude" ${x.exclude ? "checked" : ""} ${dis}> Exclude</label>` : ""}</td>
      <td style="width:40px">${readOnly ? "" : `<button class="icon" data-del-scope="${i}" title="Remove">✕</button>`}</td>
    </tr>`).join("");

  main.innerHTML = `
    <div class="page-head">
      <button class="link" id="back">← Promotions</button>
      <h1>${s.isNew ? "New promotion" : esc(p.code)}</h1>
      ${s.isNew ? "" : `<span class="status ${esc(p.status)}">${esc(STATUS[p.status] ?? p.status)}</span>`}
      <span class="spacer"></span>
      ${readOnly ? "" : `
        ${!s.isNew && p.status !== "A" && p.status !== "C" ? `<button data-status="A">Activate</button>` : ""}
        ${!s.isNew && p.status === "A" ? `<button data-status="S">Pause</button>` : ""}
        ${!s.isNew && p.status !== "C" ? `<button class="danger" data-status="C">Cancel promotion</button>` : ""}
        <button class="primary" id="save">${s.isNew ? "Create" : "Save"}</button>`}
    </div>

    ${s.errors.length ? `<div class="alert ${s.errors.every(e => e.startsWith("Note:")) ? "note" : "error"}">
      ${s.errors.length === 1 ? esc(s.errors[0]) : `Please check:<ul>${s.errors.map(e => `<li>${esc(e)}</li>`).join("")}</ul>`}</div>` : ""}

    <section class="panel"><h2><span class="step">${stepNo_()}</span>Promotion type</h2><div class="body">
      <div class="tiles">${TYPES.map(x => `
        <button class="tile ${x.id === t ? "selected" : ""}" data-type="${x.id}" ${dis}>
          <span class="code">${x.code}</span><strong>${x.name}</strong><span class="eg">${x.eg}</span></button>`).join("")}</div>
    </div></section>

    <section class="panel"><h2><span class="step">${stepNo_()}</span>Basics</h2><div class="body grid c3">
      <label class="field"><span>Code</span><input data-k="code" value="${esc(p.code)}" ${s.isNew ? "" : "disabled"} ${dis} maxlength="50" placeholder="RAMADAN-B2G1"></label>
      <label class="field span2"><span>Name</span><input data-k="name" value="${esc(p.name)}" ${dis} maxlength="100" placeholder="Buy 2 shampoo, get 1 free"></label>
      <label class="field span2"><span>Name (Arabic, printed on receipts)</span><input data-k="nameAr" dir="rtl" value="${esc(p.nameAr)}" ${dis}></label>
      <label class="field"><span>Campaign</span><input data-k="campaign" value="${esc(p.campaign)}" ${dis}></label>
    </div></section>

    <section class="panel"><h2><span class="step">${stepNo_()}</span>The deal<span class="sub">${esc(TYPES.find(x => x.id === t)?.eg ?? "")}</span></h2><div class="body">
      <div class="grid c4">
        ${needsBuyGet ? `
          <label class="field"><span>Customer buys</span><input type="number" min="1" step="1" data-k="buyQty" value="${p.buyQty || ""}" ${dis}></label>
          <label class="field"><span>and gets</span><input type="number" min="1" step="1" data-k="getQty" value="${p.getQty || ""}" ${dis}></label>` : ""}
        ${t === "MixAndMatch" ? `<label class="field"><span>Set size (any N units)</span><input type="number" min="2" step="1" data-k="buyQty" value="${p.buyQty || ""}" ${dis}></label>` : ""}
        ${rewardKinds.length ? `<label class="field"><span>${t === "BuyXGetY" ? "The reward units are" : "Reward"}</span><select data-k="rewardKind" ${dis}>
          ${rewardKinds.map(k => `<option value="${k}" ${p.rewardKind === k ? "selected" : ""}>${REWARD[k]}</option>`).join("")}</select></label>` : ""}
        ${!needsTiers && !(t === "BuyXGetXFree") && !(t === "BuyXGetY" && p.rewardKind === "X") ? `
          <label class="field"><span>${valueLabel}</span><input type="number" min="0" step="any" data-k="rewardValue" value="${p.rewardValue || ""}" ${dis}></label>` : ""}
        ${needsBuyGet ? `
          <label class="field"><span>Free / reward units</span><select data-k="freeMode" ${dis}>
            <option value="B" ${p.freeMode === "B" ? "selected" : ""}>Already in the basket</option>
            <option value="N" ${p.freeMode === "N" ? "selected" : ""}>Added as a new line</option></select></label>` : ""}
        ${needsBuyGet || t === "MixAndMatch" ? `
          <label class="field"><span>${t === "MixAndMatch" ? "Build sets from" : "Reward goes to"}</span><select data-k="rewardUnits" ${dis}>
            <option value="C" ${p.rewardUnits === "C" ? "selected" : ""}>Cheapest units (protects margin)</option>
            <option value="D" ${p.rewardUnits === "D" ? "selected" : ""}>Dearest units (best for customer)</option></select></label>` : ""}
        ${t === "BuyXGetY" && p.freeMode === "N" ? `
          <label class="field span2"><span>Reward item to add</span><input data-k="rewardItem" list="dl-items" value="${esc(p.rewardItem)}" ${dis} placeholder="Item code"></label>` : ""}
      </div>
      ${needsTiers ? `
        <p class="hint" style="margin:14px 0 6px">${t === "QuantityTier" ? "From quantity (across all qualifying items)" : "From spend (basket total of qualifying items)"} → reward</p>
        <table class="rows"><thead><tr><th>${t === "QuantityTier" ? "From quantity" : "From spend"}</th><th>${p.rewardKind === "P" ? "Discount %" : p.rewardKind === "A" ? "Amount off" : "Fixed price"}</th><th></th></tr></thead>
        <tbody>${p.tiers.map((x, i) => `<tr>
          <td><input type="number" step="any" data-tier="${i}" data-k="from" value="${x.from}" ${dis}></td>
          <td><input type="number" step="any" data-tier="${i}" data-k="value" value="${x.value}" ${dis}></td>
          <td style="width:40px">${readOnly ? "" : `<button class="icon" data-del-tier="${i}" title="Remove">✕</button>`}</td></tr>`).join("")}</tbody></table>
        ${readOnly ? "" : `<button class="link" id="add-tier" style="margin-top:8px">+ Add tier</button>`}` : ""}
    </div></section>

    <section class="panel"><h2><span class="step">${stepNo_()}</span>Items<span class="sub">${t === "BasketThreshold" ? "leave empty to count the whole basket" : "which items qualify"}</span></h2><div class="body">
      <table class="rows"><tbody>${scopeRows("T") || `<tr><td class="muted" colspan="4">${t === "BasketThreshold" ? "All items count." : "No items yet: the promotion would apply to every item."}</td></tr>`}</tbody></table>
      ${readOnly ? "" : `<button class="link" data-add-scope="T" style="margin-top:8px">+ Add qualifying items</button>`}
      ${rewardRole ? `
        <h3 style="font-size:13px;margin:18px 0 6px">Reward items <span class="hint">the items that become free or discounted</span></h3>
        <table class="rows"><tbody>${scopeRows("R") || `<tr><td class="muted" colspan="4">No reward items yet.</td></tr>`}</tbody></table>
        ${readOnly ? "" : `<button class="link" data-add-scope="R" style="margin-top:8px">+ Add reward items</button>`}` : ""}
    </div></section>

    <section class="panel"><h2><span class="step">${stepNo_()}</span>Screens<span class="sub">which B1 documents auto-apply this promotion</span></h2><div class="body">
      <div class="chips">${SCREENS.map(([code, label]) =>
        `<button class="chip ${(p.documents ?? "").split(",").includes(code) ? "on" : ""}" data-screen="${code}" ${dis}>${label}</button>`).join("")}</div>
      <p class="hint" style="margin-top:8px">${(p.documents ?? "").trim() === "" ? "None selected: applies on every screen (Quotation, Sales Order, Delivery, Invoice)." : "Applies only on the selected screen(s). A promotion started on a Quotation and copied to an Order still keeps its result either way (FR-18)."}</p>
    </div></section>

    ${multi() ? `
    <section class="panel"><h2><span class="step">${stepNo_()}</span>Companies<span class="sub">which companies this promotion applies to</span></h2><div class="body">
      <div class="chips">${info.companiesList.map(c =>
        `<button class="chip ${hasCompany(p, c.db) ? "on" : ""}" data-company="${esc(c.db)}" title="${esc(c.db)}" ${dis}>${esc(c.name)}${c.master ? " (master)" : ""}</button>`).join("")}</div>
      <p class="hint" style="margin-top:8px">${companiesHint(p)}</p>
      <p class="hint" style="margin-top:4px">Item groups, manufacturers and item properties are numbered per company, so the same number can mean something else in another company.</p>
      ${readOnly ? "" : `<button class="link" id="check-companies" style="margin-top:6px">Check items and groups in the other companies</button>`}
      ${s.notes === undefined ? "" : s.notes.length
        ? `<div class="alert note" style="margin-top:10px">Worth checking:<ul>${s.notes.map(n => `<li>${esc(n)}</li>`).join("")}</ul></div>`
        : `<p class="hint" style="margin-top:8px;color:var(--ok)">Nothing found: the items and groups it names match in the other companies.</p>`}
    </div></section>` : ""}

    <section class="panel"><h2><span class="step">${stepNo_()}</span>When</h2><div class="body grid c4">
      <label class="field"><span>Valid from</span><input type="date" data-k="validFrom" value="${esc(p.validFrom)}" ${dis}></label>
      <label class="field"><span>at</span><input type="time" data-k="validFromTime" value="${esc(p.validFromTime)}" ${dis}></label>
      <label class="field"><span>Valid to</span><input type="date" data-k="validTo" value="${esc(p.validTo)}" ${dis}></label>
      <label class="field"><span>until</span><input type="time" data-k="validToTime" value="${esc(p.validToTime)}" ${dis}></label>
      <div class="field span2"><span class="hint">Weekdays (none selected = every day)</span><div class="chips">${DAYS.map(([d, n]) =>
        `<button class="chip ${(p.weekdays ?? "").includes(d) ? "on" : ""}" data-day="${d}" ${dis}>${n}</button>`).join("")}</div></div>
      <label class="field"><span>Happy hour from</span><input type="time" data-k="timeFrom" value="${esc(p.timeFrom)}" ${dis}></label>
      <label class="field"><span>to</span><input type="time" data-k="timeTo" value="${esc(p.timeTo)}" ${dis}></label>
    </div></section>

    <section class="panel"><h2><span class="step">${stepNo_()}</span>Who<span class="sub">leave empty for everyone</span></h2><div class="body">
      <table class="rows"><tbody>${p.audience.map((a, i) => `<tr>
        <td style="width:190px"><select data-aud="${i}" data-k="dimension" ${dis}>${Object.entries(AUDIENCE).map(([k, v]) =>
          `<option value="${k}" ${a.dimension === k ? "selected" : ""}>${v}</option>`).join("")}</select></td>
        <td><input data-aud="${i}" data-k="value" value="${esc(a.value)}" ${dis}
          list="${{ GRP: "dl-custgroups", PL: "dl-pricelists" }[a.dimension] ?? ""}" placeholder="${{ CH: "B1, POS or WEB", CARD: "Customer code" }[a.dimension] ?? "Code"}"></td>
        <td style="width:40px">${readOnly ? "" : `<button class="icon" data-del-aud="${i}" title="Remove">✕</button>`}</td></tr>`).join("")
        || `<tr><td class="muted">Every customer, every channel.</td></tr>`}</tbody></table>
      ${readOnly ? "" : `<button class="link" id="add-aud" style="margin-top:8px">+ Add condition</button>`}
    </div></section>

    <section class="panel"><h2><span class="step">${stepNo_()}</span>Limits and stacking</h2><div class="body grid c4">
      <label class="field"><span>Priority (1 = highest)</span><input type="number" min="1" data-k="priority" value="${p.priority}" ${dis}></label>
      <label class="field"><span>Stacking</span><select data-k="stacking" ${dis}>
        <option value="S" ${p.stacking === "S" ? "selected" : ""}>Stackable: combines with others</option>
        <option value="E" ${p.stacking === "E" ? "selected" : ""}>Exclusive: nothing else on its lines</option></select></label>
      <label class="field"><span>Coupon code required</span><input data-k="coupon" value="${esc(p.coupon)}" ${dis} placeholder="none"></label>
      <label class="field"><span>Max times per document</span><input type="number" min="0" data-k="maxApps" value="${p.maxApps ?? ""}" ${dis} placeholder="no limit"></label>
      <label class="field"><span>Max discount per document</span><input type="number" min="0" step="any" data-k="maxDisc" value="${p.maxDisc || ""}" ${dis} placeholder="no limit"></label>
      <label class="field"><span>Budget</span><input type="number" min="0" step="any" data-k="budget" value="${p.budget || ""}" ${dis} placeholder="no budget"></label>
      <label class="field"><span>Budget used</span><input value="${money(p.budgetUsed)}" disabled></label>
      <label class="check" style="align-self:end;padding-bottom:6px"><input type="checkbox" data-k="allowBelow" ${p.allowBelow ? "checked" : ""} ${dis}> Allow below minimum price</label>
    </div></section>

    <section class="panel"><h2><span class="step">${stepNo_()}</span>Try it<span class="sub">runs this promotion, saved or not, on a sample basket</span></h2><div class="body">
      ${basketEditor(s)}
      <div style="display:flex;gap:10px;align-items:center;margin-top:10px">
        <button class="primary" id="simulate">Simulate</button>
        <label class="check"><input type="checkbox" id="with-active" ${s.withActive ? "checked" : ""}> Together with the active promotions</label>
      </div>
      ${s.result ? resultView(s.result) : ""}
    </div></section>

    ${datalist("dl-items", lk.items)}${datalist("dl-groups", lk.groups)}${datalist("dl-makers", lk.makers)}
    ${datalist("dl-custgroups", lk.custGroups)}${datalist("dl-pricelists", lk.priceLists)}`;

  wireEditor(s, lk);
}

function basketEditor(s) {
  return `<div class="grid c3" style="margin-bottom:8px">${multi() ? `<label class="field"><span>Company</span><select id="b-company">${
      info.companiesList.map(c => `<option value="${esc(c.db)}" ${(s.company ?? masterCompany().db) === c.db ? "selected" : ""}>${esc(c.name)}${c.master ? " (master)" : ""}</option>`).join("")
    }</select></label>` : ""}<label class="field"><span>Customer</span>
      <input id="b-card" value="${esc(s.cardCode)}" list="dl-items-cust" placeholder="C20000"></label></div>
    <table class="rows"><thead><tr><th>Item</th><th class="num" style="width:120px">Quantity</th><th class="num" style="width:150px">Unit price</th><th></th></tr></thead>
    <tbody>${s.basket.map((l, i) => `<tr>
      <td><input data-b="${i}" data-k="itemCode" value="${esc(l.itemCode)}" list="dl-items" placeholder="Item code"></td>
      <td><input type="number" step="any" data-b="${i}" data-k="quantity" value="${l.quantity}"></td>
      <td><input type="number" step="any" data-b="${i}" data-k="unitPrice" value="${l.unitPrice}" placeholder="price list"></td>
      <td style="width:40px"><button class="icon" data-del-b="${i}" title="Remove">✕</button></td></tr>`).join("")}</tbody></table>
    <button class="link" id="add-b" style="margin-top:6px">+ Add line</button>`;
}

function resultView(r) {
  const applied = r.promotions ?? [];
  return `
    <div class="result-totals">
      <div class="tile"><span class="code">GROSS</span><span class="value">${money(r.grossTotal)}</span></div>
      <div class="tile"><span class="code">DISCOUNT</span><span class="value" style="color:var(--ok)">${money(r.discountTotal)}</span></div>
      <div class="tile"><span class="code">NET</span><span class="value">${money(r.netTotal)}</span></div>
    </div>
    <table><thead><tr><th>Item</th><th class="num">Qty</th><th class="num">Price</th><th class="num">Discount %</th><th class="num">Discount</th><th class="num">Net</th><th>Promotions</th></tr></thead>
    <tbody>${r.lines.map(l => `<tr>
      <td>${esc(l.itemCode)}${l.isFree ? `<span class="free-tag">FREE</span>` : ""}${l.isAdded ? `<span class="free-tag" style="color:var(--accent)">ADDED</span>` : ""}</td>
      <td class="num">${l.quantity}</td><td class="num">${money(l.unitPrice)}</td><td class="num">${Number(l.discountPercent).toFixed(2)}</td>
      <td class="num">${money(l.discountAmount)}</td><td class="num">${money(l.netTotal)}</td><td>${esc(l.promotionCodes)}</td></tr>`).join("")}</tbody></table>
    <div class="grid c2" style="margin-top:14px">
      <div><strong>Why</strong><ul class="trace">${(r.trace ?? []).map(t =>
        `<li class="${t.applied ? "" : "no"}">${t.applied ? "✓" : "–"} <strong>${esc(t.code)}</strong> ${esc(t.message)}</li>`).join("")}</ul></div>
      <div><strong>Almost there</strong><ul class="trace">${(r.nearMisses ?? []).map(n =>
        `<li><strong>${esc(n.code)}</strong> ${esc(n.message)}</li>`).join("") || `<li class="no">Nothing close.</li>`}</ul></div>
    </div>
    ${applied.length === 0 ? `<div class="alert note" style="margin-top:12px">No promotion applied to this basket. The "Why" list says what stopped it.</div>` : ""}`;
}

function wireEditor(s, lk) {
  const p = s.p;
  const rerender = () => renderEditor(s, lk);
  const q = sel => main.querySelectorAll(sel);

  main.querySelector("#back").addEventListener("click", () => location.hash = "");

  q("[data-type]").forEach(b => b.addEventListener("click", () => { p.type = b.dataset.type; rerender(); }));

  // Plain fields: text, numbers, selects, checkboxes. Selects that change the layout re-render.
  q("[data-k]:not([data-scope]):not([data-tier]):not([data-aud]):not([data-b])").forEach(el => {
    const k = el.dataset.k;
    const handler = () => {
      if (el.type === "checkbox") p[k] = el.checked;
      else if (el.type === "number") p[k] = el.value === "" ? (k === "maxApps" ? null : 0) : num(el.value);
      else p[k] = el.value;
      if (el.tagName === "SELECT" && ["rewardKind", "freeMode"].includes(k)) rerender();
    };
    el.addEventListener(el.tagName === "SELECT" || el.type === "checkbox" ? "change" : "input", handler);
  });

  q("[data-day]").forEach(b => b.addEventListener("click", () => {
    const d = b.dataset.day, w = p.weekdays ?? "";
    p.weekdays = DAYS.map(([x]) => x).filter(x => x === d ? !w.includes(d) : w.includes(x)).join("");
    rerender();
  }));

  q("[data-company]").forEach(b => b.addEventListener("click", () => {
    const db = b.dataset.company;
    const on = hasCompany(p, db);
    const current = (p.companies ?? "").split(",").map(x => x.trim()).filter(Boolean);
    const next = on ? current.filter(x => x.toLowerCase() !== db.toLowerCase()) : [...current, db];
    p.companies = info.companiesList.map(c => c.db).filter(d => next.some(x => x.toLowerCase() === d.toLowerCase())).join(",");
    s.notes = undefined;
    rerender();
  }));
  main.querySelector("#check-companies")?.addEventListener("click", async () => {
    if (!await runCompanyCheck(s)) return toast("Tick another company first: the check compares the other companies with the master.", true);
    rerender();
  });

  q("[data-screen]").forEach(b => b.addEventListener("click", () => {
    const code = b.dataset.screen;
    const current = (p.documents ?? "").split(",").map(x => x.trim()).filter(Boolean);
    const next = current.includes(code) ? current.filter(x => x !== code) : [...current, code];
    p.documents = SCREENS.map(([c]) => c).filter(c => next.includes(c)).join(",");
    rerender();
  }));

  q("[data-scope]").forEach(el => el.addEventListener(el.tagName === "SELECT" || el.type === "checkbox" ? "change" : "input", () => {
    const row = p.scopes[+el.dataset.scope];
    row[el.dataset.k] = el.type === "checkbox" ? el.checked : el.value;
    if (el.dataset.k === "scopeType") { row.exclude = false; rerender(); }
  }));
  q("[data-add-scope]").forEach(b => b.addEventListener("click", () => {
    p.scopes.push({ role: b.dataset.addScope, scopeType: "I", value: "", exclude: false }); rerender();
  }));
  q("[data-del-scope]").forEach(b => b.addEventListener("click", () => { p.scopes.splice(+b.dataset.delScope, 1); rerender(); }));

  q("[data-tier]").forEach(el => el.addEventListener("input", () => { p.tiers[+el.dataset.tier][el.dataset.k] = num(el.value); }));
  main.querySelector("#add-tier")?.addEventListener("click", () => {
    const last = p.tiers[p.tiers.length - 1];
    p.tiers.push({ from: last ? last.from * 2 || 1 : 1, value: last ? last.value : 5 }); rerender();
  });
  q("[data-del-tier]").forEach(b => b.addEventListener("click", () => { p.tiers.splice(+b.dataset.delTier, 1); rerender(); }));

  q("[data-aud]").forEach(el => el.addEventListener(el.tagName === "SELECT" ? "change" : "input", () => {
    p.audience[+el.dataset.aud][el.dataset.k] = el.value;
    if (el.tagName === "SELECT") rerender();
  }));
  main.querySelector("#add-aud")?.addEventListener("click", () => { p.audience.push({ dimension: "GRP", value: "" }); rerender(); });
  q("[data-del-aud]").forEach(b => b.addEventListener("click", () => { p.audience.splice(+b.dataset.delAud, 1); rerender(); }));

  // Item code fields search B1 as you type (the first 30 matches).
  q('input[list="dl-items"]').forEach(el => el.addEventListener("input", debounce(async () => {
    if (el.value.length < 2) return;
    try {
      const rows = await call(`/lookups/items?q=${encodeURIComponent(el.value)}`);
      const known = new Set(lk.items.map(r => r.code));
      rows.forEach(r => { if (!known.has(r.code)) lk.items.push(r); });
      main.querySelector("#dl-items").innerHTML = lk.items.map(r => `<option value="${esc(r.code)}">${esc(r.name)}</option>`).join("");
    } catch { /* lookups are a convenience */ }
  }, 250)));

  // Basket for the simulation.
  main.querySelector("#b-card").addEventListener("input", e => s.cardCode = e.target.value);
  main.querySelector("#b-company")?.addEventListener("change", e => s.company = e.target.value);
  q("[data-b]").forEach(el => el.addEventListener("input", () => {
    const l = s.basket[+el.dataset.b];
    l[el.dataset.k] = el.dataset.k === "itemCode" ? el.value : el.value;
  }));
  main.querySelector("#add-b").addEventListener("click", () => { s.basket.push({ itemCode: "", quantity: 1, unitPrice: "" }); rerender(); });
  q("[data-del-b]").forEach(b => b.addEventListener("click", () => { s.basket.splice(+b.dataset.delB, 1); rerender(); }));
  main.querySelector("#with-active").addEventListener("change", e => s.withActive = e.target.checked);

  main.querySelector("#simulate").addEventListener("click", async () => {
    const lines = s.basket.filter(l => l.itemCode.trim()).map(l => ({
      itemCode: l.itemCode.trim(), quantity: num(l.quantity), unitPrice: l.unitPrice === "" ? null : num(l.unitPrice),
    }));
    if (!lines.length) return toast("Add at least one item to the basket.", true);
    try {
      s.result = await call("/simulate", { method: "POST", body: JSON.stringify({
        promotion: payload(p), lines, cardCode: s.cardCode || null, includeActive: !!s.withActive, amountDecimals: 2,
        company: multi() ? (s.company ?? masterCompany().db) : null,
      }) });
      rerender();
      main.querySelector(".result-totals")?.scrollIntoView({ behavior: "smooth", block: "center" });
    } catch (e) { toast("Simulation failed: " + e.message, true); }
  });

  main.querySelector("#save")?.addEventListener("click", async () => {
    const body = payload(p);
    try {
      const saved = s.isNew
        ? await call("/promotions", { method: "POST", body: JSON.stringify(body) })
        : await call(`/promotions/${encodeURIComponent(p.code)}`, { method: "PUT", body: JSON.stringify(body) });
      toast(`${saved.code} saved.`);
      s.p = { ...blank(), ...saved }; s.isNew = false; s.errors = [];
      await runCompanyCheck(s);   // saved either way; differences in the other companies are shown, not blocking
      history.replaceState(null, "", "#edit/" + encodeURIComponent(saved.code));
      rerender();
    } catch (e) { s.errors = e.errors ?? [e.message]; rerender(); window.scrollTo(0, 0); main.scrollTo(0, 0); }
  });

  q("[data-status]").forEach(b => b.addEventListener("click", async () => {
    const to = b.dataset.status;
    if (to === "C" && !confirm(`Cancel ${p.code}? It stops applying to new documents. Documents that already used it keep their discount.`)) return;
    try {
      const saved = await call(`/promotions/${encodeURIComponent(p.code)}/status`, { method: "POST", body: JSON.stringify({ status: to }) });
      s.p = { ...blank(), ...saved };
      toast(`${saved.code} is now ${STATUS[saved.status].toLowerCase()}.`);
      rerender();
    } catch (e) { toast(e.message, true); }
  }));
}

// What the API expects: blanks as null, rows without a value dropped.
function payload(p) {
  const blankToNull = v => (v === "" || v === undefined ? null : v);
  return {
    ...p,
    nameAr: blankToNull(p.nameAr), validFrom: blankToNull(p.validFrom), validFromTime: blankToNull(p.validFromTime),
    validTo: blankToNull(p.validTo), validToTime: blankToNull(p.validToTime), weekdays: blankToNull(p.weekdays),
    timeFrom: blankToNull(p.timeFrom), timeTo: blankToNull(p.timeTo), coupon: blankToNull(p.coupon), documents: blankToNull(p.documents), companies: blankToNull(p.companies),
    rewardItem: blankToNull(p.rewardItem), campaign: blankToNull(p.campaign),
    maxApps: p.maxApps || null,
    scopes: p.scopes.filter(x => String(x.value).trim()).map(x => ({ ...x, value: String(x.value).trim() })),
    audience: p.audience.filter(x => String(x.value).trim()).map(x => ({ ...x, value: String(x.value).trim() })),
    tiers: p.tiers.filter(x => x.from > 0 || x.value > 0),
  };
}

function debounce(fn, ms) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

// ── simulator (live promotions on a basket, as the add-on and worker see it) ──
async function simulator() {
  setNav("simulator");
  const s = { basket: [{ itemCode: "", quantity: 1, unitPrice: "" }], cardCode: "", result: null, company: masterCompany()?.db };
  const items = await lookup("items");
  const render = () => {
    main.innerHTML = `
      <div class="page-head"><h1>Simulator</h1></div>
      <p class="hint" style="margin:-8px 0 14px">All active promotions against a basket, exactly as the B1 add-on and the Mode B worker would apply them.</p>
      <section class="panel"><h2>Basket</h2><div class="body">
        ${basketEditor(s)}
        <div style="margin-top:10px"><button class="primary" id="simulate">Simulate</button></div>
        ${s.result ? resultView(s.result) : ""}
      </div></section>${datalist("dl-items", items)}`;
    main.querySelector("#b-card").addEventListener("input", e => s.cardCode = e.target.value);
    main.querySelector("#b-company")?.addEventListener("change", e => s.company = e.target.value);
    main.querySelectorAll("[data-b]").forEach(el => el.addEventListener("input", () => { s.basket[+el.dataset.b][el.dataset.k] = el.value; }));
    main.querySelector("#add-b").addEventListener("click", () => { s.basket.push({ itemCode: "", quantity: 1, unitPrice: "" }); render(); });
    main.querySelectorAll("[data-del-b]").forEach(b => b.addEventListener("click", () => { s.basket.splice(+b.dataset.delB, 1); render(); }));
    main.querySelector("#simulate").addEventListener("click", async () => {
      const lines = s.basket.filter(l => l.itemCode.trim());
      if (!lines.length) return toast("Add at least one item to the basket.", true);
      try {
        s.result = await call("/simulate", { method: "POST", body: JSON.stringify({
          promotion: null, cardCode: s.cardCode || null, amountDecimals: 2,
          company: multi() ? (s.company ?? masterCompany().db) : null,
          lines: lines.map(l => ({ itemCode: l.itemCode.trim(), quantity: num(l.quantity), unitPrice: l.unitPrice === "" ? null : num(l.unitPrice) })),
        }) });
        render();
      } catch (err) { toast("Simulation failed: " + err.message, true); }
    });
  };
  render();
}

// ── start ────────────────────────────────────────────────────────────────
(async () => {
  try {
    info = await call("/info");
    if (info.multiCompany) {
      try { info.companiesList = await call("/companies"); }
      catch { info.multiCompany = false; }   // the single-company screens still work
    }
    if (info.company) {
      const c = document.getElementById("company");
      c.textContent = multi() ? `${masterCompany().name} + ${info.companiesList.length - 1} more` : info.company;
      c.hidden = false;
    }
  } catch { /* the list shows the error */ }
  route();
})();
