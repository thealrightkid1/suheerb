// Form builder: kiosk form ordering (list page) and the field editor (edit page).
// The editor keeps the schema as an array in memory and posts it as JSON to SaveDraft.
(function () {
  'use strict';

  const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

  // Pointer-based drag and drop by a .drag-handle; works with mouse, pen and touch.
  function sortable(container, itemSelector, onDrop) {
    let dragging = null;
    let moved = false;
    container.addEventListener('pointerdown', e => {
      const handle = e.target.closest('.drag-handle');
      if (!handle || !container.contains(handle)) return;
      dragging = handle.closest(itemSelector);
      if (!dragging) return;
      moved = false;
      dragging.classList.add('dragging');
      handle.setPointerCapture(e.pointerId);
      e.preventDefault();
    });
    container.addEventListener('pointermove', e => {
      if (!dragging) return;
      const over = document.elementFromPoint(e.clientX, e.clientY)?.closest(itemSelector);
      if (!over || over === dragging || over.parentNode !== dragging.parentNode) return;
      const rect = over.getBoundingClientRect();
      const before = e.clientY < rect.top + rect.height / 2;
      over.parentNode.insertBefore(dragging, before ? over : over.nextSibling);
      moved = true;
    });
    const end = () => {
      if (!dragging) return;
      dragging.classList.remove('dragging');
      const item = dragging;
      dragging = null;
      if (moved) onDrop(item);
    };
    container.addEventListener('pointerup', end);
    container.addEventListener('pointercancel', end);
  }

  // ------------------------------------------------------------------ Form list: drag to reorder
  const formList = document.getElementById('formList');
  if (formList) {
    const status = document.getElementById('orderStatus');
    sortable(formList, 'li[data-id]', async () => {
      const ids = [...formList.querySelectorAll('li[data-id]')].map(li => li.dataset.id);
      status.textContent = 'Saving order…';
      try {
        const res = await fetch(formList.dataset.saveUrl, {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded', RequestVerificationToken: token },
          body: new URLSearchParams(ids.map(id => ['ids', id])),
        });
        const data = await res.json();
        status.textContent = data.message || 'Order saved.';
      } catch {
        status.textContent = "The new order couldn't be saved. Please reload and try again.";
      }
    });
  }

  // ------------------------------------------------------------------ Field editor
  const root = document.getElementById('formBuilder');
  if (!root) return;

  const cfg = JSON.parse(root.dataset.config);
  const typeLabel = Object.fromEntries(cfg.types.map(t => [t.value, t.label]));
  const mapLabel = Object.fromEntries(cfg.mapTargets.map(t => [t.value, t.label]));
  const ICONS = {
    heading: 'type-h1', notice: 'card-text', text: 'input-cursor-text', textarea: 'textarea-t', number: '123', date: 'calendar-date',
    email: 'envelope', tel: 'telephone', select: 'menu-button-wide', radio: 'ui-radios', checkboxes: 'ui-checks', checkbox: 'check2-square',
    signature: 'pen', city: 'geo-alt',
  };
  const structural = t => t === 'heading' || t === 'notice';
  const SHOW = {
    label: t => t !== 'notice',
    key: t => !structural(t),
    required: t => !structural(t),
    options: t => t === 'select' || t === 'radio' || t === 'checkboxes',
    html: t => t === 'notice',
    help: t => !structural(t),
    placeholder: t => ['text', 'textarea', 'number', 'email', 'tel', 'select', 'city'].includes(t),
    width: t => !structural(t),
    mapsTo: t => !structural(t) && t !== 'signature',
  };

  const blank = { key: '', type: 'text', label: '', required: false, options: null, help: null, placeholder: null, html: null, width: 'full', mapsTo: null };
  let fields = JSON.parse(document.getElementById('fbSchema').textContent || '[]').map(f => ({ ...blank, ...f, _auto: false }));
  let selected = fields.length ? 0 : -1;
  let dirty = false;
  let saving = false;
  let problems = new Map();

  const listEl = document.getElementById('fieldList');
  const panel = document.getElementById('fieldPanel');
  const editor = panel.querySelector('[data-editor]');
  const emptyMsg = panel.querySelector('[data-empty]');
  const saveBtn = document.getElementById('saveDraftBtn');
  const saveState = document.getElementById('saveState');
  const problemsBox = document.getElementById('fbProblems');
  const publishBtn = document.getElementById('publishBtn');
  const previewBtn = document.getElementById('previewBtn');
  const htmlPreview = document.getElementById('fbHtmlPreview');
  const input = name => editor.querySelector(`[data-prop="${name}"]`);

  // Type and "fills in" dropdowns, and the Add field menu.
  input('type').innerHTML = cfg.types.map(t => `<option value="${t.value}">${escapeHtml(t.label)}</option>`).join('');
  input('mapsTo').innerHTML = '<option value="">Nothing</option>' + cfg.mapTargets.map(t => `<option value="${t.value}">${escapeHtml(t.label)}</option>`).join('');
  const addMenu = document.getElementById('addFieldMenu');
  addMenu.innerHTML = cfg.types.map(t =>
    `<li><button class="dropdown-item" type="button" data-add="${t.value}"><i class="bi bi-${ICONS[t.value]} me-2"></i>${escapeHtml(t.label)}</button></li>`).join('');
  addMenu.addEventListener('click', e => { const b = e.target.closest('[data-add]'); if (b) addField(b.dataset.add); });

  // ---- keys
  function slug(label) {
    const words = (label || '').normalize('NFKD').replace(/[^\w\s]/g, ' ').replace(/_/g, ' ').trim().split(/\s+/).filter(Boolean).slice(0, 6);
    let key = words.map((w, i) => i === 0 ? w.toLowerCase() : w[0].toUpperCase() + w.slice(1).toLowerCase()).join('');
    if (!/^[A-Za-z]/.test(key)) key = 'field' + key;
    return key.slice(0, 60);
  }
  function uniqueKey(base, except) {
    let key = base || 'field';
    let n = 2;
    const taken = k => fields.some((f, i) => i !== except && f.key.toLowerCase() === k.toLowerCase());
    while (taken(key)) key = `${base}${n++}`;
    return key;
  }

  // ---- list
  function render() {
    document.getElementById('fieldCount').textContent = fields.length;
    if (!fields.length) {
      listEl.innerHTML = '<div class="text-center text-body-secondary py-5">No fields yet. Use <strong>Add field</strong> to start.</div>';
    } else {
      listEl.innerHTML = fields.map((f, i) => {
        const title = f.type === 'notice' ? textOf(f.html).slice(0, 90) || 'Notice' : f.label || '(no label)';
        const tags = [];
        if (problems.has(i)) tags.push('<span class="badge text-bg-danger">Fix</span>');
        if (f.required) tags.push('<span class="badge text-bg-light border">Required</span>');
        if (!structural(f.type) && f.width === 'half') tags.push('<span class="badge text-bg-light border">Half</span>');
        if (f.mapsTo) tags.push(`<span class="badge text-bg-light border" title="Fills in ${escapeHtml(mapLabel[f.mapsTo] || f.mapsTo)}"><i class="bi bi-arrow-right-short"></i>${escapeHtml(mapLabel[f.mapsTo] || f.mapsTo)}</span>`);
        return `<div class="fb-field ${i === selected ? 'selected' : ''} ${f.type === 'heading' ? 'is-heading' : ''}" data-index="${i}" role="listitem" tabindex="0">
            <span class="drag-handle" title="Drag to reorder"><i class="bi bi-grip-vertical"></i></span>
            <span class="fb-type" title="${escapeHtml(typeLabel[f.type] || f.type)}"><i class="bi bi-${ICONS[f.type] || 'question'}"></i></span>
            <span class="fb-label"><span class="t">${escapeHtml(title)}</span><span class="k">${structural(f.type) ? escapeHtml(typeLabel[f.type]) : escapeHtml(f.key) + ' · ' + escapeHtml(typeLabel[f.type] || f.type)}</span></span>
            <span class="fb-tags d-none d-md-flex">${tags.join('')}</span>
            <span class="fb-actions">
              <button type="button" class="btn btn-sm btn-link text-body-secondary px-1" data-act="dup" title="Duplicate"><i class="bi bi-copy"></i></button>
              <button type="button" class="btn btn-sm btn-link text-danger px-1" data-act="del" title="Delete"><i class="bi bi-trash"></i></button>
            </span>
          </div>`;
      }).join('');
    }
    renderPanel();
  }

  listEl.addEventListener('click', e => {
    const row = e.target.closest('.fb-field');
    if (!row) return;
    const i = +row.dataset.index;
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (act === 'dup') duplicate(i);
    else if (act === 'del') remove(i);
    else select(i);
  });
  listEl.addEventListener('keydown', e => {
    const row = e.target.closest('.fb-field');
    if (row && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); select(+row.dataset.index); }
  });
  sortable(listEl, '.fb-field', () => {
    const order = [...listEl.querySelectorAll('.fb-field')].map(r => +r.dataset.index);
    const current = fields[selected];
    fields = order.map(i => fields[i]);
    selected = current ? fields.indexOf(current) : -1;
    problems = new Map();
    changed();
  });

  function select(i) {
    selected = i;
    render();
    if (window.innerWidth < 1200) panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function addField(type) {
    const label = type === 'heading' ? 'New section' : type === 'notice' ? '' : type === 'signature' ? 'Signature' : type === 'city' ? 'City' : 'New question';
    const f = { ...blank, type, label, width: structural(type) || type === 'signature' || type === 'textarea' || type === 'checkboxes' ? 'full' : 'half', _auto: true };
    if (SHOW.options(type)) f.options = ['Option 1', 'Option 2'];
    if (type === 'notice') f.html = '<p>Type the notice here.</p>';
    if (type === 'city') f.mapsTo = 'CityId';
    f.key = structural(type) ? '' : uniqueKey(slug(label), -1);
    const at = selected >= 0 ? selected + 1 : fields.length;
    fields.splice(at, 0, f);
    selected = at;
    problems = new Map();
    changed();
    const focus = input(type === 'notice' ? 'html' : 'label');
    focus.focus();
    focus.select?.();
  }
  function duplicate(i) {
    const copy = JSON.parse(JSON.stringify(fields[i]));
    copy._auto = false;
    if (!structural(copy.type)) copy.key = uniqueKey(copy.key + 'Copy', -1);
    else copy.key = '';
    if (copy.label) copy.label += ' (copy)';
    fields.splice(i + 1, 0, copy);
    selected = i + 1;
    problems = new Map();
    changed();
  }
  function remove(i) {
    const f = fields[i];
    const name = f.type === 'notice' ? 'this notice' : `"${f.label || f.key}"`;
    if (!confirm(`Remove ${name} from the form?`)) return;
    fields.splice(i, 1);
    if (selected >= fields.length) selected = fields.length - 1;
    problems = new Map();
    changed();
  }

  // ---- property panel
  function renderPanel() {
    const f = fields[selected];
    editor.hidden = !f;
    emptyMsg.hidden = !!f;
    if (!f) return;
    editor.querySelectorAll('[data-show]').forEach(el => { el.hidden = !SHOW[el.dataset.show](f.type); });
    setValue('type', f.type);
    setValue('label', f.label || '');
    setValue('key', f.key || '');
    input('required').checked = !!f.required;
    setValue('options', (f.options || []).join('\n'));
    setValue('html', f.html || '');
    setValue('help', f.help || '');
    setValue('placeholder', f.placeholder || '');
    setValue('width', f.width === 'half' ? 'half' : 'full');
    setValue('mapsTo', f.mapsTo || '');
    renderHtmlPreview(f.html);
  }
  // Don't disturb the caret in the box the user is typing in.
  function setValue(name, value) {
    const el = input(name);
    if (document.activeElement !== el && el.value !== value) el.value = value;
  }

  editor.addEventListener('input', onEdit);
  editor.addEventListener('change', onEdit);
  function onEdit(e) {
    const prop = e.target.dataset.prop;
    const f = fields[selected];
    if (!prop || !f) return;
    const el = e.target;
    switch (prop) {
      case 'type': {
        const t = el.value;
        f.type = t;
        if (SHOW.options(t) && !(f.options && f.options.length)) f.options = ['Option 1', 'Option 2'];
        if (!SHOW.options(t)) f.options = null;
        if (t === 'notice' && !f.html) f.html = '<p>' + escapeHtml(f.label || 'Type the notice here.') + '</p>';
        if (structural(t)) { f.required = false; f.width = 'full'; f.mapsTo = null; f.key = ''; }
        else if (!f.key) { f.key = uniqueKey(slug(f.label), selected); f._auto = true; }
        if (!SHOW.mapsTo(t)) f.mapsTo = null;
        if (t === 'city' && !f.mapsTo) f.mapsTo = 'CityId';
        if (t !== 'city' && f.mapsTo === 'CityId') f.mapsTo = null;
        break;
      }
      case 'label':
        f.label = el.value;
        if (f._auto && !structural(f.type)) f.key = uniqueKey(slug(f.label), selected);
        break;
      case 'key':
        f.key = el.value.replace(/[^A-Za-z0-9_]/g, '');
        if (el.value !== f.key) el.value = f.key;
        f._auto = f.key === '';
        break;
      case 'required': f.required = el.checked; break;
      case 'options': f.options = el.value.split('\n'); break;
      case 'html': f.html = el.value; renderHtmlPreview(f.html); break;
      case 'help': f.help = el.value || null; break;
      case 'placeholder': f.placeholder = el.value || null; break;
      case 'width': f.width = el.value; break;
      case 'mapsTo': f.mapsTo = el.value || null; break;
    }
    if (e.type === 'change' || prop !== 'html') changed();
    else markDirty();
  }

  function renderHtmlPreview(html) {
    if (!htmlPreview) return;
    const doc = new DOMParser().parseFromString(`<div>${html || ''}</div>`, 'text/html');
    doc.querySelectorAll('script,style,iframe,object,embed,form,input,button,link,meta,svg').forEach(n => n.remove());
    doc.querySelectorAll('*').forEach(n => [...n.attributes].forEach(a => {
      if (/^on/i.test(a.name) || /^\s*(javascript|vbscript|data):/i.test(a.value)) n.removeAttribute(a.name);
    }));
    htmlPreview.innerHTML = doc.body.firstElementChild.innerHTML || '<span class="text-body-secondary">Nothing to show yet.</span>';
  }

  // ---- saving
  function changed() { markDirty(); render(); }
  function markDirty() {
    if (!dirty) {
      dirty = true;
      saveState.textContent = 'Unsaved changes';
    }
    if (publishBtn) { publishBtn.disabled = true; publishBtn.title = 'Save the draft first'; }
  }

  async function save() {
    if (saving) return false;
    saving = true;
    saveBtn.disabled = true;
    saveState.textContent = 'Saving…';
    const payload = {
      fields: fields.map(({ _auto, ...f }) => ({ ...f, options: f.options ? f.options.map(o => o.trim()).filter(Boolean) : null })),
    };
    try {
      const res = await fetch(cfg.saveUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', RequestVerificationToken: token, Accept: 'application/json' },
        body: JSON.stringify(payload),
      });
      const data = await res.json().catch(() => null);
      if (res.ok && data?.ok) {
        const autos = fields.map(f => f._auto);
        fields = data.fields.map((f, i) => ({ ...blank, ...f, _auto: !!autos[i] && !structural(f.type) }));
        problems = new Map();
        problemsBox.hidden = true;
        dirty = false;
        saveState.textContent = 'Draft saved at ' + new Date().toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
        const status = document.getElementById('draftStatus');
        if (status) status.textContent = "You're editing an unpublished draft.";
        if (publishBtn) { publishBtn.disabled = false; publishBtn.title = ''; }
        render();
        return true;
      }
      problems = new Map((data?.problems || []).map(p => [p.index, p.message]));
      problemsBox.innerHTML = `<div class="fw-semibold mb-1">${escapeHtml(data?.message || 'The draft could not be saved.')}</div>` +
        (data?.problems?.length ? '<ul class="mb-0">' + data.problems.map(p => `<li>${escapeHtml(p.message)}</li>`).join('') + '</ul>' : '');
      problemsBox.hidden = false;
      saveState.textContent = 'Not saved';
      render();
      const firstBad = [...problems.keys()].find(i => i >= 0);
      if (firstBad !== undefined) select(firstBad);
      return false;
    } catch {
      saveState.textContent = "Couldn't reach the server. Your changes are still here; try again.";
      return false;
    } finally {
      saving = false;
      saveBtn.disabled = false;
    }
  }
  saveBtn.addEventListener('click', save);
  document.addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); save(); }
  });
  previewBtn?.addEventListener('click', async e => {
    if (!dirty) return;
    e.preventDefault();
    const win = window.open('about:blank', '_blank');
    if (await save()) { if (win) win.location = cfg.previewUrl; else location.href = cfg.previewUrl; }
    else if (win) win.close();
  });
  document.getElementById('publishForm')?.addEventListener('submit', e => { if (dirty) { e.preventDefault(); alert('Save the draft before publishing.'); } });
  window.addEventListener('beforeunload', e => { if (dirty) { e.preventDefault(); e.returnValue = ''; } });

  // DOMParser documents are inert: nothing loads or runs while we read the text.
  function textOf(html) { return (new DOMParser().parseFromString(html || '', 'text/html').body.textContent || '').trim().replace(/\s+/g, ' '); }
  function escapeHtml(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

  render();
})();
