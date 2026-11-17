'use strict';
// Reverse proxy for one upstream app. Each app is served on its own port
// (or subdomain in production) so its root-relative links keep working.
const { Readable } = require('stream');
const { ensureLogin } = require('./upstream');
const { parseCookies, COOKIE_NAME } = require('./sessions');
const { page, escapeHtml } = require('./pages');

const HOP_BY_HOP = new Set([
  'connection', 'keep-alive', 'proxy-connection', 'transfer-encoding', 'upgrade', 'te', 'trailer',
  'host', 'content-length', 'expect',
]);
const STRIP_RESPONSE = new Set([
  'set-cookie', 'content-encoding', 'content-length', 'transfer-encoding', 'connection', 'keep-alive',
  'x-frame-options', 'strict-transport-security',
]);
const TEXT_TYPES = /^(text\/|application\/(javascript|json|xml|xhtml))/i;

function requestHost(cfg, req) {
  const fwd = cfg.trustProxy && req.headers['x-forwarded-host'];
  return (fwd || req.headers.host || 'localhost').split(',')[0].trim();
}
function requestProto(cfg, req) {
  const fwd = cfg.trustProxy && req.headers['x-forwarded-proto'];
  return (fwd || (req.socket.encrypted ? 'https' : 'http')).split(',')[0].trim();
}
function hostname(hostHeader) { return hostHeader.replace(/:\d+$/, ''); }

function publicOrigin(cfg, req, target) {
  if (target.publicUrl) return target.publicUrl.replace(/\/+$/, '');
  return `${requestProto(cfg, req)}://${hostname(requestHost(cfg, req))}:${target.port}`;
}
function hubOrigin(cfg, req) {
  return publicOrigin(cfg, req, { publicUrl: cfg.hubPublicUrl, port: cfg.hubPort });
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', c => chunks.push(c));
    req.on('end', () => resolve(chunks.length ? Buffer.concat(chunks) : null));
    req.on('error', reject);
  });
}

function bridgeScript(hub, appId) {
  return `(function(){
  if (window.top === window) return;
  var HUB = ${JSON.stringify(hub)}, APP = ${JSON.stringify(appId)};
  function send(){ try { parent.postMessage({ type: 'hub:location', app: APP,
    path: location.pathname + location.search + location.hash, title: document.title }, HUB); } catch (e) {} }
  send();
  window.addEventListener('hashchange', send);
  window.addEventListener('popstate', send);
  window.addEventListener('message', function (e) {
    if (e.origin === HUB && e.data && e.data.type === 'hub:back') history.back();
  });
  var t = document.querySelector('title');
  if (t) new MutationObserver(send).observe(t, { childList: true });
})();`;
}

function createProxyHandler(cfg, store, app) {
  const loginPath = app.login.path.toLowerCase();
  const logoutPattern = app.login.logoutPattern.toLowerCase();

  return async function handle(req, res) {
    const hub = hubOrigin(cfg, req);
    const self = publicOrigin(cfg, req, app);
    const url = new URL(req.url, 'http://x');
    const pathLower = url.pathname.toLowerCase();

    const session = store.get(req);
    if (!session) {
      // Not signed in to the hub: send the whole window to the hub login.
      res.writeHead(401, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      return res.end(`<!doctype html><script>top.location.href=${JSON.stringify(hub + '/login')};</script>`);
    }

    if (url.pathname === '/__hub/bridge.js') {
      res.writeHead(200, { 'content-type': 'application/javascript; charset=utf-8', 'cache-control': 'no-store' });
      return res.end(bridgeScript(hub, app.id));
    }

    if (url.pathname === '/__hub/retry') {
      try { await ensureLogin(cfg, session, app, { force: true }); } catch (_) { /* shown below */ }
      res.writeHead(302, { location: app.startPath });
      return res.end();
    }

    // The app's own logout button logs out of the whole hub.
    if (pathLower.startsWith(logoutPattern)) {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
      return res.end(`<!doctype html><script>top.location.href=${JSON.stringify(hub + '/logout')};</script>`);
    }

    const state = session.apps[app.id];
    try {
      if (state.status !== 'ok') await ensureLogin(cfg, session, app);
    } catch (err) {
      return sendLoginError(res, app, err.message);
    }

    // Never show the app's own login page: we are already signed in.
    if (pathLower === loginPath && req.method === 'GET') {
      const ret = url.searchParams.get('ReturnUrl') || app.startPath;
      res.writeHead(302, { location: ret.startsWith('/') && !ret.startsWith('//') ? ret : app.startPath });
      return res.end();
    }

    const body = ['GET', 'HEAD'].includes(req.method) ? null : await readBody(req);
    let upstreamRes;
    try {
      upstreamRes = await forward(req, body, state, self);
      if (needsLogin(upstreamRes)) {
        await ensureLogin(cfg, session, app, { force: true });
        upstreamRes = await forward(req, body, state, self);
      }
    } catch (err) {
      return sendLoginError(res, app, err.cause ? `Could not reach ${app.upstream}.` : err.message);
    }
    return sendResponse(req, res, upstreamRes, self, hub);
  };

  function needsLogin(r) {
    if (r.status === 401) return true;
    if (r.status >= 300 && r.status < 400) {
      const loc = (r.headers.get('location') || '').toLowerCase();
      return loc.includes(loginPath);
    }
    return false;
  }

  async function forward(req, body, state, self) {
    const headers = {};
    for (const [k, v] of Object.entries(req.headers)) {
      if (HOP_BY_HOP.has(k) || k === 'cookie' || k.startsWith('x-forwarded-')) continue;
      headers[k] = Array.isArray(v) ? v.join(', ') : v;
    }
    for (const k of ['origin', 'referer']) {
      if (headers[k]) headers[k] = headers[k].split(self).join(app.upstream);
    }
    const browserCookies = Object.entries(parseCookies(req.headers.cookie))
      .filter(([k]) => k !== COOKIE_NAME).map(([k, v]) => `${k}=${v}`).join('; ');
    headers.cookie = state.jar.header(browserCookies);
    if (!headers.cookie) delete headers.cookie;

    // Only ever talk to this app's own upstream, whatever the request line says.
    const target = new URL(app.upstream + (req.url.startsWith('/') ? req.url : '/'));
    if (target.origin !== new URL(app.upstream).origin) throw new Error('Blocked request to another host.');
    const r = await fetch(target, { method: req.method, headers, body, redirect: 'manual' });
    state.jar.store(r.headers.getSetCookie());
    return r;
  }

  async function sendResponse(req, res, r, self, hub) {
    const headers = {};
    r.headers.forEach((v, k) => { if (!STRIP_RESPONSE.has(k)) headers[k] = v; });
    if (headers.location) headers.location = rewriteOrigins(headers.location, req);
    if (headers['content-security-policy']) {
      headers['content-security-policy'] = headers['content-security-policy']
        .split(';').filter(d => !/^\s*frame-ancestors/i.test(d)).join(';');
    }

    const type = r.headers.get('content-type') || '';
    if (!r.body || req.method === 'HEAD') { res.writeHead(r.status, headers); return res.end(); }

    if (TEXT_TYPES.test(type)) {
      let text = rewriteOrigins(await r.text(), req);
      if (/text\/html/i.test(type)) {
        const tag = '<script src="/__hub/bridge.js"></script>';
        text = /<\/body>/i.test(text) ? text.replace(/<\/body>/i, `${tag}</body>`) : text + tag;
      }
      const buf = Buffer.from(text, 'utf8');
      headers['content-length'] = buf.length;
      res.writeHead(r.status, headers);
      return res.end(buf);
    }
    res.writeHead(r.status, headers);
    Readable.fromWeb(r.body).on('error', () => res.destroy()).pipe(res);
  }

  // Point links to any of the hub's apps at their proxied address.
  function rewriteOrigins(text, req) {
    for (const other of cfg.apps) {
      if (!other.enabled || !other.upstream) continue;
      text = text.split(other.upstream).join(publicOrigin(cfg, req, other));
    }
    return text;
  }

  function sendLoginError(res, app, message) {
    res.writeHead(502, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
    res.end(page(`Can't sign in to ${app.name}`, `
      <div class="card narrow">
        <h1>Can't sign in to ${escapeHtml(app.name)}</h1>
        <p>The hub tried to sign in to <code>${escapeHtml(app.upstream)}</code> and got this answer:</p>
        <p class="error">${escapeHtml(message)}</p>
        <p class="muted">Check the email and password for <b>${escapeHtml(app.id)}</b> in <code>config.json</code>, restart the hub, then try again.</p>
        <a class="btn" href="/__hub/retry">Try again</a>
      </div>`));
  }
}

module.exports = { createProxyHandler, hubOrigin, publicOrigin, requestHost };
