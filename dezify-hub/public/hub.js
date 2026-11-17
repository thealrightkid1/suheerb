(function () {
  'use strict';

  var ICONS = {
    home: '<path d="M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/>',
    logout: '<path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3M10 17l5-5-5-5M15 12H4"/>',
    menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
    back: '<path d="M15 18l-6-6 6-6"/>',
    reload: '<path d="M20 12a8 8 0 1 1-2.34-5.66M20 4v5h-5"/>',
    external: '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
    crm: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0M16 4.5a3.5 3.5 0 0 1 0 7M18 14.5a6.5 6.5 0 0 1 3.5 5.5"/>',
    attendance: '<rect x="5" y="4" width="14" height="17" rx="2"/><path d="M9 4V3h6v1M9 13l2 2 4-4"/>',
    registration: '<path d="M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8zM14 3v5h5M9 13h6M9 17h4"/>',
    payroll: '<rect x="3" y="6" width="18" height="13" rx="2"/><path d="M3 10h18M7 15h3"/>',
    app: '<rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/>'
  };
  function icon(name) {
    return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">' + (ICONS[name] || ICONS.app) + '</svg>';
  }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
  function $(id) { return document.getElementById(id); }
  function paintIcons(root) {
    (root || document).querySelectorAll('[data-icon]').forEach(function (el) { el.innerHTML = icon(el.getAttribute('data-icon')); });
  }

  var state = { apps: [], frames: {}, paths: {}, titles: {}, current: 'home' };
  var STATUS_TEXT = { ok: 'Connected', connecting: 'Connecting…', failed: 'Sign-in failed', idle: 'Not connected yet' };

  function appById(id) { return state.apps.find(function (a) { return a.id === id; }); }
  function originOf(url) { try { return new URL(url).origin; } catch (e) { return ''; } }

  function load() {
    return fetch('/api/me', { credentials: 'same-origin' }).then(function (r) {
      if (r.status === 401) { location.href = '/login'; throw new Error('signed out'); }
      return r.json();
    }).then(function (data) {
      state.apps = data.apps;
      document.title = data.brandName;
      $('brandName').textContent = data.brandName;
      $('logo').textContent = initials(data.brandName);
      $('userName').textContent = data.user.displayName;
      $('avatar').textContent = initials(data.user.displayName);
      $('greeting').textContent = 'Welcome, ' + data.user.displayName.split(' ')[0];
      render();
      if (state.apps.some(function (a) { return a.status === 'connecting' || a.status === 'idle' && a.enabled; })) {
        setTimeout(load, 1500);
      }
    });
  }
  function initials(name) { return name.split(/\s+/).filter(Boolean).slice(0, 2).map(function (w) { return w[0].toUpperCase(); }).join('') || 'H'; }

  function render() {
    $('appNav').innerHTML = state.apps.map(function (a) {
      if (!a.enabled) {
        return '<div class="nav-item disabled" title="Not connected yet"><span class="ico">' + icon(a.icon) + '</span><span class="grow">' + esc(a.name) + '</span><span class="tag">soon</span></div>';
      }
      return '<a class="nav-item" href="#/' + a.id + '" data-app="' + a.id + '"><span class="ico">' + icon(a.icon) +
        '</span><span class="grow">' + esc(a.name) + '</span><span class="dot ' + a.status + '" title="' + STATUS_TEXT[a.status] + '"></span></a>';
    }).join('');

    $('cards').innerHTML = state.apps.map(function (a) {
      if (!a.enabled) {
        return '<div class="card disabled"><div class="card-head"><div class="card-icon"><span class="ico">' + icon(a.icon) + '</span></div><div><h2>' + esc(a.name) +
          '</h2><div class="status"><span class="dot"></span>Not connected yet</div></div></div><p>' + esc(a.description) + '</p></div>';
      }
      var shortcuts = (a.shortcuts || []).map(function (s) {
        return '<a class="chip" href="#/' + a.id + s.path + '">' + esc(s.label) + '</a>';
      }).join('');
      return '<div class="card"><div class="card-head"><div class="card-icon"><span class="ico">' + icon(a.icon) + '</span></div><div><h2>' + esc(a.name) +
        '</h2><div class="status ' + a.status + '"><span class="dot ' + a.status + '"></span>' + STATUS_TEXT[a.status] + '</div></div></div>' +
        '<p>' + esc(a.description) + '</p>' +
        (a.status === 'failed' ? '<div class="err">' + esc(a.message) + '</div>' : '') +
        (shortcuts ? '<div class="shortcuts">' + shortcuts + '</div>' : '') +
        '<div class="card-actions"><a class="btn" href="#/' + a.id + '">Open ' + esc(a.name) + '</a>' +
        (a.status === 'failed' ? '<button class="btn secondary" data-reconnect="' + a.id + '">Try again</button>' : '') + '</div></div>';
    }).join('');
    highlight();
  }

  function highlight() {
    document.querySelectorAll('.nav-item[data-app]').forEach(function (el) {
      el.classList.toggle('active', el.getAttribute('data-app') === state.current);
    });
  }

  function ensureFrame(app, path) {
    var f = state.frames[app.id];
    if (!f) {
      f = document.createElement('iframe');
      f.title = app.name;
      f.setAttribute('allow', 'clipboard-write');
      f.src = app.url + (path || app.startPath);
      state.paths[app.id] = path || app.startPath;
      $('frames').appendChild(f);
      state.frames[app.id] = f;
    } else if (path && path !== state.paths[app.id]) {
      f.src = app.url + path;
      state.paths[app.id] = path;
    }
    return f;
  }

  function route() {
    var hash = location.hash.replace(/^#/, '') || '/';
    var m = hash.match(/^\/([\w-]+)(\/.*)?$/);
    var app = m && appById(m[1]);
    document.body.classList.remove('menu-open');

    if (!app || !app.enabled) {
      state.current = 'home';
      $('homeView').hidden = false;
      $('frames').hidden = true;
      $('frameActions').hidden = true;
      $('crumbApp').textContent = 'Home';
      $('crumbPage').textContent = '';
      highlight();
      return;
    }
    state.current = app.id;
    var frame = ensureFrame(app, m[2]);
    Object.keys(state.frames).forEach(function (id) { state.frames[id].hidden = id !== app.id; });
    $('homeView').hidden = true;
    $('frames').hidden = false;
    $('frameActions').hidden = false;
    $('crumbApp').textContent = app.name;
    $('crumbPage').textContent = state.titles[app.id] || '';
    $('newTabBtn').href = app.url + (state.paths[app.id] || app.startPath);
    frame.focus();
    highlight();
  }

  // Messages from the small bridge script injected into every app page.
  window.addEventListener('message', function (e) {
    var d = e.data;
    if (!d || d.type !== 'hub:location') return;
    var app = appById(d.app);
    if (!app || originOf(app.url) !== e.origin) return;
    state.paths[app.id] = d.path;
    state.titles[app.id] = (d.title || '').split(/ [-|·] /)[0];
    if (state.current === app.id) {
      history.replaceState(null, '', '#/' + app.id + d.path);
      $('crumbPage').textContent = state.titles[app.id];
      $('newTabBtn').href = app.url + d.path;
    }
  });

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-reconnect]');
    if (!btn) return;
    var id = btn.getAttribute('data-reconnect');
    btn.disabled = true; btn.textContent = 'Trying…';
    fetch('/api/apps/' + id + '/reconnect', { method: 'POST', credentials: 'same-origin' }).then(load);
  });

  $('reloadBtn').addEventListener('click', function () {
    var app = appById(state.current);
    if (app) state.frames[app.id].src = app.url + (state.paths[app.id] || app.startPath);
  });
  $('backBtn').addEventListener('click', function () {
    var app = appById(state.current);
    if (app) state.frames[app.id].contentWindow.postMessage({ type: 'hub:back' }, originOf(app.url));
  });
  $('menuBtn').addEventListener('click', function () { document.body.classList.toggle('menu-open'); });
  $('scrim').addEventListener('click', function () { document.body.classList.remove('menu-open'); });
  window.addEventListener('hashchange', route);

  paintIcons();
  load().then(route);
})();
