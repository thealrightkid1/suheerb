'use strict';
// Minimal server-side cookie jar. The browser never sees upstream cookies;
// the hub keeps them here, one jar per hub session per app.
class CookieJar {
  constructor() { this.cookies = new Map(); }

  store(setCookieHeaders = []) {
    for (const raw of setCookieHeaders) {
      const parts = raw.split(';').map(s => s.trim());
      const eq = parts[0].indexOf('=');
      if (eq < 1) continue;
      const name = parts[0].slice(0, eq);
      const value = parts[0].slice(eq + 1);
      let expired = value === '';
      for (const attr of parts.slice(1)) {
        const [k, v = ''] = attr.split('=');
        const key = k.toLowerCase();
        if (key === 'max-age' && Number(v) <= 0) expired = true;
        if (key === 'expires' && Date.parse(v) < Date.now()) expired = true;
      }
      if (expired) this.cookies.delete(name);
      else this.cookies.set(name, value);
    }
  }

  header(extra = '') {
    const merged = new Map();
    for (const pair of extra.split(';')) {
      const eq = pair.indexOf('=');
      if (eq > 0) merged.set(pair.slice(0, eq).trim(), pair.slice(eq + 1).trim());
    }
    for (const [k, v] of this.cookies) merged.set(k, v);
    return [...merged].map(([k, v]) => `${k}=${v}`).join('; ');
  }

  clear() { this.cookies.clear(); }
}

module.exports = { CookieJar };
