'use strict';
const crypto = require('crypto');
const { CookieJar } = require('./jar');

const COOKIE_NAME = 'hub_sid';

function hashPassword(password) {
  const salt = crypto.randomBytes(16).toString('hex');
  const hash = crypto.scryptSync(password, salt, 64).toString('hex');
  return `scrypt$${salt}$${hash}`;
}

function verifyPassword(user, password) {
  if (typeof password !== 'string') return false;
  if (user.passwordHash) {
    const [, salt, hash] = user.passwordHash.split('$');
    if (!salt || !hash) return false;
    const test = crypto.scryptSync(password, salt, 64);
    return crypto.timingSafeEqual(test, Buffer.from(hash, 'hex'));
  }
  if (typeof user.password !== 'string') return false;
  const a = crypto.createHash('sha256').update(password).digest();
  const b = crypto.createHash('sha256').update(user.password).digest();
  return crypto.timingSafeEqual(a, b);
}

function parseCookies(header = '') {
  const out = {};
  for (const pair of header.split(';')) {
    const eq = pair.indexOf('=');
    if (eq > 0) out[pair.slice(0, eq).trim()] = decodeURIComponent(pair.slice(eq + 1).trim());
  }
  return out;
}

class SessionStore {
  constructor(cfg) {
    this.cfg = cfg;
    this.sessions = new Map();
    this.failures = new Map(); // ip -> { count, resetAt }
    setInterval(() => this.sweep(), 10 * 60 * 1000).unref();
  }

  create(user) {
    const sid = crypto.randomBytes(32).toString('base64url');
    const session = {
      sid,
      user,
      expiresAt: Date.now() + this.cfg.sessionHours * 3600 * 1000,
      apps: {}, // appId -> { jar, status, message, lastAttempt, pending }
    };
    for (const app of this.cfg.apps) {
      session.apps[app.id] = { jar: new CookieJar(), status: 'idle', message: '', lastAttempt: 0, pending: null };
    }
    this.sessions.set(sid, session);
    return session;
  }

  get(req) {
    const sid = parseCookies(req.headers.cookie)[COOKIE_NAME];
    if (!sid) return null;
    const s = this.sessions.get(sid);
    if (!s) return null;
    if (s.expiresAt < Date.now()) { this.sessions.delete(sid); return null; }
    return s;
  }

  destroy(session) { if (session) this.sessions.delete(session.sid); }

  cookie(sid, maxAgeSeconds) {
    const parts = [`${COOKIE_NAME}=${sid}`, 'Path=/', 'HttpOnly', 'SameSite=Lax', `Max-Age=${maxAgeSeconds}`];
    if (this.cfg.cookieDomain) parts.push(`Domain=${this.cfg.cookieDomain}`);
    if (this.cfg.secureCookies) parts.push('Secure');
    return parts.join('; ');
  }

  // Simple brute-force protection for the hub login.
  isLocked(ip) {
    const f = this.failures.get(ip);
    return !!f && f.count >= 10 && f.resetAt > Date.now();
  }
  recordFailure(ip) {
    const f = this.failures.get(ip);
    if (!f || f.resetAt < Date.now()) this.failures.set(ip, { count: 1, resetAt: Date.now() + 15 * 60 * 1000 });
    else f.count++;
  }
  clearFailures(ip) { this.failures.delete(ip); }

  sweep() {
    const now = Date.now();
    for (const [sid, s] of this.sessions) if (s.expiresAt < now) this.sessions.delete(sid);
    for (const [ip, f] of this.failures) if (f.resetAt < now) this.failures.delete(ip);
  }
}

module.exports = { SessionStore, verifyPassword, hashPassword, parseCookies, COOKIE_NAME };
