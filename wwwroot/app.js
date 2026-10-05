// ระบบสมาชิก ยามุมยาเภสัช — หน้าพนักงาน
const $ = (s, el = document) => el.querySelector(s);
const main = $('#main');
const TIER = { member: 'สมาชิก', silver: 'ซิลเวอร์', gold: 'โกลด์', platinum: 'แพลทินัม' };
let settings = {};
let rewardsCache = null;

// ---------- helpers ----------
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const money = n => Number(n || 0).toLocaleString('th-TH', { minimumFractionDigits: 0, maximumFractionDigits: 2 });
const int = n => Number(n || 0).toLocaleString('th-TH', { maximumFractionDigits: 2 });
// แหล่งแต้ม: 'cw' = ใช้แต้มจากโปรแกรม CW (แลก/ปรับที่ CW) / 'own' = คำนวณเองในระบบสมาชิก
const cwMode = () => settings.PointSource !== 'own';
function cwRuleText() {
  const c = settings.CwPoint || {};
  if (!c.ok) return 'อ่านการตั้งค่าแต้มจาก CW ไม่ได้';
  if (!c.Active) return 'ระบบแต้มใน CW ปิดอยู่';
  return `ซื้อ ${int(c.RecPrice)} บาท ได้ ${int(c.RecPoint)} แต้ม`;
}
const toDate = s => s ? new Date(s.replace(' ', 'T')) : null;
const dateTh = s => { const d = toDate(s); return d && d.getFullYear() > 1900 ? d.toLocaleDateString('th-TH', { day: 'numeric', month: 'short', year: '2-digit' }) : '-'; };
const dateTimeTh = s => { const d = toDate(s); return d ? d.toLocaleString('th-TH', { day: 'numeric', month: 'short', year: '2-digit', hour: '2-digit', minute: '2-digit' }) : '-'; };
const daysAgo = s => { const d = toDate(s); if (!d) return 'ยังไม่เคยซื้อ'; const n = Math.floor((Date.now() - d) / 864e5); return n <= 0 ? 'วันนี้' : n + ' วันก่อน'; };
const badge = t => `<span class="badge t-${t}">${TIER[t] || t}</span>`;
const age = s => { const d = toDate(s); if (!d || d.getFullYear() < 1900) return ''; const n = new Date(); let a = n.getFullYear() - d.getFullYear(); if (n < new Date(n.getFullYear(), d.getMonth(), d.getDate())) a--; return a >= 0 && a < 130 ? a + ' ปี' : ''; };

function toast(msg, err) {
  const t = $('#toast');
  t.textContent = msg; t.className = 'toast show' + (err ? ' err' : '');
  clearTimeout(toast.h); toast.h = setTimeout(() => t.className = 'toast', 2600);
}

function getPin() { try { return sessionStorage.getItem('pin') || ''; } catch { return ''; } }
function setPin(p) { try { sessionStorage.setItem('pin', p); } catch {} }
function getStaff() { try { return localStorage.getItem('staff') || ''; } catch { return ''; } }
function setStaff(s) { try { localStorage.setItem('staff', s); } catch {} }

async function api(path, body) {
  const opt = { method: body ? 'POST' : 'GET', headers: { 'X-Staff-Pin': getPin() } };
  if (body) { opt.body = JSON.stringify(body); opt.headers['Content-Type'] = 'application/json'; }
  const res = await fetch('/api/' + path, opt);
  const data = await res.json().catch(() => ({ error: 'ตอบกลับไม่ถูกต้อง' }));
  if (res.status === 401) {
    const p = prompt('ใส่ PIN พนักงาน');
    if (p) { setPin(p); return api(path, body); }
  }
  if (!res.ok) throw new Error(data.error || 'เกิดข้อผิดพลาด');
  return data;
}

// ---------- router ----------
const views = { dash: viewDash, members: viewMembers, edits: viewEdits, points: viewPoints, activity: viewActivity, rewards: viewRewards, settings: viewSettings };
async function route() {
  const name = (location.hash || '#dash').slice(1).split('?')[0];
  document.querySelectorAll('#tabs a').forEach(a => a.classList.toggle('active', a.getAttribute('href') === '#' + name));
  main.innerHTML = '<div class="empty">กำลังโหลด…</div>';
  try { await (views[name] || viewDash)(); }
  catch (e) { main.innerHTML = `<div class="card alert">${esc(e.message)}</div>`; }
}
window.addEventListener('hashchange', route);

// ---------- dashboard ----------
async function viewDash() {
  const d = await api('dashboard');
  const share = d.sales.Total ? Math.round(d.sales.MemberTotal / d.sales.Total * 100) : 0;
  const monthName = new Date().toLocaleDateString('th-TH', { month: 'long' });
  main.innerHTML = `
  <div class="grid kpis">
    <div class="card kpi"><div class="label">สมาชิกทั้งหมด</div><div class="value">${int(d.members)}</div>
      <div class="sub">ใหม่เดือนนี้ ${int(d.newThisMonth)} · มาใน 90 วัน ${int(d.active90)}</div></div>
    <div class="card kpi"><div class="label">ยอดขายสมาชิก (${monthName})</div><div class="value">฿${money(d.sales.MemberTotal)}</div>
      <div class="sub">${share}% ของยอดขายร้าน · ${int(d.sales.MemberBills)} บิล</div></div>
    <div class="card kpi"><div class="label">แต้มคงค้างทั้งระบบ</div><div class="value">${int(d.pointsOutstanding)}</div>
      <div class="sub">${cwMode() ? 'จากโปรแกรม CW · ' + esc(cwRuleText()) : settings.BahtPerPoint + ' บาท = 1 แต้ม'}</div></div>
    <div class="card kpi"><div class="label">${cwMode() ? 'ใช้แต้มเป็นส่วนลดเดือนนี้' : 'แลกแต้มเดือนนี้'}</div><div class="value">${int(d.redeemMonth.N)} ${cwMode() ? 'บิล' : 'ครั้ง'}</div>
      <div class="sub">ใช้ไป ${int(d.redeemMonth.Points)} แต้ม</div></div>
  </div>
  <div class="grid cols">
    <div class="card"><h3>ระดับสมาชิก (ตามยอดซื้อ 12 เดือน)</h3>
      <table>${['platinum', 'gold', 'silver', 'member'].map(t => `<tr><td>${badge(t)}</td>
        <td class="small muted">${t === 'member' ? 'ต่ำกว่า ฿' + int(settings.TierSilver) : '฿' + int(settings['Tier' + t[0].toUpperCase() + t.slice(1)]) + ' ขึ้นไป'}</td>
        <td class="num"><b>${int(d.tiers[t])}</b> คน</td></tr>`).join('')}</table></div>
    <div class="card"><h3>🎂 วันเกิดเดือน${monthName} (${d.birthdays.length})</h3>${memberMini(d.birthdays, m => dateTh(m.BirthDate).replace(/ \d+$/, '') + (age(m.BirthDate) ? ' · ' + age(m.BirthDate) : ''))}</div>
    <div class="card"><h3>ลูกค้าประจำ ยอดสูงสุด 12 เดือน</h3>${memberMini(d.top, m => '฿' + money(m.Spend365))}</div>
    <div class="card"><h3>ลูกค้าประจำที่หายไปเกิน 60 วัน</h3>${memberMini(d.dormant, m => daysAgo(m.LastVisit))}</div>
    <div class="card" style="grid-column:1/-1"><h3>ความเคลื่อนไหวแต้มล่าสุด</h3>${ledgerTable(d.recent, true, false)}</div>
  </div>`;
}

function memberMini(list, right) {
  if (!list.length) return '<div class="empty small">ไม่มีรายการ</div>';
  return `<table>${list.map(m => `<tr class="click" data-mid="${m.Id}"><td>${esc(m.FullName)} <span class="muted small">${esc(m.Code)}</span></td>
    <td>${badge(m.Tier)}</td><td class="num small">${right(m)}</td></tr>`).join('')}</table>`;
}

// ---------- members ----------
let memberState = { q: '', tier: '', sort: 'spend', bday: false };
async function viewMembers() {
  main.innerHTML = `
  <div class="toolbar">
    <input id="mq" placeholder="ค้นหาชื่อ เบอร์ รหัส บาร์โค้ด" value="${esc(memberState.q)}">
    ${['', 'platinum', 'gold', 'silver', 'member'].map(t => `<button class="chip ${memberState.tier === t ? 'on' : ''}" data-tier="${t}">${t ? TIER[t] : 'ทุกระดับ'}</button>`).join('')}
    <button class="chip ${memberState.bday ? 'on' : ''}" id="bday">🎂 เกิดเดือนนี้</button>
    <select id="msort">
      <option value="spend">เรียง: ยอดซื้อ 12 เดือน</option><option value="points">เรียง: แต้มคงเหลือ</option>
      <option value="last">เรียง: มาล่าสุด</option><option value="new">เรียง: สมัครล่าสุด</option><option value="name">เรียง: ชื่อ</option>
    </select>
    <span class="muted small" id="mcount"></span>
  </div>
  <div class="card" style="padding:0"><div class="table-wrap" id="mtable"><div class="empty">กำลังโหลด…</div></div></div>
  <p class="muted small">ข้อมูลลูกค้ามาจาก CW Pharma โดยตรง — เพิ่ม/แก้ไขข้อมูลลูกค้าที่โปรแกรม CW แล้วจะแสดงที่นี่ทันที</p>`;
  $('#msort').value = memberState.sort;
  let rows = [];
  const load = async () => { rows = await api('members?q=' + encodeURIComponent(memberState.q)); render(); };
  const render = () => {
    const m = new Date().getMonth();
    let list = rows.filter(r => (!memberState.tier || r.Tier === memberState.tier) && (!memberState.bday || (toDate(r.BirthDate)?.getMonth() === m && toDate(r.BirthDate).getFullYear() > 1900)));
    const key = { spend: r => -r.Spend365, points: r => -r.Points, last: r => -(toDate(r.LastVisit) || 0), new: r => -(toDate(r.Registered) || 0), name: r => r.FullName };
    const k = key[memberState.sort];
    list.sort((a, b) => { const x = k(a), y = k(b); return typeof x === 'string' ? x.localeCompare(y, 'th') : x - y; });
    $('#mcount').textContent = `${list.length} คน`;
    $('#mtable').innerHTML = list.length ? `<table><thead><tr><th>รหัส</th><th>ชื่อ</th><th class="hide-sm">โทร</th><th>ระดับ</th>
      <th class="num">แต้ม</th><th class="num">ยอด 12 เดือน</th><th class="num hide-sm">ครั้ง</th><th class="hide-sm">มาล่าสุด</th></tr></thead><tbody>
      ${list.map(r => `<tr class="click" data-mid="${r.Id}"><td class="small">${esc(r.Code)}</td>
        <td>${esc(r.FullName)} ${r.Allergy ? '<span class="pill red">แพ้ยา</span>' : ''}</td>
        <td class="hide-sm small">${esc(r.Phone)}</td><td>${badge(r.Tier)}</td><td class="num"><b>${int(r.Points)}</b></td>
        <td class="num">฿${money(r.Spend365)}</td><td class="num hide-sm">${int(r.Visits)}</td><td class="hide-sm small">${dateTh(r.LastVisit)}</td></tr>`).join('')}
      </tbody></table>` : '<div class="empty">ไม่พบสมาชิก</div>';
  };
  let t;
  $('#mq').oninput = e => { memberState.q = e.target.value.trim(); clearTimeout(t); t = setTimeout(load, 250); };
  main.querySelectorAll('[data-tier]').forEach(b => b.onclick = () => { memberState.tier = b.dataset.tier; main.querySelectorAll('[data-tier]').forEach(x => x.classList.toggle('on', x === b)); render(); });
  $('#bday').onclick = e => { memberState.bday = !memberState.bday; e.target.classList.toggle('on', memberState.bday); render(); };
  $('#msort').onchange = e => { memberState.sort = e.target.value; render(); };
  await load();
}

// ---------- member drawer ----------
let current = null;
async function openMember(id) {
  $('#drawer').innerHTML = '<div class="empty">กำลังโหลด…</div>';
  $('#drawer').classList.add('open'); $('#drawerBg').classList.add('open');
  try { current = await api('members/' + id); renderMember('orders'); }
  catch (e) { $('#drawer').innerHTML = `<div class="m-body"><div class="alert">${esc(e.message)}</div></div>`; }
}
function closeMember() { $('#drawer').classList.remove('open'); $('#drawerBg').classList.remove('open'); current = null; }
$('#drawerBg').onclick = closeMember;
document.addEventListener('keydown', e => { if (e.key === 'Escape' && current && !$('#dlg').open) closeMember(); });

function renderMember(tab) {
  const { member: m, orders, ledger } = current;
  const pct = m.NextTier ? Math.min(100, (m.Spend365 - m.TierMin) / (m.NextTierMin - m.TierMin) * 100) : 100;
  $('#drawer').innerHTML = `
  <div class="m-head"><div class="row">
    <div><div class="m-name">${esc(m.FullName)}</div>
      <div class="muted small">${esc(m.Code)} · บาร์โค้ด ${esc(m.BarCode || '-')} ${m.Wholesale ? '· <span class="pill">ลูกค้าขายส่ง</span>' : ''}</div>
      <div style="margin-top:6px">${badge(m.Tier)} <span class="small muted">${esc(m.Benefit || '')}</span></div></div>
    <div class="m-points"><div class="muted small">แต้มคงเหลือ</div><div class="value">${int(m.Points)}</div></div>
    <button class="close" onclick="closeMember()" aria-label="ปิด">×</button>
  </div>
  <div style="display:flex;gap:8px;margin-top:12px;flex-wrap:wrap">
    ${current.pointSource === 'cw' ? '<span class="small muted" style="align-self:center">แต้มจากโปรแกรม CW · ใช้แต้มแทนเงินสดที่หน้าขาย CW</span>' : `<button class="btn primary" onclick="redeemDialog()">🎁 แลกของรางวัล</button>
    <button class="btn" onclick="adjustDialog()">± ปรับแต้ม</button>`}
    <button class="btn" onclick="editDialog()">✏️ แก้ไขข้อมูล</button>
    <button class="btn" onclick="pinDialog()">🔒 ${current.pin ? 'เปลี่ยน PIN ดูประวัติยา' : 'ตั้ง PIN ดูประวัติยา'}</button>
  </div>
  <div class="small muted" style="margin-top:6px">${current.pin
    ? `ลูกค้าดูประวัติยาเองได้ (ตั้ง PIN โดย ${esc(current.pin.UpdatedBy)} ${dateTh(current.pin.UpdatedAt)})${current.pin.FailCount >= 10 ? ' · <b style="color:var(--danger)">ถูกล็อก — ตั้ง PIN ใหม่เพื่อปลดล็อก</b>' : ''}`
    : 'ยังไม่มี PIN — ลูกค้ายังดูประวัติยาเองไม่ได้'}</div></div>
  <div class="m-body">
    ${m.Allergy ? `<div class="alert">⚠ แพ้ยา: ${esc(m.Allergy)}</div>` : ''}
    ${m.Disease ? `<div class="alert warn">โรคประจำตัว: ${esc(m.Disease)}</div>` : ''}
    <div class="card facts">
      <div><div class="k">เบอร์โทร</div><div class="v">${esc(m.Phone || '-')}</div></div>
      <div><div class="k">วันเกิด</div><div class="v">${dateTh(m.BirthDate)} ${age(m.BirthDate) ? '(' + age(m.BirthDate) + ')' : ''}</div></div>
      <div><div class="k">สมัครเมื่อ</div><div class="v">${dateTh(m.Registered)}</div></div>
      <div><div class="k">มาล่าสุด</div><div class="v">${daysAgo(m.LastVisit)}</div></div>
      <div><div class="k">ยอดซื้อ 12 เดือน</div><div class="v">฿${money(m.Spend365)}</div></div>
      <div><div class="k">ยอดซื้อสะสมทั้งหมด</div><div class="v">฿${money(m.SpendAll)} · ${int(m.Visits)} ครั้ง</div></div>
      <div><div class="k">แต้มที่ได้จากการซื้อ${current.pointSource === 'cw' ? ' (CW)' : ''}</div><div class="v">${int(m.Earned)}</div></div>
      <div><div class="k">${current.pointSource === 'cw' ? 'ใช้เป็นส่วนลด / ปรับใน CW' : 'แลกไปแล้ว / ปรับ'}</div><div class="v">${int(m.Redeemed)} / ${m.Adjusted > 0 ? '+' : ''}${int(m.Adjusted)}</div></div>
    </div>
    <div class="card">
      ${m.NextTier ? `<div class="small">อีก <b>฿${money(m.ToNextTier)}</b> จะได้เป็น ${badge(m.NextTier)}</div>
        <div class="progress"><div style="width:${pct}%"></div></div>
        <div class="small muted">ยอดซื้อย้อนหลัง 12 เดือน ฿${money(m.Spend365)} / ฿${money(m.NextTierMin)}</div>`
        : `<div class="small">ระดับสูงสุดแล้ว ${badge(m.Tier)}</div>`}
    </div>
    ${m.Address || m.Comment ? `<div class="card small">${m.Address ? '<div><span class="muted">ที่อยู่:</span> ' + esc(m.Address) + '</div>' : ''}${m.Comment ? '<div><span class="muted">หมายเหตุ:</span> ' + esc(m.Comment) + '</div>' : ''}</div>` : ''}
    <div class="subtabs">
      <button class="chip ${tab === 'orders' ? 'on' : ''}" onclick="renderMember('orders')">ประวัติการซื้อ (${orders.length})</button>
      ${current.pointSource === 'cw' ? '' : `<button class="chip ${tab === 'ledger' ? 'on' : ''}" onclick="renderMember('ledger')">ประวัติแลก/ปรับแต้ม (${ledger.length})</button>`}
      <button class="chip ${tab === 'edits' ? 'on' : ''}" onclick="renderMember('edits')">แก้ไขข้อมูล (${current.edits.length})${pendingOf(current.edits) ? ` <span class="pill red">รออนุมัติ ${pendingOf(current.edits)}</span>` : ''}</button>
    </div>
    <div class="card" style="padding:0">${tab === 'orders' ? ordersTable(orders) : tab === 'edits' ? editsTable(current.edits, false) : ledgerTable(ledger, false, true)}</div>
  </div>`;
}

function ordersTable(orders) {
  if (!orders.length) return '<div class="empty">ยังไม่มีประวัติการซื้อใน CW</div>';
  return `<table><thead><tr><th>วันที่</th><th>เลขที่บิล</th><th class="num">ยอดสุทธิ</th><th class="num">แต้ม</th></tr></thead><tbody>
  ${orders.map(o => `<tr class="click" data-order="${o.Id}"><td class="small">${dateTimeTh(o.Date)}</td><td class="small">${esc(o.Code)}</td>
    <td class="num">฿${money(o.Net)}${o.Returned ? `<div class="small muted">คืน ฿${money(o.Returned)}</div>` : ''}</td>
    <td class="num">${o.Points ? '+' + int(o.Points) : '<span class="muted">-</span>'}${o.PayPoints ? `<div class="small" style="color:var(--danger)">ใช้ ${int(o.PayPoints)}</div>` : ''}</td></tr>`).join('')}</tbody></table>`;
}

async function toggleOrder(tr) {
  const next = tr.nextElementSibling;
  if (next && next.classList.contains('items')) { next.remove(); return; }
  const items = await api('orders/' + tr.dataset.order);
  const row = document.createElement('tr');
  row.className = 'items';
  row.innerHTML = `<td colspan="4">${items.map(i => `<div style="display:flex;justify-content:space-between;gap:10px">
    <span>${esc(i.Name)}</span>
    <span class="num" style="white-space:nowrap">${int(i.Qty)} ${esc(i.Unit || '')} × ฿${money(i.Price)}${i.Discount ? ` <span class="muted">ลด ${money(i.Discount)}</span>` : ''}</span></div>`).join('') || 'ไม่มีรายการ'}</td>`;
  tr.after(row);
}

function ledgerTable(rows, showName, canCancel) {
  if (!rows.length) return '<div class="empty small">ยังไม่มีรายการ</div>';
  return `<table><thead><tr><th>วันที่</th>${showName ? '<th>สมาชิก</th>' : ''}<th>รายการ</th><th class="num">แต้ม</th><th>พนักงาน</th>${canCancel ? '<th></th>' : ''}</tr></thead><tbody>
  ${rows.map(l => `<tr class="${l.IsCancelled ? 'cancelled' : ''}"><td class="small">${dateTimeTh(l.CreatedAt)}</td>
    ${showName ? `<td class="click" data-mid="${l.CustomerId}"><a href="javascript:void 0">${esc(l.FullName || '#' + l.CustomerId)}</a></td>` : ''}
    <td>${l.Kind === 'redeem' ? '🎁 ' : '± '}${esc(l.Description)}${l.IsCancelled ? `<div class="small">ยกเลิกโดย ${esc(l.CancelledBy)} ${dateTimeTh(l.CancelledAt)}</div>` : ''}</td>
    <td class="num"><b>${l.Points > 0 ? '+' : ''}${int(l.Points)}</b></td><td class="small">${esc(l.Staff)}</td>
    ${canCancel ? `<td>${l.IsCancelled ? '' : `<button class="btn sm danger" onclick="cancelLedger(${l.Id})">ยกเลิก</button>`}</td>` : ''}</tr>`).join('')}</tbody></table>`;
}

async function cancelLedger(id) {
  const staff = prompt('ยกเลิกรายการนี้ (แต้มจะคืน/หักกลับ)\nชื่อพนักงานที่ยกเลิก:', getStaff());
  if (!staff) return;
  try {
    await api(`ledger/${id}/cancel`, { staff });
    toast('ยกเลิกรายการแล้ว');
    if (current) await openMember(current.member.Id);
    if (location.hash === '#activity') route();
  } catch (e) { toast(e.message, true); }
}

// ---------- dialogs ----------
function dialog(title, body, okText, onOk) {
  const d = $('#dlg');
  d.innerHTML = `<form method="dialog"><div class="dlg-head">${title}</div><div class="dlg-body">${body}</div>
    <div class="dlg-foot"><button class="btn" value="cancel" formnovalidate>ยกเลิก</button><button class="btn primary" value="ok" id="dlgOk">${okText}</button></div></form>`;
  d.querySelector('form').onsubmit = async e => {
    if (e.submitter && e.submitter.value === 'cancel') return;
    e.preventDefault();
    const btn = $('#dlgOk'); btn.disabled = true;
    try { await onOk(d); d.close(); } catch (err) { toast(err.message, true); } finally { btn.disabled = false; }
  };
  d.showModal();
  return d;
}

const staffField = () => `<label class="f">ชื่อพนักงาน<input name="staff" required value="${esc(getStaff())}"></label>`;

async function redeemDialog() {
  const m = current.member;
  const rewards = (await api('rewards')).filter(r => r.IsActive);
  let sel = null;
  const d = dialog(`แลกของรางวัล · ${esc(m.FullName)} (มี ${int(m.Points)} แต้ม)`, `
    <div class="rewards-pick">${rewards.map(r => `<div class="rw ${r.Points > m.Points ? 'no' : ''}" data-id="${r.Id}" data-p="${r.Points}">
      <div><b>${esc(r.Name)}</b>${r.Note ? `<div class="small muted">${esc(r.Note)}</div>` : ''}</div><div class="num"><b>${int(r.Points)}</b> แต้ม</div></div>`).join('') || '<div class="empty">ยังไม่มีของรางวัล — เพิ่มที่แท็บ "ของรางวัล"</div>'}</div>
    <label class="f">จำนวน<input name="qty" type="number" min="1" value="1"></label>
    <label class="f">หมายเหตุ (ถ้ามี)<input name="note" placeholder="เช่น ใช้กับบิล ORR-…"></label>
    ${staffField()}`, 'ยืนยันแลก', async dlg => {
    if (!sel) throw new Error('เลือกของรางวัลก่อน');
    const f = dlg.querySelector('form');
    setStaff(f.staff.value.trim());
    current.member = await api(`members/${m.Id}/redeem`, { rewardId: sel, qty: f.qty.value, note: f.note.value, staff: f.staff.value });
    toast('แลกแต้มเรียบร้อย');
    await openMember(m.Id);
  });
  d.querySelectorAll('.rw:not(.no)').forEach(el => el.onclick = () => {
    d.querySelectorAll('.rw').forEach(x => x.classList.remove('sel')); el.classList.add('sel'); sel = +el.dataset.id;
  });
}

function adjustDialog() {
  const m = current.member;
  dialog(`ปรับแต้ม · ${esc(m.FullName)}`, `
    <label class="f">จำนวนแต้ม (ใส่ลบเพื่อหักแต้ม)<input name="points" type="number" required placeholder="เช่น 50 หรือ -20"></label>
    <label class="f">เหตุผล<input name="reason" required placeholder="เช่น โบนัสวันเกิด / แต้มย้อนหลังก่อนเริ่มระบบ"></label>
    ${staffField()}`, 'บันทึก', async dlg => {
    const f = dlg.querySelector('form');
    setStaff(f.staff.value.trim());
    await api(`members/${m.Id}/adjust`, { points: f.points.value, reason: f.reason.value, staff: f.staff.value });
    toast('ปรับแต้มเรียบร้อย');
    await openMember(m.Id);
  });
}

function pinDialog() {
  const m = current.member;
  dialog(`PIN ดูประวัติยา · ${esc(m.FullName)}`, `
    <div class="small muted">ให้ลูกค้าเป็นคนกด PIN เอง (ตัวเลข 4-6 หลัก) ลูกค้าใช้ <b>เบอร์โทร ${esc(m.Phone || '(ยังไม่มีเบอร์ในระบบ CW)')}</b> + PIN นี้ ดูประวัติการจ่ายยาที่หน้าลูกค้า</div>
    <label class="f">PIN ใหม่<input name="pin" type="password" inputmode="numeric" pattern="[0-9]{4,6}" maxlength="6" required autocomplete="off"></label>
    <label class="f">ยืนยัน PIN<input name="pin2" type="password" inputmode="numeric" maxlength="6" required autocomplete="off"></label>
    ${current.pin ? '<label style="display:flex;gap:8px;align-items:center"><input type="checkbox" name="nopin"> ยกเลิก PIN (ลูกค้าจะดูประวัติยาเองไม่ได้)</label>' : ''}
    ${staffField()}`, 'บันทึก', async dlg => {
    const f = dlg.querySelector('form');
    const remove = f.nopin?.checked;
    if (!remove && f.pin.value !== f.pin2.value) throw new Error('PIN ทั้งสองช่องไม่ตรงกัน');
    setStaff(f.staff.value.trim());
    await api(`members/${m.Id}/pin`, { pin: remove ? '' : f.pin.value, staff: f.staff.value });
    toast(remove ? 'ยกเลิก PIN แล้ว' : 'ตั้ง PIN แล้ว');
    await openMember(m.Id);
  });
  const f = $('#dlg form');
  if (f.nopin) f.nopin.onchange = () => { f.pin.required = f.pin2.required = !f.nopin.checked; };
}

// ---------- แก้ไขข้อมูลลูกค้า (บันทึกลงโปรแกรม CW) ----------
const EDIT_FIELDS = [
  ['Phone', 'เบอร์โทร', 'tel'], ['Email', 'อีเมล', 'email'], ['BirthDate', 'วันเกิด', 'date'],
  ['Address', 'ที่อยู่', 'area'], ['Allergy', 'แพ้ยา (ชื่อยาในโปรแกรม CW — ใช้เตือนตอนขาย)', 'drugs'],
  ['AllergyNote', 'อาการแพ้ / หมายเหตุแพ้ยา', 'area'], ['Disease', 'โรคประจำตัว', 'area']];
const EDIT_LABEL = { ...Object.fromEntries(EDIT_FIELDS.map(f => [f[0], f[1]])), Allergy: 'แพ้ยา' };

// ---------- เลือกชื่อยาแพ้ จากรายชื่อยาสามัญของ CW ----------
let drugNameList = null;
async function loadDrugNames() { if (!drugNameList) drugNameList = await api('drugnames'); return drugNameList; }
const splitDrugs = s => String(s || '').split(/[,;\n]+/).map(x => x.trim()).filter(Boolean);
// ชื่อยาที่ตรงกับรายการ CW (ไม่สนตัวพิมพ์เล็กใหญ่) ถ้าไม่ตรงคืน null
const matchDrug = x => drugNameList.find(n => n.toLowerCase() === x.toLowerCase()) || null;

function drugPickerHtml(name) {
  return `<div class="drugpick" data-picker="${name}"><div class="chips-sel"></div>
    <div style="display:flex;gap:6px"><input list="drugList" placeholder="พิมพ์ชื่อยา เช่น Amoxicillin แล้วเลือกจากรายการ" style="flex:1">
    <button type="button" class="btn sm">เพิ่ม</button></div>
    <div class="small muted hint"></div><input type="hidden" name="${name}"></div>`;
}
// ผูกช่องเลือกยา: เริ่มจากรายชื่อเดิม, ชื่อที่ไม่ตรงกับ CW แสดงเป็นสีแดง (ต้องลบ/เลือกใหม่)
function bindDrugPicker(root, name, initial) {
  const el = root.querySelector(`[data-picker="${name}"]`);
  if (!$('#drugList')) { const dl = document.createElement('datalist'); dl.id = 'drugList'; dl.innerHTML = drugNameList.map(n => `<option value="${esc(n)}">`).join(''); document.body.append(dl); }
  let list = splitDrugs(initial).map(x => matchDrug(x) || x);
  const input = el.querySelector('input[list]'), hidden = el.querySelector('input[type=hidden]');
  const render = () => {
    el.querySelector('.chips-sel').innerHTML = list.map((d, i) => `<span class="pill ${matchDrug(d) ? '' : 'red'}" style="margin:0 4px 4px 0">${esc(d)}
      <a href="javascript:void 0" data-rm="${i}" style="margin-left:4px">×</a></span>`).join('') || '<span class="small muted">ไม่แพ้ยา</span>';
    el.querySelectorAll('[data-rm]').forEach(a => a.onclick = () => { list.splice(+a.dataset.rm, 1); render(); });
    const bad = list.filter(d => !matchDrug(d));
    el.querySelector('.hint').textContent = bad.length ? 'ชื่อสีแดงไม่มีในรายชื่อยาของ CW — ลบแล้วเลือกชื่อที่ตรงจากรายการ' : '';
    hidden.value = list.join(',');
  };
  const add = () => {
    const v = input.value.trim(); if (!v) return;
    const hit = matchDrug(v);
    if (!hit) { toast('ไม่พบ "' + v + '" ในรายชื่อยาของ CW — เลือกจากรายการ', true); return; }
    if (!list.some(d => d.toLowerCase() === hit.toLowerCase())) list.push(hit);
    input.value = ''; render();
  };
  el.querySelector('button').onclick = add;
  input.onkeydown = e => { if (e.key === 'Enter') { e.preventDefault(); add(); } };
  input.onchange = () => { if (matchDrug(input.value.trim())) add(); };
  render();
}
const EDIT_STATUS = { pending: ['รออนุมัติ', 'red'], approved: ['อนุมัติแล้ว', ''], applied: ['พนักงานแก้', ''], rejected: ['ไม่อนุมัติ', 'gray'], superseded: ['ถูกแทนที่', 'gray'] };
const pendingOf = rows => rows.filter(e => e.Status === 'pending').length;
const editVal = (k, v) => !v ? '<span class="muted">(ว่าง)</span>' : k === 'BirthDate' ? dateTh(v) : esc(v);

async function editDialog() {
  const m = current.member;
  try { await loadDrugNames(); } catch (e) { toast(e.message, true); return; }
  const cur = { Phone: m.Phone, Email: m.Email, BirthDate: (m.BirthDate || '').slice(0, 10), Address: m.Address,
    Allergy: m.AllergyDrugs, AllergyNote: m.AllergyNote, Disease: m.Disease };
  if (cur.BirthDate.slice(0, 4) < '1900') cur.BirthDate = '';
  const d = dialog(`แก้ไขข้อมูล · ${esc(m.FullName)}`, `
    <div class="small muted">บันทึกแล้วจะแก้ในโปรแกรม CW ทันที (เก็บค่าเดิมไว้ในประวัติ)</div>
    ${EDIT_FIELDS.map(([k, label, type]) => `<label class="f">${label}${type === 'drugs' ? '</label>' + drugPickerHtml(k) : (type === 'area'
      ? `<textarea name="${k}" rows="2" maxlength="500">${esc(cur[k])}</textarea>`
      : `<input name="${k}" type="${type}" value="${esc(cur[k])}" ${type === 'tel' ? 'inputmode="numeric" maxlength="12"' : type === 'email' ? 'maxlength="50"' : ''}>`) + '</label>'}`).join('')}
    ${staffField()}`, 'บันทึกลง CW', async dlg => {
    const f = dlg.querySelector('form');
    const body = { staff: f.staff.value };
    EDIT_FIELDS.forEach(([k]) => { if (f[k].value.trim() !== (cur[k] || '').trim()) body[k] = f[k].value; });
    if (Object.keys(body).length === 1) throw new Error('ยังไม่ได้แก้ไขข้อมูล');
    if ('Allergy' in body && !confirm('ยืนยันแก้รายการแพ้ยาใน CW?\n\nเดิม: ' + (cur.Allergy || '(ไม่แพ้ยา)') + '\nใหม่: ' + (body.Allergy || '(ไม่แพ้ยา)'))) throw new Error('ยกเลิกแล้ว');
    setStaff(f.staff.value.trim());
    await api(`members/${m.Id}/edit`, body);
    toast('บันทึกลง CW แล้ว');
    await openMember(m.Id);
    renderMember('edits');
  });
  bindDrugPicker(d, 'Allergy', cur.Allergy);
}

const editRows = {};
function editsTable(rows, showName) {
  rows.forEach(e => editRows[e.Id] = e);
  if (!rows.length) return '<div class="empty small">ยังไม่มีการแก้ไขข้อมูล</div>';
  return `<table><thead><tr><th>วันที่</th>${showName ? '<th>สมาชิก</th>' : ''}<th>ช่อง</th><th>เดิม → ใหม่</th><th>สถานะ</th><th></th></tr></thead><tbody>
  ${rows.map(e => { const st = EDIT_STATUS[e.Status] || [e.Status, '']; return `<tr><td class="small">${dateTimeTh(e.CreatedAt)}<div class="muted">${e.Source === 'member' ? 'ลูกค้าขอแก้' : 'พนักงาน'}</div></td>
    ${showName ? `<td class="click" data-mid="${e.CustomerId}"><a href="javascript:void 0">${esc(e.FullName || '#' + e.CustomerId)}</a><div class="small muted">${esc(e.Code || '')}</div></td>` : ''}
    <td><b>${esc(e.Label || EDIT_LABEL[e.Field] || e.Field)}</b></td>
    <td class="small">${editVal(e.Field, e.OldValue)} → <b>${editVal(e.Field, e.NewValue)}</b>${e.Note ? `<div class="muted">หมายเหตุ: ${esc(e.Note)}</div>` : ''}</td>
    <td class="small"><span class="pill ${st[1]}">${st[0]}</span>${e.DecidedBy ? `<div class="muted">${esc(e.DecidedBy)} ${dateTimeTh(e.DecidedAt)}</div>` : ''}</td>
    <td style="white-space:nowrap">${e.Status === 'pending' ? `<button class="btn sm primary" onclick="decideEdit(${e.Id},true)">อนุมัติ</button>
      <button class="btn sm danger" onclick="decideEdit(${e.Id},false)">ไม่อนุมัติ</button>` : ''}</td></tr>`; }).join('')}</tbody></table>`;
}

async function decideEdit(id, approve) {
  const e = editRows[id];
  // คำขอแพ้ยาจากลูกค้า: ให้เภสัชกรเลือกชื่อยาที่ตรงกับรายชื่อยาของ CW ก่อนบันทึก
  if (approve && e && e.Field === 'Allergy') return approveAllergy(e);
  const staff = prompt((approve ? 'อนุมัติ — ข้อมูลจะถูกบันทึกลงโปรแกรม CW' : 'ไม่อนุมัติคำขอนี้') + '\nชื่อพนักงาน:', getStaff());
  if (!staff) return;
  const note = approve ? '' : (prompt('เหตุผลที่ไม่อนุมัติ (ลูกค้าจะเห็น):', '') || '');
  try {
    setStaff(staff.trim());
    await api(`edits/${id}/${approve ? 'approve' : 'reject'}`, { staff, note });
    toast(approve ? 'อนุมัติและบันทึกลง CW แล้ว' : 'ไม่อนุมัติแล้ว');
    if (current) { const mid = current.member.Id; await openMember(mid); renderMember('edits'); }
    if (location.hash.startsWith('#edits')) route();
    refreshEditBadge();
  } catch (e) { toast(e.message, true); }
}

async function afterDecide(msg) {
  toast(msg);
  if (current) { const mid = current.member.Id; await openMember(mid); renderMember('edits'); }
  if (location.hash.startsWith('#edits')) route();
  refreshEditBadge();
}

async function approveAllergy(e) {
  try { await loadDrugNames(); } catch (err) { toast(err.message, true); return; }
  const d = dialog(`อนุมัติแพ้ยา · ${esc(e.FullName || '')}`, `
    <div class="small">ลูกค้าเขียนมา: <b>${esc(e.NewValue || '(ไม่แพ้ยา)')}</b>${e.Note ? `<div class="muted">หมายเหตุ: ${esc(e.Note)}</div>` : ''}</div>
    <div class="small muted" style="margin:6px 0">ตรวจกับลูกค้า แล้วเลือกชื่อยาจากรายชื่อยาของ CW (CW ใช้ชื่อเหล่านี้เตือนตอนขาย) — รายการนี้จะแทนที่รายการแพ้ยาเดิมใน CW</div>
    <label class="f">รายการแพ้ยาที่จะบันทึกลง CW</label>${drugPickerHtml('pick')}
    ${staffField()}`, 'อนุมัติ บันทึกลง CW', async dlg => {
    const f = dlg.querySelector('form');
    if (splitDrugs(f.pick.value).some(x => !matchDrug(x))) throw new Error('ยังมีชื่อยาที่ไม่อยู่ในรายชื่อยาของ CW (สีแดง)');
    setStaff(f.staff.value.trim());
    await api(`edits/${e.Id}/approve`, { staff: f.staff.value, value: f.pick.value });
    await afterDecide('อนุมัติและบันทึกแพ้ยาลง CW แล้ว');
  });
  bindDrugPicker(d, 'pick', e.NewValue);
}

let editFilter = 'pending';
async function viewEdits() {
  const rows = await api('edits?status=' + editFilter);
  main.innerHTML = `<div class="toolbar"><h2 style="margin:0">คำขอแก้ไขข้อมูลลูกค้า</h2>
    ${[['pending', 'รออนุมัติ'], ['', 'ทั้งหมด']].map(([v, t]) => `<button class="chip ${editFilter === v ? 'on' : ''}" data-ef="${v}">${t}</button>`).join('')}</div>
  <div class="card" style="padding:0">${editsTable(rows, true)}</div>
  <p class="muted small">ลูกค้าขอแก้ข้อมูลจากหน้าสมาชิก → อนุมัติแล้วระบบจะบันทึกลงโปรแกรม CW ให้ (ตรวจข้อมูลแพ้ยา/โรคประจำตัวกับลูกค้าก่อนอนุมัติ)</p>`;
  main.querySelectorAll('[data-ef]').forEach(b => b.onclick = () => { editFilter = b.dataset.ef; route(); });
  refreshEditBadge(editFilter === 'pending' ? rows.length : undefined);
}

async function refreshEditBadge(n) {
  try { if (n === undefined) n = (await api('edits?status=pending')).length; } catch { return; }
  const a = $('#tabs a[href="#edits"]');
  if (a) a.innerHTML = 'คำขอแก้ไข' + (n ? ` <span class="pill red">${n}</span>` : '');
}

// ---------- คำนวณแต้มตามอัตรา CW ----------
async function viewPoints() {
  const d = await api('points/recalc');
  const c = d.config;
  main.innerHTML = `<div class="toolbar"><h2 style="margin:0">คำนวณแต้มตามอัตราของ CW</h2>
    <button class="btn primary" onclick="route()">🧮 คำนวณใหม่</button></div>
  <div class="card small">
    อัตราใน CW: ซื้อ ${int(c.RecPrice)} บาท ได้ ${int(c.RecPoint)} แต้ม · ช่วงวันที่ ${c.Begin ? dateTh(c.Begin) : 'ไม่กำหนด'} – ${c.End ? dateTh(c.End) : 'ไม่กำหนด'}
    ${!c.Active || !c.RecActive ? '<div class="alert warn" style="margin-top:8px">ระบบได้แต้มใน CW ปิดอยู่</div>' : ''}
    ${c.End && toDate(c.End) <= new Date() ? '<div class="alert warn" style="margin-top:8px">ช่วงสะสมแต้มใน CW สิ้นสุดแล้ว — บิลใหม่จะไม่ได้แต้ม ให้ขยายวันสิ้นสุดในโปรแกรม CW</div>' : ''}
    <div style="margin-top:8px">คิดจากยอดสุทธิแต่ละบิล (หักคืนสินค้า) ในช่วงวันที่ข้างบน ปัดเศษแต้มลงต่อบิล — <b>แสดงผลอย่างเดียว ยังไม่บันทึกลง CW</b></div>
  </div>
  <div class="grid kpis">
    <div class="card kpi"><div class="label">สมาชิกที่มีบิลในช่วงนี้</div><div class="value">${int(d.rows.length)}</div></div>
    <div class="card kpi"><div class="label">แต้มที่ควรได้ (คำนวณ)</div><div class="value">${int(d.totalCalc)}</div></div>
    <div class="card kpi"><div class="label">แต้มใน CW ตอนนี้</div><div class="value">${int(d.totalCurrent)}</div></div>
  </div>
  <div class="card" style="padding:0">${d.rows.length ? `<div class="table-wrap"><table><thead><tr><th>รหัส</th><th>ชื่อ</th><th class="num">บิล</th><th class="num">ยอดสุทธิ</th>
    <th class="num">ควรได้</th><th class="num">ใน CW</th><th class="num">ต่าง</th></tr></thead><tbody>
    ${d.rows.map(r => `<tr class="click" data-mid="${r.Id}"><td class="small">${esc(r.Code)}</td><td>${esc(r.FullName)}</td><td class="num">${int(r.Bills)}</td>
      <td class="num">฿${money(r.Net)}</td><td class="num"><b>${int(r.Calc)}</b></td><td class="num">${int(r.InCw)}</td>
      <td class="num" style="${r.Calc - r.InCw ? 'color:var(--danger)' : ''}">${r.Calc - r.InCw > 0 ? '+' : ''}${int(r.Calc - r.InCw)}</td></tr>`).join('')}
    </tbody></table></div>` : '<div class="empty">ไม่มีบิลสมาชิกในช่วงสะสมแต้ม</div>'}</div>`;
}

// ---------- activity ----------
async function viewActivity() {
  const rows = await api('ledger');
  main.innerHTML = `<h2>ประวัติแลกแต้ม / ปรับแต้ม (200 รายการล่าสุด)</h2><div class="card" style="padding:0">${ledgerTable(rows, true, true)}</div>`;
}

// ---------- rewards ----------
async function viewRewards() {
  const rows = await api('rewards');
  main.innerHTML = `<div class="toolbar"><h2 style="margin:0">ของรางวัล</h2><button class="btn primary" onclick="rewardDialog()">+ เพิ่มของรางวัล</button></div>
  <div class="card" style="padding:0">${rows.length ? `<table><thead><tr><th>ชื่อ</th><th class="num">แต้มที่ใช้</th><th>หมายเหตุ</th><th>สถานะ</th><th></th></tr></thead><tbody>
  ${rows.map(r => `<tr><td><b>${esc(r.Name)}</b></td><td class="num">${int(r.Points)}</td><td class="small muted">${esc(r.Note)}</td>
    <td>${r.IsActive ? '<span class="pill">เปิดใช้</span>' : '<span class="pill red">ปิด</span>'}</td>
    <td><button class="btn sm" data-edit='${esc(JSON.stringify(r))}'>แก้ไข</button></td></tr>`).join('')}</tbody></table>` : '<div class="empty">ยังไม่มีของรางวัล</div>'}</div>
  ${cwMode() ? `<div class="alert warn" style="margin-top:12px">ตอนนี้ใช้แต้มจากโปรแกรม CW — ลูกค้าใช้แต้มแทนเงินสดที่หน้าขาย CW ของรางวัลในหน้านี้จะไม่แสดงให้ลูกค้าและแลกไม่ได้ (เปลี่ยนได้ที่ ตั้งค่า → แหล่งแต้ม)</div>`
    : `<p class="muted small">คิดแต้ม: ทุก ${esc(settings.BahtPerPoint)} บาท = 1 แต้ม → ของรางวัล 100 แต้ม เท่ากับลูกค้าซื้อครบ ฿${int(settings.BahtPerPoint * 100)}</p>`}`;
  main.querySelectorAll('[data-edit]').forEach(b => b.onclick = () => rewardDialog(JSON.parse(b.dataset.edit)));
}

function rewardDialog(r = { Name: '', Points: '', Note: '', IsActive: true }) {
  dialog(r.Id ? 'แก้ไขของรางวัล' : 'เพิ่มของรางวัล', `
    <label class="f">ชื่อของรางวัล<input name="name" required value="${esc(r.Name)}"></label>
    <label class="f">แต้มที่ใช้แลก<input name="points" type="number" min="1" required value="${esc(r.Points)}"></label>
    <label class="f">หมายเหตุ<input name="note" value="${esc(r.Note)}"></label>
    <label style="display:flex;gap:8px;align-items:center"><input type="checkbox" name="active" ${r.IsActive ? 'checked' : ''}> เปิดให้แลก</label>`,
    'บันทึก', async dlg => {
      const f = dlg.querySelector('form');
      await api('rewards', { Id: r.Id || null, Name: f.name.value, Points: f.points.value, Note: f.note.value, IsActive: f.active.checked });
      toast('บันทึกแล้ว'); route();
    });
}

// ---------- settings ----------
async function viewSettings() {
  const s = settings = await api('settings');
  const fld = (k, label, type = 'text', extra = '') => `<label class="f">${label}<input name="${k}" type="${type}" value="${esc(s[k])}" ${extra}></label>`;
  main.innerHTML = `<form id="sf" class="grid" style="gap:16px">
  <div class="card"><h3>ร้าน & การเชื่อมต่อ</h3><div class="form-grid">
    ${fld('ShopName', 'ชื่อร้าน')}
    <label class="f">ฐานข้อมูล CW Pharma<input value="${esc(s.CwDatabase)} (อ่านอย่างเดียว — แก้ได้ที่ config.ini)" disabled></label>
    <label class="f">รวมลูกค้าขายส่งเป็นสมาชิก<select name="IncludeWholesale"><option value="0">ไม่รวม</option><option value="1">รวม</option></select></label>
  </div></div>
  <div class="card"><h3>การสะสมแต้ม</h3><div class="form-grid">
    <label class="f">แหล่งแต้ม<select name="PointSource">
      <option value="cw">ใช้แต้มจากโปรแกรม CW (แนะนำ)</option><option value="own">คำนวณแต้มเองในระบบสมาชิก (แบบเดิม)</option></select></label>
  </div>
  <div id="cwPointBox" class="small" style="margin-top:10px"></div>
  <div id="ownPointBox"><div class="form-grid" style="margin-top:10px">
    ${fld('BahtPerPoint', 'ยอดซื้อกี่บาท = 1 แต้ม', 'number', 'min="1" step="any" required')}
    ${fld('PointStartDate', 'เริ่มนับแต้มจากบิลตั้งแต่วันที่', 'date', 'required')}
  </div><p class="small muted">แต้มคำนวณจากบิลขายใน CW ทุกบิลที่ผูกรหัสลูกค้า (หักยอดรับคืนสินค้าแล้ว) — แต้มต่อบิลปัดเศษลง เปลี่ยนค่าแล้วแต้มทุกคนคำนวณใหม่ทันที</p></div></div>
  <div class="card"><h3>ระดับสมาชิก (ยอดซื้อย้อนหลัง 12 เดือน)</h3><div class="form-grid">
    ${fld('TierSilver', 'ซิลเวอร์ ตั้งแต่ (บาท)', 'number', 'min="1" required')}
    ${fld('TierGold', 'โกลด์ ตั้งแต่ (บาท)', 'number', 'min="1" required')}
    ${fld('TierPlatinum', 'แพลทินัม ตั้งแต่ (บาท)', 'number', 'min="1" required')}
    ${fld('BenefitMember', 'สิทธิ์ระดับสมาชิก')}${fld('BenefitSilver', 'สิทธิ์ระดับซิลเวอร์')}
    ${fld('BenefitGold', 'สิทธิ์ระดับโกลด์')}${fld('BenefitPlatinum', 'สิทธิ์ระดับแพลทินัม')}
  </div></div>
  <div class="card"><h3>ความปลอดภัย</h3><div class="form-grid">
    <label class="f">PIN พนักงาน (ใช้ตอนแลก/ปรับแต้ม/แก้ตั้งค่า) ${s.HasPin ? '— ตั้งไว้แล้ว' : '— ยังไม่ได้ตั้ง'}
      <input name="StaffPin" type="password" placeholder="${s.HasPin ? 'เว้นว่าง = ใช้ PIN เดิม' : 'เว้นว่าง = ไม่ใช้ PIN'}" autocomplete="new-password"></label>
    ${s.HasPin ? '<label style="display:flex;gap:8px;align-items:center"><input type="checkbox" name="clearPin"> ยกเลิกการใช้ PIN</label>' : ''}
  </div></div>
  <div class="card"><h3>อัปเดตโปรแกรม</h3><div class="form-grid">
    <label class="f">อัปเดตอัตโนมัติ<select name="AutoUpdate"><option value="1">เปิด — ติดตั้งเองตอนไม่มีคนใช้งาน</option><option value="0">ปิด — แจ้งเตือนอย่างเดียว</option></select></label>
    <div id="updInfo" class="small"></div>
  </div><div style="display:flex;gap:8px;margin-top:12px">
    <button type="button" class="btn" id="updCheck">ตรวจหาอัปเดตตอนนี้</button>
    <button type="button" class="btn primary" id="updInstall" hidden>อัปเดตเลย</button></div></div>
  <div><button class="btn primary">บันทึกการตั้งค่า</button></div></form>`;
  const f = $('#sf');
  f.IncludeWholesale.value = s.IncludeWholesale;
  f.AutoUpdate.value = s.AutoUpdate;
  f.PointSource.value = s.PointSource === 'own' ? 'own' : 'cw';
  const c = s.CwPoint || {};
  const today = new Date().toISOString().slice(0, 10);
  const warn = [];
  if (!c.ok) warn.push('อ่านการตั้งค่าแต้มจาก CW ไม่ได้' + (c.error ? ': ' + c.error : ''));
  else {
    if (!c.Active) warn.push('ระบบแต้มขายปลีกใน CW ยังปิดอยู่');
    if (c.Active && !c.RecActive) warn.push('CW ปิดการ "ได้แต้ม" อยู่ — ลูกค้าจะไม่ได้แต้มจากการซื้อ');
    if (c.End && c.End <= today) warn.push(`ช่วงเวลาสะสมแต้มใน CW สิ้นสุด ${dateTh(c.End)} — หลังจากนี้ CW อาจไม่ให้แต้ม ให้ขยายวันสิ้นสุดใน CW`);
  }
  $('#cwPointBox').innerHTML = c.ok ? `
    <div>ได้แต้ม: <b>${esc(cwRuleText())}</b> ${c.RecActive ? '' : '(ปิด)'}</div>
    <div>ใช้แต้ม: ${c.PayActive ? `<b>${int(c.PayPoint)} แต้ม = ${int(c.PayPrice)} บาท</b>` : 'ปิด'} — ใช้แต้มแทนเงินสดที่หน้าขาย CW</div>
    <div>ช่วงเวลา: ${dateTh(c.Begin)} – ${dateTh(c.End)}</div>
    <div class="muted">แก้การตั้งค่าแต้มได้ที่โปรแกรม CW · ระบบสมาชิกแสดงยอดแต้มและประวัติจาก CW และปิดปุ่มแลก/ปรับแต้มในระบบนี้ (กันแต้มซ้ำสองที่)</div>
    ${warn.map(w => `<div class="alert warn" style="margin-top:6px">⚠ ${esc(w)}</div>`).join('')}`
    : warn.map(w => `<div class="alert warn">⚠ ${esc(w)}</div>`).join('');
  const syncPoint = () => { const cw = f.PointSource.value === 'cw'; $('#cwPointBox').hidden = !cw; $('#ownPointBox').hidden = cw; };
  f.PointSource.onchange = syncPoint; syncPoint();
  const showUpd = u => {
    $('#updInfo').innerHTML = `<div>เวอร์ชันที่ใช้อยู่: <b>${esc(u.current)}</b></div>
      ${u.configured ? `<div>เวอร์ชันล่าสุด: <b>${esc(u.latest || '-')}</b> ${u.available ? '<span class="pill">มีอัปเดต</span>' : u.latest ? '<span class="muted">(ล่าสุดแล้ว)</span>' : ''}</div>
      ${u.notes ? `<div class="muted">มีอะไรใหม่: ${esc(u.notes)}</div>` : ''}
      <div class="muted">ตรวจล่าสุด: ${u.lastCheck ? dateTimeTh(u.lastCheck) : '-'}</div>` : '<div class="muted">ยังไม่ได้ตั้งที่อยู่อัปเดต (UpdateUrl)</div>'}
      ${u.error ? `<div style="color:var(--danger)">${esc(u.error)}</div>` : ''}`;
    $('#updInstall').hidden = !u.available;
  };
  api('update').then(showUpd).catch(() => {});
  $('#updCheck').onclick = async () => {
    try { const u = await api('update/check', {}); showUpd(u); showUpdateBar(u); toast(u.available ? 'มีเวอร์ชันใหม่ ' + u.latest : 'ใช้เวอร์ชันล่าสุดอยู่แล้ว'); }
    catch (e) { toast(e.message, true); api('update').then(showUpd); }
  };
  $('#updInstall').onclick = installUpdate;
  f.onsubmit = async e => {
    e.preventDefault();
    const body = {};
    new FormData(f).forEach((v, k) => body[k] = v);
    body.StaffPin = f.clearPin?.checked ? '' : (body.StaffPin || (s.HasPin ? '__keep__' : ''));
    delete body.clearPin;
    try {
      settings = await api('settings', body);
      if (body.StaffPin && body.StaffPin !== '__keep__') setPin(body.StaffPin);
      toast('บันทึกการตั้งค่าแล้ว'); applyShop(); route();
    } catch (err) { toast(err.message, true); }
  };
}

// ---------- global events ----------
document.addEventListener('click', e => {
  const tr = e.target.closest('[data-order]');
  if (tr) return toggleOrder(tr).catch(err => toast(err.message, true));
  const m = e.target.closest('[data-mid]');
  if (m) openMember(m.dataset.mid);
});

$('#quickForm').onsubmit = async e => {
  e.preventDefault();
  const q = $('#quickInput').value.trim();
  if (!q) return;
  try {
    const rows = await api('members?q=' + encodeURIComponent(q));
    if (rows.length === 1) { openMember(rows[0].Id); $('#quickInput').value = ''; }
    else { memberState.q = q; location.hash = '#members'; if (location.hash === '#members') route(); if (!rows.length) toast('ไม่พบสมาชิก', true); }
  } catch (err) { toast(err.message, true); }
};

// ---------- auto update ----------
function showUpdateBar(u) {
  $('#appVer').textContent = 'v' + u.current;
  const bar = $('#updateBar');
  bar.hidden = !u.available;
  if (u.available) {
    bar.innerHTML = `<span>มีโปรแกรมเวอร์ชันใหม่ <b>${esc(u.latest)}</b>${u.notes ? ' — ' + esc(u.notes) : ''}</span>
      <button class="btn sm primary" onclick="installUpdate()">อัปเดตเลย</button>`;
  }
}

async function installUpdate() {
  if (!confirm('อัปเดตโปรแกรมตอนนี้? ระบบจะหยุดประมาณ 10 วินาทีแล้วเปิดใหม่เอง')) return;
  const bar = $('#updateBar');
  bar.hidden = false;
  bar.innerHTML = 'กำลังดาวน์โหลดและติดตั้งอัปเดต… อย่าปิดหน้าต่างโปรแกรม';
  let target;
  try { target = (await api('update/install', {})).latest; }
  catch (e) { bar.innerHTML = esc(e.message); toast(e.message, true); return; }
  bar.innerHTML = `ติดตั้งเวอร์ชัน ${esc(target)} แล้ว กำลังเริ่มโปรแกรมใหม่…`;
  for (let i = 0; i < 90; i++) {
    await new Promise(r => setTimeout(r, 2000));
    try {
      const v = await (await fetch('/api/version', { cache: 'no-store' })).json();
      if (v.version === target) { location.reload(); return; }
    } catch {}
  }
  bar.innerHTML = 'โปรแกรมยังไม่กลับมา — ลองเปิดไฟล์ YaMumMember.exe ใหม่';
}

function applyShop() {
  $('#shopName').textContent = settings.ShopName || 'ร้านยามุมยาเภสัช';
  document.title = 'ระบบสมาชิก ' + (settings.ShopName || '');
}

(async () => {
  try {
    settings = await api('settings');
    applyShop();
    $('#cwStatus').textContent = 'เชื่อมต่อ CW Pharma (' + settings.CwDatabase + ') แล้ว';
    $('#cwStatus').className = 'status ok';
  } catch (e) {
    $('#cwStatus').textContent = 'เชื่อมต่อไม่ได้: ' + e.message;
    $('#cwStatus').className = 'status err';
  }
  route();
  api('update').then(showUpdateBar).catch(() => {});
  refreshEditBadge();
  setInterval(() => refreshEditBadge(), 2 * 60 * 1000);
  setInterval(() => api('update').then(showUpdateBar).catch(() => {}), 30 * 60 * 1000);
})();
