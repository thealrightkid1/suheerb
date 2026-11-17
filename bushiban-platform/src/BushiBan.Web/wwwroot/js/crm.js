// CRM screens: lead/student editor helpers, timeline form, pipeline board, student profile and dashboard charts.
(function () {
  'use strict';

  const money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
  const antiforgery = () => (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || '';

  function ageOn(dob, today) {
    let age = today.getFullYear() - dob.getFullYear();
    const m = today.getMonth() - dob.getMonth();
    if (m < 0 || (m === 0 && today.getDate() < dob.getDate())) age--;
    return age;
  }

  // Same thresholds as the server (PeopleService.SuggestAgeGroup).
  const ageGroupFor = age => age < 6 ? '1' : age < 13 ? '2' : age < 18 ? '3' : '4';

  // ---------- Lead / student editor ----------
  document.querySelectorAll('[data-person-form]').forEach(form => {
    const isStudent = form.dataset.kind === 'student';
    const dob = form.querySelector('[data-dob]');
    const ageText = form.querySelector('[data-age-text]');
    const ageGroup = form.querySelector('[data-age-group]');
    const guardianLabel = form.querySelector('[data-guardian-label]');
    let autoGroup = ageGroup && !ageGroup.value; // only overwrite a value we chose ourselves

    if (ageGroup) ageGroup.addEventListener('change', () => { autoGroup = false; });

    function onDob() {
      if (!dob || !dob.value) { if (ageText) ageText.textContent = ''; if (guardianLabel) guardianLabel.classList.remove('required'); return; }
      const d = new Date(dob.value + 'T00:00:00');
      if (isNaN(d)) return;
      const age = ageOn(d, new Date());
      if (ageText) ageText.textContent = age >= 0 ? `${age} years old` : '';
      if (ageGroup && (autoGroup || !ageGroup.value) && age >= 0) { ageGroup.value = ageGroupFor(age); autoGroup = true; }
      if (guardianLabel && isStudent) guardianLabel.classList.toggle('required', age < 18);
    }
    if (dob) { dob.addEventListener('change', onDob); onDob(); }

    // Live "possible duplicate" hint while typing a phone or email (the server checks again on save).
    const result = form.querySelector('[data-dup-result]');
    const url = form.dataset.dupUrl;
    if (result && url) {
      const phone = form.querySelector('[data-dup-phone]');
      const email = form.querySelector('[data-dup-email]');
      let last = '';
      async function check() {
        const params = new URLSearchParams();
        if (phone && phone.value.replace(/\D/g, '').length >= 7) params.set('phone', phone.value);
        if (email && /.+@.+\..+/.test(email.value)) params.set('email', email.value);
        if (form.dataset.excludeLead) params.set('leadId', form.dataset.excludeLead);
        if (form.dataset.excludeStudent) params.set('studentId', form.dataset.excludeStudent);
        if (!params.has('phone') && !params.has('email')) { result.hidden = true; return; }
        const key = params.toString();
        if (key === last) return;
        last = key;
        try {
          const res = await fetch(`${url}?${key}`, { headers: { Accept: 'application/json' } });
          if (!res.ok) return;
          const matches = await res.json();
          if (!matches.length) { result.hidden = true; return; }
          result.innerHTML = '';
          const box = document.createElement('div');
          box.className = 'alert alert-warning py-2 small mb-0';
          box.append('Possible match: ');
          matches.slice(0, 4).forEach((m, i) => {
            if (i) box.append(', ');
            const a = document.createElement('a');
            a.href = m.url; a.target = '_blank'; a.rel = 'noopener'; a.className = 'alert-link';
            a.textContent = `${m.name} (${m.kind.toLowerCase()})`;
            box.append(a);
          });
          box.append('. You can still save if this is a different person.');
          result.append(box);
          result.hidden = false;
        } catch { /* the check is only a hint */ }
      }
      [phone, email].forEach(el => el && el.addEventListener('blur', check));
    }

    // Read-only fee panel on the student form: standard fees of the chosen program, minus the discount.
    const panel = form.querySelector('[data-fee-panel]');
    const feesJson = document.getElementById('programFees');
    if (panel && feesJson) {
      const fees = JSON.parse(feesJson.textContent || '{}');
      const program = form.querySelector('[data-program]');
      const discount = form.querySelector('[data-discount]');
      const set = (k, v) => { const el = panel.querySelector(`[data-fee="${k}"]`); if (el) el.textContent = v; };
      function update() {
        const f = program && fees[program.value];
        const d = Math.max(0, parseFloat(discount && discount.value) || 0);
        set('name', f ? f.name : 'no fee structure');
        set('admission', f ? money.format(f.admission) : '–');
        set('security', f ? money.format(f.security) : '–');
        set('monthly', f ? money.format(f.monthly) : '–');
        set('discount', money.format(d));
        set('net', f ? money.format(Math.max(0, f.monthly - d)) : '–');
      }
      program && program.addEventListener('change', update);
      discount && discount.addEventListener('input', update);
      update();
    }
  });

  // ---------- Timeline: show the due date only for follow-up tasks ----------
  document.querySelectorAll('[data-activity-form]').forEach(form => {
    const due = form.querySelector('[data-due-field]');
    const sync = () => {
      const checked = form.querySelector('input[name="Type"]:checked');
      if (due) due.hidden = !checked || checked.value !== 'FollowUp';
    };
    form.addEventListener('change', sync);
    sync();
  });

  // ---------- Pipeline board ----------
  const board = document.querySelector('[data-board]');
  if (board) {
    const canEdit = board.dataset.canEdit === 'true';
    const toastEl = document.querySelector('[data-board-toast]');
    const toast = toastEl && window.bootstrap ? bootstrap.Toast.getOrCreateInstance(toastEl, { delay: 3000 }) : null;
    function notify(message, ok) {
      if (!toast) { if (!ok) alert(message); return; }
      toastEl.classList.toggle('text-bg-success', ok);
      toastEl.classList.toggle('text-bg-danger', !ok);
      toastEl.querySelector('.toast-body').textContent = message;
      toast.show();
    }
    function refreshCounts() {
      board.querySelectorAll('.board-col').forEach(col => {
        const cards = col.querySelectorAll('.board-card').length;
        const empty = col.querySelector('.board-empty');
        if (empty) empty.hidden = cards > 0;
      });
    }
    function bump(col, delta) {
      const badge = col && col.querySelector('[data-count]');
      if (badge) badge.textContent = Math.max(0, (parseInt(badge.textContent, 10) || 0) + delta);
    }

    async function move(card, targetCol) {
      const fromCol = card.closest('.board-col');
      if (!targetCol || fromCol === targetCol) return;
      const body = targetCol.querySelector('[data-dropzone]');
      const placeholder = document.createComment('card');
      card.replaceWith(placeholder);
      body.prepend(card);
      bump(fromCol, -1); bump(targetCol, 1); refreshCounts();
      card.classList.add('saving');
      try {
        const form = new FormData();
        form.set('id', card.dataset.id);
        form.set('phase', targetCol.dataset.phase);
        const res = await fetch(board.dataset.moveUrl, {
          method: 'POST', body: form,
          headers: { RequestVerificationToken: antiforgery(), Accept: 'application/json' },
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(data.error || 'The change could not be saved.');
        notify(`Moved to "${data.label}".`, true);
        // Rebuild the card's "Move to" menu for its new column.
        card.querySelectorAll('[data-move-to]').forEach(b => { b.closest('li').hidden = b.dataset.moveTo === targetCol.dataset.phase; });
      } catch (err) {
        placeholder.replaceWith(card);
        bump(fromCol, 1); bump(targetCol, -1); refreshCounts();
        notify(err.message || 'The change could not be saved.', false);
      } finally {
        placeholder.remove();
        card.classList.remove('saving');
      }
    }

    if (canEdit) {
      let dragged = null;
      board.addEventListener('dragstart', e => {
        const card = e.target.closest && e.target.closest('.board-card');
        if (!card) return;
        dragged = card;
        card.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', card.dataset.id);
      });
      board.addEventListener('dragend', () => {
        if (dragged) dragged.classList.remove('dragging');
        dragged = null;
        board.querySelectorAll('.drop-target').forEach(c => c.classList.remove('drop-target'));
      });
      board.addEventListener('dragover', e => {
        const col = e.target.closest && e.target.closest('.board-col');
        if (!col || !dragged) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        board.querySelectorAll('.drop-target').forEach(c => c !== col && c.classList.remove('drop-target'));
        col.classList.add('drop-target');
      });
      board.addEventListener('dragleave', e => {
        const col = e.target.closest && e.target.closest('.board-col');
        if (col && !col.contains(e.relatedTarget)) col.classList.remove('drop-target');
      });
      board.addEventListener('drop', e => {
        const col = e.target.closest && e.target.closest('.board-col');
        if (!col || !dragged) return;
        e.preventDefault();
        col.classList.remove('drop-target');
        move(dragged, col);
      });
      // Touch screens and keyboards: the "Move to" menu on each card.
      board.addEventListener('click', e => {
        const btn = e.target.closest && e.target.closest('[data-move-to]');
        if (!btn) return;
        const card = btn.closest('.board-card');
        move(card, board.querySelector(`.board-col[data-phase="${btn.dataset.moveTo}"]`));
      });
    }
  }

  // ---------- Student profile ----------
  document.querySelectorAll('[data-reveal-pin]').forEach(btn => btn.addEventListener('click', () => {
    const pin = btn.previousElementSibling;
    const shown = pin.textContent === pin.dataset.pin;
    pin.textContent = shown ? '••••' : pin.dataset.pin;
    btn.textContent = shown ? 'Show' : 'Hide';
  }));

  // Keep the open tab in the address bar so reloads and redirects come back to it.
  const tabs = document.querySelectorAll('.crm-tabs [data-bs-toggle="tab"]');
  if (tabs.length && window.bootstrap) {
    const fromHash = location.hash && document.querySelector(`.crm-tabs [data-bs-target="${CSS.escape(location.hash)}"]`);
    if (fromHash) bootstrap.Tab.getOrCreateInstance(fromHash).show();
    tabs.forEach(t => t.addEventListener('shown.bs.tab', () => history.replaceState(null, '', t.dataset.bsTarget)));
  }

  // ---------- Dashboard charts ----------
  if (window.Chart) {
    const css = getComputedStyle(document.documentElement);
    const text = css.getPropertyValue('--bs-secondary-color').trim() || '#6c757d';
    const grid = css.getPropertyValue('--bs-border-color-translucent').trim() || 'rgba(0,0,0,.1)';
    const red = css.getPropertyValue('--bb-red').trim() || '#c8102e';
    Chart.defaults.color = text;
    Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;

    const read = id => { const el = document.getElementById(id); return el ? JSON.parse(el.textContent) : null; };
    const checkins = read('checkinsData');
    if (checkins) {
      new Chart(document.getElementById('checkinsChart'), {
        type: 'bar',
        data: { labels: checkins.labels, datasets: [{ label: 'Check-ins', data: checkins.values, backgroundColor: red, borderRadius: 4, maxBarThickness: 22 }] },
        options: {
          maintainAspectRatio: false, plugins: { legend: { display: false } },
          scales: { x: { grid: { display: false }, ticks: { maxRotation: 0, autoSkip: true, maxTicksLimit: 10 } }, y: { beginAtZero: true, ticks: { precision: 0 }, grid: { color: grid } } },
        },
      });
    }
    const leads = read('leadsData');
    if (leads) {
      const colors = ['#94a3b8', '#0dcaf0', '#3b82f6', '#6366f1', '#f59e0b', '#ef4444', '#64748b'];
      new Chart(document.getElementById('leadsChart'), {
        type: 'bar',
        // Long phase names ("Presented Program - Waiting for Commitment") go on two lines.
        data: { labels: leads.labels.map(l => l.includes(' - ') ? l.split(' - ') : l), datasets: [{ label: 'Leads', data: leads.values, backgroundColor: colors, borderRadius: 4, maxBarThickness: 26 }] },
        options: {
          indexAxis: 'y', maintainAspectRatio: false, plugins: { legend: { display: false } },
          scales: { x: { beginAtZero: true, ticks: { precision: 0 }, grid: { color: grid } }, y: { grid: { display: false }, ticks: { autoSkip: false } } },
        },
      });
    }
  }
})();
