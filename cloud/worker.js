// หน้าสมาชิกออนไลน์ ร้านยามุมยาเภสัช — Cloudflare Worker + D1
// ลูกค้าดูแต้ม/ประวัติยาได้ตลอด 24 ชม. แม้คอมร้านปิด (ข้อมูล ณ เวลาที่คอมร้านเปิดล่าสุด)
// โปรแกรมที่ร้านส่งข้อมูลมาทุก 5 นาที (/sync/push) และรับคำขอแก้ไข/เปลี่ยน PIN กลับไป (/sync/pull)
// ต้องตั้งค่าใน Worker: D1 binding ชื่อ DB และ Secret ชื่อ SYNC_KEY (คีย์เดียวกับในโปรแกรมร้าน)

const WRONG = 'เบอร์โทรหรือ PIN ไม่ถูกต้อง (ถ้ายังไม่มี PIN ติดต่อพนักงานเพื่อตั้ง PIN)';
const PIN_LOCK_AFTER = 5, PIN_BLOCK_AFTER = 10, SESSION_SEC = 15 * 60;
const enc = new TextEncoder();

class ApiError extends Error { constructor(status, msg) { super(msg); this.status = status; } }

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const path = url.pathname;
    try {
      if (path.startsWith('/sync/')) return await sync(req, env, path.slice(6));
      if (path.startsWith('/api/')) return json(await api(req, env, path.slice(5).replace(/\/+$/, '')));
      if (req.method !== 'GET') throw new ApiError(405, 'ไม่รองรับ');
      if (path === '/' || path === '/check' || path === '/index.html') {
        const page = await kvGet(env, 'page');
        return new Response(page || '<!doctype html><meta charset="utf-8"><p style="font-family:sans-serif;padding:20px">ระบบสมาชิกกำลังเตรียมข้อมูล กรุณาลองใหม่ภายหลัง</p>',
          { headers: headers({ 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' }) });
      }
      throw new ApiError(404, 'ไม่พบหน้านี้');
    } catch (e) {
      const status = e instanceof ApiError ? e.status : 500;
      if (status === 500) console.error(e);
      return json({ error: status === 500 ? 'เกิดข้อผิดพลาด กรุณาลองใหม่' : e.message }, status);
    }
  },
};

function headers(extra = {}) {
  return {
    'X-Frame-Options': 'DENY', 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer',
    'Strict-Transport-Security': 'max-age=31536000', ...extra,
  };
}
function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), { status, headers: headers({ 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' }) });
}
async function body(req, max = 8192) {
  const text = await req.text();
  if (text.length > max) throw new ApiError(413, 'ข้อมูลใหญ่เกินไป');
  try { return text ? JSON.parse(text) : {}; } catch { throw new ApiError(400, 'ข้อมูลไม่ถูกต้อง'); }
}
const str = (b, k) => (b[k] == null ? '' : String(b[k])).trim();
const nowSec = () => Math.floor(Date.now() / 1000);
// เวลาไทยรูปแบบเดียวกับโปรแกรมร้าน (ใช้เทียบว่า PIN ฝั่งไหนใหม่กว่า)
const nowTh = () => new Date(Date.now() + 7 * 3600e3).toISOString().replace('T', ' ').slice(0, 19);

async function kvGet(env, k) { const r = await env.DB.prepare('SELECT v FROM kv WHERE k=?').bind(k).first(); return r ? r.v : null; }

// ---------- จำกัดจำนวนครั้งต่อ IP ----------
async function hits(env, key, windowSec, add) {
  const now = nowSec();
  const r = await env.DB.prepare('SELECT n, reset FROM hits WHERE k=?').bind(key).first();
  let n = r && r.reset > now ? r.n : 0;
  if (add) {
    n++;
    const reset = r && r.reset > now ? r.reset : now + windowSec;
    await env.DB.prepare('INSERT INTO hits(k,n,reset) VALUES(?,?,?) ON CONFLICT(k) DO UPDATE SET n=excluded.n, reset=excluded.reset').bind(key, n, reset).run();
  }
  return n;
}

// ---------- PIN (PBKDF2-SHA1 20000 รอบ 32 ไบต์ เหมือน Rfc2898DeriveBytes ของโปรแกรมร้าน) ----------
const b64 = buf => btoa(String.fromCharCode(...new Uint8Array(buf)));
const unb64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
async function hashPin(pin, salt) {
  const key = await crypto.subtle.importKey('raw', enc.encode(pin), 'PBKDF2', false, ['deriveBits']);
  return b64(await crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-1', salt: unb64(salt), iterations: 20000 }, key, 256));
}
function sameText(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string' || a.length !== b.length) return false;
  let d = 0;
  for (let i = 0; i < a.length; i++) d |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return d === 0;
}
function validateNewPin(pin) {
  if (!/^[0-9]{4,6}$/.test(pin)) throw new ApiError(400, 'PIN ต้องเป็นตัวเลข 4-6 หลัก');
  if (/^(\d)\1+$/.test(pin) || '0123456789'.includes(pin) || '9876543210'.includes(pin))
    throw new ApiError(400, 'PIN เดาง่ายเกินไป (เช่น 1111, 1234) กรุณาเลือกใหม่');
}

// ---------- token เข้าสู่ระบบ: cid.pin_ver.หมดอายุ + ลายเซ็น (เปลี่ยน PIN แล้ว token เก่าใช้ไม่ได้) ----------
async function hmacKey(env) {
  return crypto.subtle.importKey('raw', enc.encode('session:' + env.SYNC_KEY), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
}
async function sign(env, payload) {
  const sig = await crypto.subtle.sign('HMAC', await hmacKey(env), enc.encode(payload));
  return [...new Uint8Array(sig)].map(x => x.toString(16).padStart(2, '0')).join('');
}
async function newToken(env, m) {
  const payload = `${m.cid}.${m.pin_ver}.${nowSec() + SESSION_SEC}`;
  return payload + '.' + await sign(env, payload);
}
async function session(req, env) {
  const auth = req.headers.get('Authorization') || '';
  const t = auth.startsWith('Bearer ') ? auth.slice(7).trim() : '';
  const parts = t.split('.');
  const expired = new ApiError(401, 'หมดเวลาการใช้งาน กรุณาเข้าสู่ระบบใหม่');
  if (parts.length !== 4) throw expired;
  const payload = parts.slice(0, 3).join('.');
  if (!sameText(await sign(env, payload), parts[3]) || +parts[2] < nowSec()) throw expired;
  const m = await env.DB.prepare('SELECT * FROM member WHERE cid=?').bind(+parts[0]).first();
  if (!m || m.pin_ver !== +parts[1] || m.blocked) throw expired;
  return m;
}

async function profile(env, m) {
  const d = JSON.parse(m.data);
  d.SyncedAt = await kvGet(env, 'lastSync'); // เวลาที่คอมร้านส่งข้อมูลล่าสุด
  d.token = await newToken(env, m); // ต่ออายุทุกครั้งที่ใช้งาน
  return d;
}

// ---------- API หน้าสมาชิก ----------
async function api(req, env, route) {
  const ip = req.headers.get('CF-Connecting-IP') || 'unknown';
  if (await hits(env, 'all:' + ip, 60, true) > 120) throw new ApiError(429, 'ใช้งานถี่เกินไป กรุณารอสักครู่');
  const post = req.method === 'POST';
  if (route === 'mode') return { public: true, cloud: true };
  if (route === 'settings') return { ShopName: (await kvGet(env, 'ShopName')) || 'ร้านยามุมยาเภสัช' };

  if (route === 'my/login' || route === 'my/pin') {
    if (await hits(env, 'fail:' + ip, 1800, false) >= 8) throw new ApiError(429, 'ใส่ข้อมูลผิดหลายครั้ง กรุณารอ 30 นาที');
    if (await hits(env, 'login:' + ip, 900, true) > 30) throw new ApiError(429, 'ใช้งานถี่เกินไป กรุณารอสักครู่');
  }
  try {
    if (route === 'my/login' && post) return await login(env, await body(req));
    if (route === 'my/profile') return await profile(env, await session(req, env));
    if (route === 'my/pin' && post) return await changePin(req, env, await body(req));
    if (route === 'my/logout' && post) return { ok: true };
    if (route === 'my/edit' && post) return await editRequest(req, env, await body(req));
    const dm = route.match(/^my\/drug\/(\d+)$/);
    if (dm) {
      await session(req, env);
      const r = await env.DB.prepare('SELECT data FROM drug WHERE id=?').bind(+dm[1]).first();
      if (!r) throw new ApiError(404, 'ไม่พบข้อมูลยา');
      return JSON.parse(r.data);
    }
  } catch (e) {
    if (e instanceof ApiError && e.status === 403) await hits(env, 'fail:' + ip, 1800, true);
    throw e;
  }
  throw new ApiError(404, 'ไม่พบ API: ' + route);
}

async function login(env, b) {
  const digits = str(b, 'phone').replace(/[^0-9]/g, ''), pin = str(b, 'pin');
  if (digits.length < 9 || !pin) throw new ApiError(400, 'กรุณาใส่เบอร์โทรและ PIN');
  // เบอร์เดียวอาจมีหลายคนในครอบครัว — หาคนที่ PIN ตรง
  const cands = (await env.DB.prepare('SELECT * FROM member WHERE phone=?').bind(digits).all()).results;
  if (!cands.length) throw new ApiError(403, WRONG);
  const now = nowSec();
  for (const c of cands) {
    if (c.blocked || c.fail >= PIN_BLOCK_AFTER || c.locked_until > now) continue;
    if (sameText(await hashPin(pin, c.salt), c.pin_hash)) {
      await env.DB.prepare('UPDATE member SET fail=0, locked_until=0 WHERE cid=?').bind(c.cid).run();
      return profile(env, c);
    }
  }
  await env.DB.batch(cands.map(c => env.DB.prepare(
    'UPDATE member SET fail=fail+1, locked_until=CASE WHEN (fail+1) % ? = 0 THEN ? ELSE locked_until END WHERE cid=?')
    .bind(PIN_LOCK_AFTER, now + 1800, c.cid)));
  const fails = Math.min(...cands.map(c => c.fail)) + 1;
  if (fails >= PIN_BLOCK_AFTER || cands.every(c => c.blocked)) throw new ApiError(403, 'ใส่ PIN ผิดเกินกำหนด — ติดต่อพนักงานเพื่อตั้ง PIN ใหม่');
  const locked = cands.every(c => c.locked_until > now) || fails % PIN_LOCK_AFTER === 0;
  throw new ApiError(403, locked ? 'ใส่ PIN ผิดหลายครั้ง กรุณารอ 30 นาที หรือติดต่อพนักงาน' : WRONG);
}

async function changePin(req, env, b) {
  const m = await session(req, env);
  const current = str(b, 'current'), fresh = str(b, 'pin');
  if (!sameText(await hashPin(current, m.salt), m.pin_hash)) throw new ApiError(403, 'PIN ปัจจุบันไม่ถูกต้อง');
  validateNewPin(fresh);
  if (fresh === current) throw new ApiError(400, 'PIN ใหม่ต้องไม่ซ้ำกับ PIN เดิม');
  const salt = b64(crypto.getRandomValues(new Uint8Array(16)));
  const hash = await hashPin(fresh, salt), at = nowTh();
  await env.DB.batch([
    env.DB.prepare('UPDATE member SET pin_hash=?, salt=?, pin_at=?, pin_ver=pin_ver+1, fail=0, locked_until=0 WHERE cid=?').bind(hash, salt, at, m.cid),
    env.DB.prepare('INSERT INTO outbox(kind,cid,data,created) VALUES(?,?,?,?)').bind('pin', m.cid, JSON.stringify({ hash, salt, pin_at: at }), at),
  ]);
  // เครื่องอื่นที่ค้างอยู่จะหลุด เหลือแค่เครื่องนี้ (ส่ง token ใหม่ให้)
  return { ok: true, token: await newToken(env, { cid: m.cid, pin_ver: m.pin_ver + 1 }) };
}

// ตรวจข้อมูลเบื้องต้นแบบเดียวกับโปรแกรมร้าน (ร้านตรวจซ้ำตอนรับคำขอ)
const EDIT_KEYS = ['Phone', 'Email', 'BirthDate', 'Address', 'Allergy', 'AllergyNote', 'Disease'];
function normalizeEdit(key, v) {
  v = String(v ?? '').trim();
  if (key === 'Phone') {
    v = v.replace(/[^0-9]/g, '');
    if (v && (v.length < 9 || v.length > 10)) throw new ApiError(400, 'เบอร์โทรต้องเป็นตัวเลข 9-10 หลัก');
  } else if (key === 'Email') {
    if (v.length > 50) throw new ApiError(400, 'อีเมลยาวเกิน 50 ตัวอักษร');
    if (v && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(v)) throw new ApiError(400, 'อีเมลไม่ถูกต้อง');
  } else if (key === 'BirthDate') {
    if (v) {
      const d = new Date(v + 'T00:00:00Z');
      if (!/^\d{4}-\d{2}-\d{2}$/.test(v) || isNaN(d) || d.getUTCFullYear() < 1900 || v > nowTh().slice(0, 10)) throw new ApiError(400, 'วันเกิดไม่ถูกต้อง');
    }
  } else if (key === 'Allergy') {
    const names = [];
    for (const x of v.split(/[,;\r\n]+/).map(s => s.trim()).filter(Boolean))
      if (!names.some(n => n.toLowerCase() === x.toLowerCase())) names.push(x);
    v = names.join(',');
    if (v.length > 600) throw new ApiError(400, 'รายการแพ้ยายาวเกินไป');
  } else if (v.length > 500) throw new ApiError(400, 'ข้อมูลยาวเกินไป');
  return v;
}

async function editRequest(req, env, b) {
  const m = await session(req, env);
  const data = JSON.parse(m.data);
  const cur = data.Contact || {};
  const note = str(b, 'note').slice(0, 300);
  const changes = [];
  for (const k of EDIT_KEYS) {
    if (!(k in b)) continue;
    const v = normalizeEdit(k, b[k]);
    if (v !== (cur[k] || '')) changes.push([k, v]);
  }
  if (!changes.length) throw new ApiError(400, 'ไม่มีข้อมูลที่เปลี่ยนแปลง');
  const at = nowTh();
  const recent = await env.DB.prepare("SELECT COUNT(*) n FROM outbox WHERE cid=? AND kind='edit' AND created >= ?")
    .bind(m.cid, new Date(Date.now() + 7 * 3600e3 - 864e5).toISOString().replace('T', ' ').slice(0, 19)).first();
  if (recent.n + changes.length > 20) throw new ApiError(429, 'ส่งคำขอมากเกินไป กรุณารอพนักงานตรวจสอบ');
  // แสดงว่ารอตรวจสอบทันที (โปรแกรมร้านจะส่งสถานะจริงมาแทนตอนซิงก์ครั้งถัดไป)
  data.PendingEdits = (data.PendingEdits || []).filter(p => !changes.some(c => c[0] === p.Field))
    .concat(changes.map(([Field, NewValue]) => ({ Field, NewValue, CreatedAt: at })));
  await env.DB.batch([
    ...changes.map(([Field, NewValue]) => env.DB.prepare('INSERT INTO outbox(kind,cid,data,created) VALUES(?,?,?,?)')
      .bind('edit', m.cid, JSON.stringify({ Field, NewValue, Note: note, CreatedAt: at }), at)),
    env.DB.prepare('UPDATE member SET data=? WHERE cid=?').bind(JSON.stringify(data), m.cid),
  ]);
  return profile(env, { ...m, data: JSON.stringify(data) });
}

// ---------- ซิงก์กับโปรแกรมร้าน (ต้องใช้ SYNC_KEY) ----------
async function sync(req, env, route) {
  const auth = req.headers.get('Authorization') || '';
  if (!env.SYNC_KEY || env.SYNC_KEY.length < 32 || !sameText(auth, 'Bearer ' + env.SYNC_KEY)) throw new ApiError(401, 'unauthorized');

  if (route === 'pull') {
    const rows = (await env.DB.prepare('SELECT * FROM outbox ORDER BY id LIMIT 200').all()).results;
    return json({ items: rows.map(r => ({ ...r, data: JSON.parse(r.data) })) });
  }
  const b = await body(req, 50 * 1024 * 1024);
  if (route === 'ack') {
    await env.DB.prepare('DELETE FROM outbox WHERE id IN (SELECT value FROM json_each(?))').bind(JSON.stringify(b.ids || [])).run();
    return json({ ok: true });
  }
  if (route !== 'push') throw new ApiError(404, 'not found');
  const st = [], at = nowTh();
  for (const [k, v] of Object.entries(b.settings || {}))
    st.push(env.DB.prepare('INSERT INTO kv(k,v) VALUES(?,?) ON CONFLICT(k) DO UPDATE SET v=excluded.v').bind(k, String(v)));
  for (const m of b.members || [])
    st.push(env.DB.prepare(`INSERT INTO member(cid,phone,pin_hash,salt,pin_at,pin_ver,blocked,data,updated) VALUES(?,?,?,?,?,1,?,?,?)
ON CONFLICT(cid) DO UPDATE SET phone=excluded.phone, blocked=excluded.blocked, data=excluded.data, updated=excluded.updated,
  pin_hash=CASE WHEN excluded.pin_at > member.pin_at THEN excluded.pin_hash ELSE member.pin_hash END,
  salt=CASE WHEN excluded.pin_at > member.pin_at THEN excluded.salt ELSE member.salt END,
  pin_ver=CASE WHEN excluded.pin_at > member.pin_at THEN member.pin_ver + 1 ELSE member.pin_ver END,
  fail=CASE WHEN excluded.pin_at > member.pin_at THEN 0 ELSE member.fail END,
  locked_until=CASE WHEN excluded.pin_at > member.pin_at THEN 0 ELSE member.locked_until END,
  pin_at=CASE WHEN excluded.pin_at > member.pin_at THEN excluded.pin_at ELSE member.pin_at END`)
      .bind(m.cid, m.phone, m.pin_hash, m.salt, m.pin_at, m.blocked ? 1 : 0, JSON.stringify(m.data), at));
  for (const d of b.drugs || [])
    st.push(env.DB.prepare('INSERT INTO drug(id,data) VALUES(?,?) ON CONFLICT(id) DO UPDATE SET data=excluded.data').bind(d.id, JSON.stringify(d.data)));
  // รายชื่อทั้งหมดที่ยังมี PIN อยู่ — คนที่ถูกยกเลิก PIN ที่ร้านจะถูกลบออกจากระบบออนไลน์
  if (Array.isArray(b.all)) st.push(env.DB.prepare('DELETE FROM member WHERE cid NOT IN (SELECT value FROM json_each(?))').bind(JSON.stringify(b.all)));
  if (Array.isArray(b.allDrugs)) st.push(env.DB.prepare('DELETE FROM drug WHERE id NOT IN (SELECT value FROM json_each(?))').bind(JSON.stringify(b.allDrugs)));
  st.push(env.DB.prepare('DELETE FROM hits WHERE reset < ?').bind(nowSec()));
  st.push(env.DB.prepare("INSERT INTO kv(k,v) VALUES('lastSync',?) ON CONFLICT(k) DO UPDATE SET v=excluded.v").bind(at));
  for (let i = 0; i < st.length; i += 50) await env.DB.batch(st.slice(i, i + 50));
  const count = await env.DB.prepare('SELECT COUNT(*) n FROM member').first();
  return json({ ok: true, members: count.n, at });
}
