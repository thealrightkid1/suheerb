// Kiosk behaviours: clock, attendance check-in (PIN pad + name search), registration forms
// (signature pads, required checks, idle reset) and auto-return screens. No dependencies besides
// Signature Pad on form pages and Tom Select (via site.js) for the city picker.
(function () {
  'use strict';

  const token = document.querySelector('meta[name="csrf-token"]')?.content || '';

  // ------------------------------------------------------------------ Clock (branch time zone)
  const clock = document.getElementById('kioskClock');
  if (clock) {
    const tz = clock.dataset.tz;
    const fmt = (opts) => { try { return new Intl.DateTimeFormat('en-US', { ...opts, timeZone: tz }); } catch { return new Intl.DateTimeFormat('en-US', opts); } };
    const timeFmt = fmt({ hour: 'numeric', minute: '2-digit' });
    const dateFmt = fmt({ weekday: 'long', month: 'short', day: 'numeric' });
    const tick = () => {
      const now = new Date();
      clock.querySelector('.time').textContent = timeFmt.format(now);
      clock.querySelector('.date').textContent = dateFmt.format(now);
    };
    tick();
    setInterval(tick, 10000);
  }

  // ------------------------------------------------------------------ Auto-return screens (thank you, unavailable)
  const autoReturn = document.querySelector('[data-auto-return]');
  if (autoReturn) {
    const seconds = parseInt(autoReturn.dataset.seconds || '8', 10);
    const bar = autoReturn.querySelector('.kiosk-countdown span');
    if (bar) { bar.style.transitionDuration = seconds + 's'; requestAnimationFrame(() => requestAnimationFrame(() => bar.style.width = '0%')); }
    setTimeout(() => location.replace(autoReturn.dataset.autoReturn), seconds * 1000);
  }

  // ------------------------------------------------------------------ Attendance check-in
  const checkin = document.getElementById('checkin');
  if (checkin) initCheckIn(checkin);

  function initCheckIn(root) {
    const checkInUrl = root.dataset.checkinUrl;
    const searchUrl = root.dataset.searchUrl;
    const dots = [...root.querySelectorAll('.pin-dot')];
    const pinMsg = document.getElementById('pinMsg');
    const pinPanel = root.querySelector('[data-panel="pin"]');
    const searchPanel = root.querySelector('[data-panel="search"]');
    const searchInput = document.getElementById('nameSearch');
    const searchMsg = document.getElementById('searchMsg');
    const results = document.getElementById('searchResults');
    const overlay = document.getElementById('ckOverlay');
    const card = overlay.querySelector('.ck-card');
    const defaultPinMsg = pinMsg.textContent;
    const defaultSearchMsg = searchMsg.textContent;

    let pin = '';
    let busy = false;
    let autoTimer = null;
    let lockTimer = null;
    let overlayTimer = null;
    let idleTimer = null;
    let pendingStudent = null;

    // Tabs
    root.querySelectorAll('.ck-tab').forEach(tab => tab.addEventListener('click', () => showTab(tab.dataset.tab)));
    function showTab(name) {
      root.querySelectorAll('.ck-tab').forEach(t => { const on = t.dataset.tab === name; t.classList.toggle('active', on); t.setAttribute('aria-selected', on); });
      pinPanel.hidden = name !== 'pin';
      searchPanel.hidden = name !== 'search';
      if (name === 'search') { searchInput.focus(); bumpIdle(); }
    }

    // PIN pad
    function renderPin() {
      dots.forEach((d, i) => d.classList.toggle('filled', i < pin.length));
    }
    function setPinMsg(text, kind) {
      pinMsg.textContent = text;
      pinMsg.className = 'ck-msg' + (kind ? ' ' + kind : '');
    }
    function press(digit) {
      if (busy || lockTimer) return;
      if (pin.length >= 6) return;
      pin += digit;
      renderPin();
      setPinMsg(defaultPinMsg);
      clearTimeout(autoTimer);
      if (pin.length === 6) submitPin();
      else if (pin.length >= 4) autoTimer = setTimeout(submitPin, 1400);
    }
    function back() {
      if (busy) return;
      clearTimeout(autoTimer);
      pin = pin.slice(0, -1);
      renderPin();
    }
    function clearPin() { clearTimeout(autoTimer); pin = ''; renderPin(); }
    function submitPin() {
      clearTimeout(autoTimer);
      if (busy || lockTimer) return;
      if (pin.length < 4) { setPinMsg('PINs have at least 4 digits.', 'warn'); return; }
      const value = pin;
      post({ pin: value });
    }
    pinPanel.querySelectorAll('.pin-key').forEach(k => k.addEventListener('click', () => {
      if (k.dataset.digit) press(k.dataset.digit);
      else if (k.dataset.action === 'back') back();
      else if (k.dataset.action === 'go') submitPin();
    }));
    document.addEventListener('keydown', e => {
      if (!overlay.hidden) { if (e.key === 'Enter' || e.key === 'Escape') closeOverlay(); return; }
      if (pinPanel.hidden) return;
      if (/^[0-9]$/.test(e.key)) press(e.key);
      else if (e.key === 'Backspace') back();
      else if (e.key === 'Enter') submitPin();
    });

    function lockPad(seconds) {
      clearPin();
      pinPanel.classList.add('locked');
      let left = seconds;
      const update = () => setPinMsg(`Too many wrong PINs. Please wait ${left} second${left === 1 ? '' : 's'}, or tap "Find my name".`, 'error');
      update();
      lockTimer = setInterval(() => {
        left -= 1;
        if (left <= 0) { clearInterval(lockTimer); lockTimer = null; pinPanel.classList.remove('locked'); setPinMsg(defaultPinMsg); }
        else update();
      }, 1000);
    }

    // Name search
    let searchTimer = null;
    let searchSeq = 0;
    searchInput.addEventListener('input', () => {
      bumpIdle();
      clearTimeout(searchTimer);
      searchTimer = setTimeout(runSearch, 250);
    });
    async function runSearch() {
      const q = searchInput.value.trim();
      results.replaceChildren();
      if (q.length < 2) { searchMsg.textContent = defaultSearchMsg; return; }
      const seq = ++searchSeq;
      searchMsg.textContent = 'Searching…';
      try {
        const res = await fetch(`${searchUrl}?q=${encodeURIComponent(q)}`, { headers: { Accept: 'application/json' } });
        const list = await readJson(res);
        if (seq !== searchSeq) return;
        if (!list.length) { searchMsg.textContent = 'No one found with that name. Check the spelling, or ask the front desk.'; return; }
        searchMsg.textContent = 'Tap your name to check in.';
        list.forEach(p => {
          const b = document.createElement('button');
          b.type = 'button';
          b.className = 'ck-person';
          const name = document.createElement('span');
          name.className = 'ck-person-name';
          name.textContent = p.name;
          b.append(name);
          if (p.belt) b.append(beltBadge(p.belt, p.color));
          b.addEventListener('click', () => confirmStudent(p));
          results.append(b);
        });
      } catch (err) {
        if (seq === searchSeq) searchMsg.textContent = err.message;
      }
    }
    function confirmStudent(p) {
      pendingStudent = p;
      showOverlay('confirm', { headline: `Are you ${p.name}?`, belt: p.belt, beltColor: p.color, detail: 'Tap "Yes" to check in for class.' });
    }
    overlay.querySelector('.ck-yes').addEventListener('click', e => {
      e.stopPropagation();
      if (!pendingStudent) return;
      const id = pendingStudent.id;
      pendingStudent = null;
      post({ studentId: id });
    });
    overlay.querySelector('.ck-cancel').addEventListener('click', e => { e.stopPropagation(); pendingStudent = null; closeOverlay(false); });

    // Server call
    async function post(fields) {
      busy = true;
      root.classList.add('busy');
      try {
        const res = await fetch(checkInUrl, {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': token, Accept: 'application/json' },
          body: new URLSearchParams(fields),
        });
        handle(await readJson(res));
      } catch (err) {
        clearPin();
        showOverlay('error', { headline: 'Something went wrong', detail: err.message });
      } finally {
        busy = false;
        root.classList.remove('busy');
      }
    }
    async function readJson(res) {
      const type = res.headers.get('content-type') || '';
      if (res.redirected || !type.includes('json')) throw new Error('This kiosk has been signed out. Please ask the front desk to sign it back in.');
      if (!res.ok) throw new Error('Something went wrong. Please try again or see the front desk.');
      return res.json();
    }
    function handle(r) {
      switch (r.kind) {
        case 'checked-in':
          showOverlay('success', { headline: `Welcome, ${r.firstName}!`, belt: r.belt, beltColor: r.beltColor,
            detail: `You're checked in. Classes this month: ${r.classesThisMonth}`, auto: true });
          break;
        case 'duplicate':
          showOverlay('info', { headline: r.message, belt: r.belt, beltColor: r.beltColor,
            detail: `Classes this month: ${r.classesThisMonth}. Have a great class!`, auto: true });
          break;
        case 'wrong-pin':
          clearPin();
          setPinMsg(r.message, 'error');
          pinPanel.classList.remove('shake'); void pinPanel.offsetWidth; pinPanel.classList.add('shake');
          break;
        case 'locked':
          lockPad(r.retryAfter || 60);
          break;
        default:
          clearPin();
          showOverlay('error', { headline: r.message || 'Please see the front desk.', auto: true });
      }
    }

    // Result overlay
    function beltBadge(name, color) {
      const s = document.createElement('span');
      s.className = 'belt';
      s.style.setProperty('--belt', color || '#ccc');
      s.textContent = name + ' belt';
      return s;
    }
    function showOverlay(kind, o) {
      clearTimeout(overlayTimer);
      card.className = 'ck-card ck-' + kind;
      const icons = { success: 'bi-check-circle-fill', info: 'bi-hand-thumbs-up-fill', error: 'bi-exclamation-circle-fill', confirm: 'bi-person-check-fill' };
      card.querySelector('.ck-icon i').className = 'bi ' + icons[kind];
      card.querySelector('h2').textContent = o.headline || '';
      const beltWrap = card.querySelector('.ck-belt');
      beltWrap.replaceChildren();
      if (o.belt) beltWrap.append(beltBadge(o.belt, o.beltColor));
      card.querySelector('.ck-detail').textContent = o.detail || '';
      card.querySelector('.ck-actions').hidden = kind !== 'confirm';
      card.querySelector('.ck-tap').hidden = kind === 'confirm';
      const timer = card.querySelector('.ck-timer');
      timer.hidden = !o.auto;
      overlay.hidden = false;
      if (o.auto) {
        const bar = timer.querySelector('span');
        bar.style.transition = 'none'; bar.style.width = '100%';
        requestAnimationFrame(() => requestAnimationFrame(() => { bar.style.transition = 'width 4s linear'; bar.style.width = '0%'; }));
        overlayTimer = setTimeout(() => closeOverlay(true), 4000);
      }
    }
    function closeOverlay(reset = true) {
      clearTimeout(overlayTimer);
      overlay.hidden = true;
      if (reset) resetAll();
    }
    overlay.addEventListener('click', () => { if (!card.classList.contains('ck-confirm')) closeOverlay(true); });

    function resetAll() {
      clearPin();
      if (!lockTimer) setPinMsg(defaultPinMsg);
      searchInput.value = '';
      results.replaceChildren();
      searchMsg.textContent = defaultSearchMsg;
      showTab('pin');
    }

    // The name list is cleared after 45 seconds without a tap, so the next person starts fresh.
    function bumpIdle() {
      clearTimeout(idleTimer);
      idleTimer = setTimeout(() => { if (overlay.hidden) resetAll(); }, 45000);
    }
    root.addEventListener('pointerdown', bumpIdle);
  }

  // ------------------------------------------------------------------ Registration forms (and form builder preview)
  document.querySelectorAll('[data-kiosk-form]').forEach(initForm);

  function initForm(root) {
    root.querySelectorAll('.kf-sig').forEach(initSignature);
    root.addEventListener('input', e => clearError(e.target.closest('.kf-field')));
    root.addEventListener('change', e => clearError(e.target.closest('.kf-field')));

    const form = root.closest('form[data-kiosk-submit]');
    if (!form) return;
    let dirty = false;
    let submitting = false;
    form.addEventListener('input', () => dirty = true);
    form.addEventListener('change', () => dirty = true);

    form.addEventListener('submit', e => {
      if (submitting) { e.preventDefault(); return; }
      let first = null;
      root.querySelectorAll('.kf-field[data-key]').forEach(field => {
        const msg = check(field);
        setError(field, msg);
        if (msg && !first) first = field;
      });
      const summary = form.querySelector('[data-error-summary]');
      if (first) {
        e.preventDefault();
        if (summary) summary.hidden = false;
        first.scrollIntoView({ behavior: 'smooth', block: 'center' });
        const focusable = first.querySelector('input:not([type=hidden]), select, textarea');
        if (focusable && !first.classList.contains('kf-signature')) setTimeout(() => focusable.focus({ preventScroll: true }), 350);
        return;
      }
      if (summary) summary.hidden = true;
      submitting = true;
      const btn = form.querySelector('.kf-submit-btn');
      if (btn) { btn.disabled = true; btn.innerHTML = '<span class="spinner-border spinner-border-sm me-2"></span>Sending…'; }
    });

    // Leaving with answers typed: ask first.
    document.querySelectorAll('[data-leave-form]').forEach(a => a.addEventListener('click', e => {
      if (dirty && !confirm('Leave this form? Your answers will be cleared.')) e.preventDefault();
    }));

    // Privacy: clear an abandoned form after 3 minutes without a touch (with a 30-second warning).
    const idleBox = document.getElementById('kioskIdle');
    const idleUrl = form.dataset.idleUrl;
    if (idleBox && idleUrl) {
      let idle = null, countdown = null;
      const arm = () => {
        clearTimeout(idle);
        if (!idleBox.hidden) return;
        idle = setTimeout(warn, 180000);
      };
      const warn = () => {
        let left = 30;
        idleBox.hidden = false;
        const count = idleBox.querySelector('[data-idle-count]');
        count.textContent = left;
        countdown = setInterval(() => {
          left -= 1;
          count.textContent = left;
          if (left <= 0) { clearInterval(countdown); submitting = true; location.replace(idleUrl); }
        }, 1000);
      };
      idleBox.querySelector('[data-idle-stay]').addEventListener('click', () => { clearInterval(countdown); idleBox.hidden = true; arm(); });
      ['pointerdown', 'keydown', 'input', 'scroll'].forEach(ev => document.addEventListener(ev, arm, { passive: true }));
      arm();
    }
  }

  function setError(field, msg) {
    field.classList.toggle('has-error', !!msg);
    const box = field.querySelector('.kf-error');
    if (box) box.textContent = msg || '';
  }
  function clearError(field) {
    if (field && field.classList.contains('has-error')) setError(field, '');
  }

  const emailPattern = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
  function check(field) {
    const type = field.dataset.type;
    const required = field.dataset.required === 'true';
    const input = field.querySelector('input:not(.btn-check):not([type=hidden]), select, textarea');
    switch (type) {
      case 'radio':
        return required && !field.querySelector('input:checked') ? 'Please choose one.' : '';
      case 'checkboxes':
        return required && !field.querySelector('input:checked') ? 'Please choose at least one.' : '';
      case 'checkbox':
        return required && !field.querySelector('input:checked') ? 'Please tick this box to continue.' : '';
      case 'signature':
        return required && !field.querySelector('.kf-sig-data').value ? 'Please sign here.' : '';
      case 'select':
      case 'city': {
        const sel = field.querySelector('select');
        return required && !sel.value ? 'Please choose one.' : '';
      }
      default: {
        const v = (input?.value || '').trim();
        if (!v) return required ? 'Please fill this in.' : '';
        if (type === 'email' && !emailPattern.test(v)) return 'Please enter a valid email address, like name@example.com.';
        if (type === 'tel' && (v.replace(/\D/g, '').length < 7 || /[^0-9 ()+\-.xX#]/.test(v))) return 'Please enter a phone number with at least 7 digits.';
        if (type === 'number' && !/^-?\d{1,3}(,?\d{3})*(\.\d+)?$|^-?\d+(\.\d+)?$/.test(v)) return 'Please enter a number.';
        return '';
      }
    }
  }

  // Signature Pad: full width, crisp on high-density screens, survives rotation, Clear button.
  function initSignature(wrap) {
    if (!window.SignaturePad) return;
    const canvas = wrap.querySelector('canvas');
    const input = wrap.querySelector('.kf-sig-data');
    const pad = new window.SignaturePad(canvas, { penColor: '#0f172a', minWidth: 0.9, maxWidth: 2.8, throttle: 8 });
    let lastWidth = 0;

    function redraw() {
      if (!input.value) return;
      const img = new Image();
      img.onload = () => {
        const w = canvas.offsetWidth, h = canvas.offsetHeight;
        const scale = Math.min(w / img.width, h / img.height, 1) || 1;
        pad.fromDataURL(input.value, { width: img.width * scale, height: img.height * scale });
      };
      img.src = input.value;
    }
    function resize() {
      const width = canvas.offsetWidth;
      if (!width || width === lastWidth) return;
      lastWidth = width;
      const ratio = Math.max(window.devicePixelRatio || 1, 1);
      canvas.width = width * ratio;
      canvas.height = canvas.offsetHeight * ratio;
      canvas.getContext('2d').scale(ratio, ratio);
      pad.clear();
      redraw();
    }
    pad.addEventListener('endStroke', () => {
      input.value = pad.toDataURL('image/png');
      wrap.closest('.kf-field')?.classList.remove('has-error');
      const box = wrap.closest('.kf-field')?.querySelector('.kf-error');
      if (box) box.textContent = '';
      input.dispatchEvent(new Event('input', { bubbles: true }));
    });
    wrap.querySelector('.kf-sig-clear').addEventListener('click', () => { pad.clear(); input.value = ''; });
    let t = null;
    window.addEventListener('resize', () => { clearTimeout(t); t = setTimeout(resize, 150); });
    resize();
  }
})();
