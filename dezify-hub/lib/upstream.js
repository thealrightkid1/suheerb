'use strict';
// Signs a hub session in to an upstream app (ASP.NET Core Identity login form)
// using the credentials stored in config.json.

const RETRY_AFTER_FAILURE_MS = 60 * 1000;

function credentialsFor(cfg, session, app) {
  const override = session.user.appCredentials && session.user.appCredentials[app.id];
  return override || app.credentials || null;
}

function findHiddenInput(html, name) {
  const inputs = html.match(/<input\b[^>]*>/gi) || [];
  for (const tag of inputs) {
    if (new RegExp(`name=["']${name.replace(/[.$]/g, '\\$&')}["']`, 'i').test(tag)) {
      const m = tag.match(/value=["']([^"']*)["']/i);
      if (m) return m[1];
    }
  }
  return null;
}

function extractError(html) {
  const m = html.match(/class="[^"]*validation-summary-errors[^"]*"[^>]*>([\s\S]*?)<\/div>/i);
  const text = (m ? m[1] : '').replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
  return text || 'The app did not accept the login.';
}

async function doLogin(cfg, session, app) {
  const state = session.apps[app.id];
  const creds = credentialsFor(cfg, session, app);
  if (!creds || !creds.email || !creds.password) {
    throw new Error('No credentials are set for this app in config.json.');
  }
  state.jar.clear();
  const loginUrl = app.upstream + app.login.path;

  const page = await fetch(loginUrl, { headers: { cookie: state.jar.header() }, redirect: 'manual' });
  state.jar.store(page.headers.getSetCookie());
  const html = await page.text();
  const token = findHiddenInput(html, '__RequestVerificationToken');
  if (!token) throw new Error(`Could not find the login form at ${app.login.path}.`);

  const body = new URLSearchParams();
  body.set(app.login.userField, creds.email);
  body.set(app.login.passwordField, creds.password);
  body.set('Input.RememberMe', 'false');
  body.set('__RequestVerificationToken', token);

  const res = await fetch(loginUrl, {
    method: 'POST',
    redirect: 'manual',
    headers: {
      'content-type': 'application/x-www-form-urlencoded',
      cookie: state.jar.header(),
      origin: app.upstream,
      referer: loginUrl,
    },
    body: body.toString(),
  });
  state.jar.store(res.headers.getSetCookie());
  const location = res.headers.get('location') || '';
  if (res.status >= 300 && res.status < 400 && !location.includes(app.login.path)) return;
  throw new Error(extractError(await res.text()));
}

// Ensures only one login runs per session+app at a time and avoids hammering
// an app with bad credentials.
async function ensureLogin(cfg, session, app, { force = false } = {}) {
  const state = session.apps[app.id];
  if (state.pending) return state.pending;
  if (!force && state.status === 'failed' && Date.now() - state.lastAttempt < RETRY_AFTER_FAILURE_MS) {
    throw new Error(state.message);
  }
  state.status = 'connecting';
  state.lastAttempt = Date.now();
  state.pending = doLogin(cfg, session, app)
    .then(() => { state.status = 'ok'; state.message = ''; })
    .catch(err => {
      state.status = 'failed';
      state.message = err.cause ? `Could not reach ${app.upstream} (${err.cause.code || err.message}).` : err.message;
      throw new Error(state.message);
    })
    .finally(() => { state.pending = null; });
  return state.pending;
}

module.exports = { ensureLogin };
