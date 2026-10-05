-- ฐานข้อมูล D1 สำหรับหน้าสมาชิกออนไลน์ (วางใน Cloudflare → D1 → Console แล้วกด Execute)
-- เก็บเฉพาะสมาชิกที่มี PIN — ข้อมูลส่งมาจากโปรแกรมที่ร้านทุก 5 นาทีที่เครื่องร้านเปิดอยู่
CREATE TABLE IF NOT EXISTS member (
  cid INTEGER PRIMARY KEY,
  phone TEXT NOT NULL,
  pin_hash TEXT NOT NULL,
  salt TEXT NOT NULL,
  pin_at TEXT NOT NULL,          -- เวลาที่ตั้ง PIN ล่าสุด (เวลาไทย yyyy-MM-dd HH:mm:ss)
  pin_ver INTEGER NOT NULL DEFAULT 1,
  blocked INTEGER NOT NULL DEFAULT 0,
  fail INTEGER NOT NULL DEFAULT 0,
  locked_until INTEGER NOT NULL DEFAULT 0,
  data TEXT NOT NULL,            -- ข้อมูลหน้าสมาชิก (JSON) แบบเดียวกับที่โปรแกรมร้านแสดง
  updated TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS member_phone ON member(phone);
CREATE TABLE IF NOT EXISTS drug (id INTEGER PRIMARY KEY, data TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT NOT NULL);
-- สิ่งที่ลูกค้าทำออนไลน์ (เปลี่ยน PIN / ขอแก้ข้อมูล) รอโปรแกรมร้านมารับ
CREATE TABLE IF NOT EXISTS outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, cid INTEGER NOT NULL, data TEXT NOT NULL, created TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS hits (k TEXT PRIMARY KEY, n INTEGER NOT NULL, reset INTEGER NOT NULL);
-- เข้าผ่าน LINE: บัญชี LINE ที่ผูกกับสมาชิก (Worker สร้างให้เองถ้ายังไม่มี)
CREATE TABLE IF NOT EXISTS line_link (sub TEXT PRIMARY KEY, cid INTEGER NOT NULL, pin_ver INTEGER NOT NULL, created TEXT NOT NULL);
