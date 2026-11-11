// Seeds Turkish skill/plugin descriptions into the Notlar SQLite DB.
// Usage: node --no-warnings tools/seed-skill-translations.mjs
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const appData = process.env.APPDATA;
if (!appData) {
  throw new Error('APPDATA environment variable is not set.');
}
const dbPath = path.join(appData, 'Notlar', 'notlar.db');
const jsonPath = path.join(__dirname, 'skill-translations.tr.json');

const entries = JSON.parse(readFileSync(jsonPath, 'utf8'));

const db = new DatabaseSync(dbPath);
db.exec('PRAGMA busy_timeout=5000;');

db.exec(`
  CREATE TABLE IF NOT EXISTS skill_translations (
    skill_id TEXT PRIMARY KEY,
    description_tr TEXT NOT NULL,
    updated_at TEXT NOT NULL
  );
`);

const upsert = db.prepare(`
  INSERT INTO skill_translations (skill_id, description_tr, updated_at)
  VALUES (?, ?, ?)
  ON CONFLICT(skill_id) DO UPDATE SET
    description_tr = excluded.description_tr,
    updated_at = excluded.updated_at;
`);

function nowLocalIso() {
  const d = new Date();
  const pad = (n, len = 2) => String(n).padStart(len, '0');
  const yyyy = d.getFullYear();
  const MM = pad(d.getMonth() + 1);
  const dd = pad(d.getDate());
  const HH = pad(d.getHours());
  const mm = pad(d.getMinutes());
  const ss = pad(d.getSeconds());
  const fff = pad(d.getMilliseconds(), 3);
  return `${yyyy}-${MM}-${dd}T${HH}:${mm}:${ss}.${fff}+03:00`;
}

let count = 0;
for (const entry of entries) {
  if (!entry || typeof entry.id !== 'string' || typeof entry.description_tr !== 'string') {
    continue;
  }
  upsert.run(entry.id, entry.description_tr, nowLocalIso());
  count++;
}

db.close();

console.log(`Seeded ${count} skill_translations rows into ${dbPath}`);
