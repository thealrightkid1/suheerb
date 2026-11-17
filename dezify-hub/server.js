'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const { loadConfig } = require('./lib/config');
const { SessionStore, verifyPassword } = require('./lib/sessions');
const { ensureLogin } = require('./lib/upstream');
const { createProxyHandler, publicOrigin } = require('./lib/proxy');
const { loginPage } = require('./lib/pages');

const cfg = loadConfig();
const store = new SessionStore(cfg);
const PUBLIC_DIR = path.join(__dirname, 'public');
const MIME = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'application/javascript', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon' };

const SECURITY_HEADERS = {
  'x-content-type-options': 'nosniff',
  'referrer-policy': 'same-origin',
  'x-frame-options': 'DENY',
};

function send(res, status, body, headers = {}) {
  res.writeHead(status, { ...SECURITY_HEADERS, 'cache-control': 'no-store', ...headers });
  res.end(body);
}
function redirect(res, location, headers = {}) { send(res, 302, '', { location, ...headers }); }

function readForm(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', c => { data += c; if (data.length > 1e5) req.destroy(); });
    req.on('end', () => resolve(Object.fromEntries(new URLSearchParams(data))));
    req.on('error', reject);
  });
}

function clientIp(req) {
  const fwd = cfg.trustProxy && req.headers['x-forwarded-for'];
  return (fwd ? fwd.split(',')[0] : req.socket.remoteAddress || '').trim();
}

function connectAll(session) {
  for (const app of cfg.apps) {
    if (app.enabled && app.upstream) ensureLogin(cfg, session, app).catch(() => {});
  }
}

function appSummary(req, session) {
  return cfg.apps.map(app => {
    const state = session.apps[app.id];
    return {
      id: app.id,
      name: app.name,
      description: app.description || '',
      icon: app.icon || app.id,
      enabled: !!(app.enabled && app.upstream),
      url: app.enabled && app.upstream ? publicOrigin(cfg, req, app) : null,
      startPath: app.startPath,
      shortcuts: app.shortcuts,
      status: state ? state.status : 'idle',
      message: state ? state.message : '',
    };
  });
}

async function hubHandler(req, res) {
  const url = new URL(req.url, 'http://x');
  const session = store.get(req);

  if (url.pathname === '/login') {
    if (req.method === 'POST') {
      const ip = clientIp(req);
      if (store.isLocked(ip)) return send(res, 429, loginPage(cfg, { error: 'Too many attempts. Try again in 15 minutes.' }), { 'content-type': MIME['.html'] });
      const form = await readForm(req);
      const user = (cfg.users || []).find(u => u.username.toLowerCase() === String(form.username || '').trim().toLowerCase());
      if (!user || !verifyPassword(user, form.password)) {
        store.recordFailure(ip);
        return send(res, 401, loginPage(cfg, { error: 'Wrong username or password.', username: form.username || '' }), { 'content-type': MIME['.html'] });
      }
      store.clearFailures(ip);
      store.destroy(session);
      const fresh = store.create(user);
      connectAll(fresh);
      return redirect(res, '/', { 'set-cookie': store.cookie(fresh.sid, cfg.sessionHours * 3600) });
    }
    if (session) return redirect(res, '/');
    return send(res, 200, loginPage(cfg), { 'content-type': MIME['.html'] });
  }

  if (url.pathname === '/logout') {
    store.destroy(session);
    return redirect(res, '/login', { 'set-cookie': store.cookie('', 0) });
  }

  if (!session) {
    if (url.pathname.startsWith('/api/')) return send(res, 401, '{"error":"signed out"}', { 'content-type': 'application/json' });
    return redirect(res, '/login');
  }

  if (url.pathname === '/api/me') {
    return send(res, 200, JSON.stringify({
      brandName: cfg.brandName,
      user: { username: session.user.username, displayName: session.user.displayName || session.user.username },
      apps: appSummary(req, session),
    }), { 'content-type': 'application/json' });
  }

  const reconnect = url.pathname.match(/^\/api\/apps\/([\w-]+)\/reconnect$/);
  if (reconnect && req.method === 'POST') {
    const app = cfg.apps.find(a => a.id === reconnect[1]);
    if (!app) return send(res, 404, '{}', { 'content-type': 'application/json' });
    try { await ensureLogin(cfg, session, app, { force: true }); } catch (_) { /* status carries the message */ }
    return send(res, 200, JSON.stringify(appSummary(req, session).find(a => a.id === app.id)), { 'content-type': 'application/json' });
  }

  // Static files for the hub shell.
  const file = url.pathname === '/' ? 'index.html' : url.pathname.replace(/^\/+/, '');
  const full = path.join(PUBLIC_DIR, path.normalize(file));
  if (!full.startsWith(PUBLIC_DIR + path.sep) || !fs.existsSync(full) || fs.statSync(full).isDirectory()) {
    return send(res, 404, 'Not found', { 'content-type': 'text/plain' });
  }
  const headers = { 'content-type': MIME[path.extname(full)] || 'application/octet-stream' };
  return send(res, 200, fs.readFileSync(full), headers);
}

function listen(name, port, handler) {
  const server = http.createServer((req, res) => {
    handler(req, res).catch(err => {
      console.error(`[${name}]`, err);
      if (!res.headersSent) send(res, 500, 'Something went wrong in the hub.', { 'content-type': 'text/plain' });
      else res.destroy();
    });
  });
  server.listen(port, cfg.host, () => console.log(`  ${name.padEnd(14)} http://${cfg.host === '0.0.0.0' ? 'localhost' : cfg.host}:${port}`));
}

console.log(`\n${cfg.brandName} is starting:`);
listen('hub', cfg.hubPort, hubHandler);
for (const app of cfg.apps) {
  if (app.enabled && app.upstream && app.port) listen(app.name, app.port, createProxyHandler(cfg, store, app));
}
