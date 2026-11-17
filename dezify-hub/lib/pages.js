'use strict';
// Small server-rendered pages: hub login and error screens.

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

const STYLE = `
  *{box-sizing:border-box;margin:0}
  body{font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;background:#0f172a;color:#e2e8f0;
       min-height:100vh;display:flex;align-items:center;justify-content:center;padding:24px}
  .card{background:#1e293b;border:1px solid #334155;border-radius:16px;padding:36px;width:100%;max-width:400px;
        box-shadow:0 20px 50px rgba(0,0,0,.35)}
  .card.narrow{max-width:520px}
  h1{font-size:1.35rem;margin-bottom:6px}
  p{color:#94a3b8;line-height:1.55;margin:10px 0}
  .muted{font-size:.9rem}
  .error{color:#fecaca;background:#7f1d1d55;border:1px solid #b91c1c;border-radius:8px;padding:10px 12px}
  code{background:#0f172a;padding:2px 6px;border-radius:5px;font-size:.85em;color:#cbd5e1}
  label{display:block;font-size:.85rem;color:#cbd5e1;margin:16px 0 6px}
  input{width:100%;padding:11px 13px;border-radius:9px;border:1px solid #334155;background:#0f172a;color:#f1f5f9;font-size:1rem}
  input:focus{outline:2px solid #3b82f6;border-color:transparent}
  .btn{display:inline-block;margin-top:22px;width:100%;text-align:center;padding:12px;border:0;border-radius:9px;
       background:#3b82f6;color:#fff;font-size:1rem;font-weight:600;cursor:pointer;text-decoration:none}
  .btn:hover{background:#2563eb}
  .brand{display:flex;align-items:center;gap:12px;margin-bottom:22px}
  .logo{width:42px;height:42px;border-radius:11px;background:linear-gradient(135deg,#ef4444,#b91c1c);display:grid;
        place-items:center;font-weight:800;color:#fff;letter-spacing:.5px}
`;

function page(title, body) {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>${escapeHtml(title)}</title>
<style>${STYLE}</style></head><body>${body}</body></html>`;
}

function initials(name) {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0].toUpperCase()).join('') || 'H';
}

function loginPage(cfg, { error = '', username = '' } = {}) {
  return page(`Sign in · ${cfg.brandName}`, `
  <form class="card" method="post" action="/login">
    <div class="brand"><div class="logo">${escapeHtml(initials(cfg.brandName))}</div>
      <div><h1>${escapeHtml(cfg.brandName)}</h1><p style="margin:0">One sign-in for every app</p></div></div>
    ${error ? `<p class="error">${escapeHtml(error)}</p>` : ''}
    <label for="u">Username</label>
    <input id="u" name="username" autocomplete="username" value="${escapeHtml(username)}" required autofocus>
    <label for="p">Password</label>
    <input id="p" name="password" type="password" autocomplete="current-password" required>
    <button class="btn" type="submit">Sign in</button>
  </form>`);
}

module.exports = { page, loginPage, escapeHtml };
