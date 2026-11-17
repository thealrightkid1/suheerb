// Message composer: one person or a group, email or SMS, templates, live preview and SMS segment counter.
(function () {
  'use strict';
  const form = document.getElementById('composeForm');
  if (!form) return;
  const data = JSON.parse(document.getElementById('composeData').textContent);
  const $ = sel => form.querySelector(sel);
  const $$ = sel => Array.from(form.querySelectorAll(sel));

  const modeInput = $('#mode');
  const subject = $('#subject');
  const body = $('#body');
  const template = $('#template');
  const sendBtn = $('#sendBtn');
  const sendHint = $('#sendHint');
  const audienceBox = $('#audiencePreview');
  let personValues = null;  // merge values for the selected person
  let person = data.person; // details of the selected person
  let audience = null;      // last audience preview
  let lastField = body;

  const channel = () => form.querySelector('input[name="Channel"]:checked').value; // "Email" | "SMS"
  const mode = () => modeInput.value; // "one" | "group"
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const render = (text, values) => String(text || '').replace(/\{\{\s*([A-Za-z0-9_]+)\s*\}\}/g, (_, k) => {
    const key = Object.keys(values).find(x => x.toLowerCase() === k.toLowerCase());
    return key && values[key] != null ? values[key] : '';
  });

  // ----- SMS length (same rules as SmsLength in MessageText.cs) -----
  const GSM = '@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !"#¤%&\'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà';
  const GSM_EXT = '^{}\\[~]|€';
  function smsInfo(text) {
    const chars = Array.from(text);
    const gsm = chars.every(c => GSM.includes(c) || GSM_EXT.includes(c));
    if (!text) return { length: 0, segments: 0, perSegment: 160, gsm: true };
    if (gsm) {
      const units = chars.reduce((n, c) => n + (GSM_EXT.includes(c) ? 2 : 1), 0);
      return { length: units, segments: units <= 160 ? 1 : Math.ceil(units / 153), perSegment: units <= 160 ? 160 : 153, gsm };
    }
    const units = text.length;
    return { length: units, segments: units <= 70 ? 1 : Math.ceil(units / 67), perSegment: units <= 70 ? 70 : 67, gsm };
  }

  function updateCounter() {
    const el = $('#smsCounter');
    if (channel() !== 'SMS') { el.textContent = ''; return; }
    // Count the longest likely text: with the selected person's details, or the sample ones.
    const info = smsInfo(render(body.value, personValues || data.sample));
    const segs = info.segments;
    el.className = 'sms-counter small sms-only ' + (segs > 3 ? 'text-danger' : segs > 1 ? 'text-warning-emphasis' : 'text-body-secondary');
    el.innerHTML = `<i class="bi bi-chat-square-text me-1"></i>${info.length} characters · ${segs} ${segs === 1 ? 'message' : 'messages'}` +
      (info.gsm ? '' : ' <span title="Emoji and some symbols make each text hold only 70 characters.">(contains special characters)</span>') +
      (segs > 1 ? ` <span class="d-block d-sm-inline">Long texts are split into ${segs} parts and billed as ${segs}.</span>` : '');
  }

  // ----- Preview -----
  function updatePreview() {
    const values = mode() === 'one' && personValues ? personValues : data.sample;
    $('#previewNote').textContent = mode() === 'one' && personValues ? `Exactly what ${person?.text ?? 'they'} will receive.` : 'With sample details. Each person gets their own name, program and balance.';
    $('#previewSubject').textContent = render(subject.value, values);
    $('#previewBody').textContent = render(body.value, values) || '…';
    updateCounter();
  }

  // ----- Mode and channel -----
  function setMode(m) {
    modeInput.value = m;
    $$('[data-mode]').forEach(b => b.classList.toggle('active', b.dataset.mode === m));
    $('#onePane').classList.toggle('d-none', m !== 'one');
    $('#groupPane').classList.toggle('d-none', m !== 'group');
    if (m === 'group') refreshAudience();
    refreshUi();
  }

  function refreshUi() {
    const isEmail = channel() === 'Email';
    $$('.email-only').forEach(e => e.classList.toggle('d-none', !isEmail));
    $$('.sms-only').forEach(e => e.classList.toggle('d-none', isEmail));
    document.querySelectorAll('.preview-card .email-only').forEach(e => e.classList.toggle('d-none', !isEmail));
    Array.from(template.options).forEach(o => { if (o.value) o.hidden = o.dataset.channel !== channel(); });
    if (template.selectedOptions[0]?.hidden) template.value = '';
    const kindLeads = $('#kindLeads').checked;
    $$('.lead-only').forEach(e => e.classList.toggle('d-none', !kindLeads));

    const what = isEmail ? 'email' : 'text';
    let label = `Send ${what}`, hint = '', disabled = false;
    if (mode() === 'group') {
      if (!audience) { disabled = true; }
      else if (audience.reachable === 0) { disabled = true; hint = 'Nobody in this group can receive it.'; }
      else if (audience.overLimit) { disabled = true; hint = `Too many recipients (limit ${audience.limit.toLocaleString()}).`; }
      else label = `Send ${what} to ${audience.reachable.toLocaleString()} ${audience.reachable === 1 ? 'person' : 'people'}`;
    } else if (person) {
      const optedIn = isEmail ? person.emailOptIn : person.smsOptIn;
      const address = isEmail ? person.email : person.phone;
      if (!address) { disabled = true; hint = `${person.text} has no ${isEmail ? 'valid email address' : 'valid mobile number'} on file.`; }
      else if (!optedIn) { disabled = true; hint = `${person.text} has unsubscribed from ${isEmail ? 'email' : 'texts'}.`; }
      else label = `Send ${what} to ${person.text}`;
    }
    sendBtn.querySelector('span').textContent = label;
    sendBtn.disabled = disabled;
    sendHint.textContent = hint;
    updatePreview();
  }

  $$('[data-mode]').forEach(b => b.addEventListener('click', () => setMode(b.dataset.mode)));
  $$('input[name="Channel"]').forEach(r => r.addEventListener('change', () => { refreshUi(); if (mode() === 'group') refreshAudience(); }));

  // ----- Templates and placeholders -----
  template.addEventListener('change', () => {
    const t = data.templates.find(x => String(x.id) === template.value);
    if (!t) return;
    const hasText = body.value.trim() || subject.value.trim();
    if (hasText && !confirm('Replace what you have written with this template?')) { template.value = ''; return; }
    if (t.subject != null) subject.value = t.subject;
    body.value = t.body;
    updatePreview();
  });

  [subject, body].forEach(f => {
    f.addEventListener('focus', () => { lastField = f; });
    f.addEventListener('input', updatePreview);
  });
  $$('[data-insert]').forEach(chip => chip.addEventListener('click', () => {
    const field = lastField && !lastField.closest('.d-none') ? lastField : body;
    const text = chip.dataset.insert;
    const start = field.selectionStart ?? field.value.length, end = field.selectionEnd ?? field.value.length;
    field.setRangeText(text, start, end, 'end');
    field.focus();
    updatePreview();
  }));

  // ----- One person -----
  function showPerson(p) {
    person = p;
    const box = $('#personInfo');
    if (!p) { box.innerHTML = ''; personValues = null; refreshUi(); return; }
    const line = (icon, value, raw, optedIn, label) => {
      if (!value) return `<div class="text-body-secondary"><i class="bi bi-${icon} me-1"></i>${raw ? `${esc(raw)} <span class="text-danger">(not a valid ${label})</span>` : `No ${label} on file`}</div>`;
      return `<div><i class="bi bi-${icon} me-1"></i>${esc(value)} ${optedIn ? '' : '<span class="badge text-bg-warning ms-1">Unsubscribed</span>'}</div>`;
    };
    box.innerHTML = `<span class="badge text-bg-light border mb-1">${esc(p.kind)}</span>` +
      line('envelope', p.email, p.emailRaw, p.emailOptIn, 'email address') +
      line('phone', p.phone, p.phoneRaw, p.smsOptIn, 'mobile number');
    personValues = null;
    fetch(`${data.urls.merge}?recipient=${encodeURIComponent(p.value)}`, { headers: { Accept: 'application/json' } })
      .then(r => r.ok ? r.json() : null).then(v => { personValues = v; updatePreview(); });
    refreshUi();
  }

  const recipientSelect = $('#recipient');
  const ts = new TomSelect(recipientSelect, {
    valueField: 'value', labelField: 'text', searchField: ['text', 'email', 'phone', 'emailRaw', 'phoneRaw'],
    maxItems: 1, loadThrottle: 250, preload: false, create: false,
    options: data.person ? [data.person] : [],
    shouldLoad: q => q.length >= 2,
    load(query, callback) {
      fetch(`${data.urls.recipients}?q=${encodeURIComponent(query)}`, { headers: { Accept: 'application/json' } })
        .then(r => r.json()).then(callback).catch(() => callback());
    },
    render: {
      option: (o, e) => `<div><span class="fw-medium">${e(o.text)}</span> <span class="badge text-bg-light border ms-1">${e(o.kind)}</span>` +
        `<div class="small text-body-secondary">${e(o.emailRaw || 'no email')} · ${e(o.phoneRaw || 'no phone')}</div></div>`,
      no_results: () => '<div class="no-results">No students or leads match.</div>',
    },
  });
  ts.on('change', v => showPerson(v ? ts.options[v] : null));

  // ----- Group -----
  let audienceTimer = null, audienceRequest = 0;
  function refreshAudience() {
    clearTimeout(audienceTimer);
    audienceTimer = setTimeout(async () => {
      const id = ++audienceRequest;
      const params = new URLSearchParams();
      $$('#groupPane [name^="Audience."]').forEach(el => {
        if ((el.type === 'radio' && !el.checked) || el.closest('.d-none')) return;
        if (el.value) params.append(el.name, el.value);
      });
      params.append('channel', channel());
      audienceBox.className = 'audience-preview mt-3 text-body-secondary';
      audienceBox.textContent = 'Counting recipients…';
      try {
        const res = await fetch(`${data.urls.preview}?${params}`, { headers: { Accept: 'application/json' } });
        if (!res.ok) throw new Error();
        const p = await res.json();
        if (id !== audienceRequest) return;
        audience = p;
        const what = channel() === 'Email' ? 'email' : 'texts';
        const skippedParts = [];
        if (p.unsubscribed) skippedParts.push(`${p.unsubscribed.toLocaleString()} unsubscribed from ${what}`);
        if (p.noAddress) skippedParts.push(`${p.noAddress.toLocaleString()} with no ${channel() === 'Email' ? 'email address' : 'mobile number'}`);
        audienceBox.className = 'audience-preview mt-3 ' + (p.overLimit || p.reachable === 0 ? 'is-bad' : 'is-good');
        audienceBox.innerHTML =
          `<strong>${p.reachable.toLocaleString()} ${p.reachable === 1 ? 'recipient' : 'recipients'}</strong>` +
          (p.skipped ? `, ${p.skipped.toLocaleString()} will be skipped (${skippedParts.join(', ')})` : '') +
          `<span class="text-body-secondary"> · ${p.total.toLocaleString()} match the filters</span>` +
          (p.overLimit ? `<div class="mt-1">That's more than ${p.limit.toLocaleString()}, the most one send can go to. Narrow the filters and send in parts.</div>` : '');
      } catch {
        if (id !== audienceRequest) return;
        audience = null;
        audienceBox.className = 'audience-preview mt-3 is-bad';
        audienceBox.textContent = "Couldn't count recipients. Check your connection and try again.";
      }
      refreshUi();
    }, 200);
  }
  $$('#groupPane [name^="Audience."]').forEach(el => el.addEventListener('change', () => { refreshUi(); refreshAudience(); }));

  // ----- Submit -----
  form.addEventListener('submit', e => {
    if (channel() === 'Email' && !subject.value.trim()) { e.preventDefault(); subject.classList.add('is-invalid'); subject.focus(); return; }
    if (!body.value.trim()) { e.preventDefault(); body.classList.add('is-invalid'); body.focus(); return; }
    if (mode() === 'group' && audience) {
      const what = channel() === 'Email' ? 'email' : 'text message';
      if (!confirm(`Send this ${what} to ${audience.reachable.toLocaleString()} ${audience.reachable === 1 ? 'person' : 'people'}?`)) { e.preventDefault(); return; }
    }
    sendBtn.disabled = true;
    sendBtn.querySelector('span').textContent = 'Sending…';
  });
  [subject, body].forEach(f => f.addEventListener('input', () => f.classList.remove('is-invalid')));

  if (data.person) showPerson(data.person);
  setMode(mode());
})();
