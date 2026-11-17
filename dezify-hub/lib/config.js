'use strict';
const fs = require('fs');
const path = require('path');

const CONFIG_PATH = process.env.HUB_CONFIG || path.join(__dirname, '..', 'config.json');

function loadConfig() {
  if (!fs.existsSync(CONFIG_PATH)) {
    console.error(`\nMissing ${CONFIG_PATH}.\nCopy config.example.json to config.json and fill in the credentials.\n`);
    process.exit(1);
  }
  const cfg = JSON.parse(fs.readFileSync(CONFIG_PATH, 'utf8'));
  cfg.host = cfg.host || '127.0.0.1';
  cfg.hubPort = cfg.hubPort || 3000;
  cfg.sessionHours = cfg.sessionHours || 12;
  cfg.brandName = cfg.brandName || 'Hub';
  cfg.apps = (cfg.apps || []).map(app => ({
    enabled: true,
    startPath: '/',
    shortcuts: [],
    ...app,
    upstream: app.upstream ? app.upstream.replace(/\/+$/, '') : '',
    login: {
      path: '/Identity/Account/Login',
      logoutPattern: '/Identity/Account/Logout',
      userField: 'Input.Email',
      passwordField: 'Input.Password',
      ...(app.login || {}),
    },
  }));
  for (const u of cfg.users || []) {
    if (u.password && !u.passwordHash) {
      console.warn(`[config] user "${u.username}" has a plain-text password. Use "npm run hash-password" before going live.`);
    }
  }
  return cfg;
}

module.exports = { loadConfig };
