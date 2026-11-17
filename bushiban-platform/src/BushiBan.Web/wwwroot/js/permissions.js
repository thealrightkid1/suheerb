// User permissions screen: per-group toggles and counters, select all / clear all, fill from a preset.
(function () {
  'use strict';
  const form = document.getElementById('permForm');
  if (!form) return;
  const presets = JSON.parse(document.getElementById('presetData').textContent);
  const boxes = () => Array.from(form.querySelectorAll('input.perm:not([data-admin])'));
  const editable = () => boxes().filter(b => !b.disabled);
  const adminBox = form.querySelector('input[data-admin]');
  const presetName = document.getElementById('presetName');

  function matchPreset() {
    const on = new Set(boxes().filter(b => b.checked).map(b => b.value));
    if (adminBox?.checked) on.add(adminBox.value);
    for (const [key, p] of Object.entries(presets)) {
      const set = new Set(p.permissions);
      if (set.size === on.size && [...set].every(v => on.has(v))) return p.label;
    }
    return 'Custom';
  }

  function refresh() {
    form.querySelectorAll('[data-group]').forEach(group => {
      const items = Array.from(group.querySelectorAll('input.perm'));
      const on = items.filter(b => b.checked).length;
      group.querySelector('[data-group-count]').textContent = `${on} / ${items.length}`;
      const toggle = group.querySelector('[data-group-toggle]');
      toggle.checked = on === items.length;
      toggle.indeterminate = on > 0 && on < items.length;
      toggle.disabled = items.every(b => b.disabled);
      group.classList.toggle('is-empty', on === 0);
    });
    document.getElementById('totalCount').textContent = boxes().filter(b => b.checked).length;
    presetName.textContent = matchPreset();
  }

  form.addEventListener('change', e => {
    if (e.target.matches('[data-group-toggle]')) {
      const group = e.target.closest('[data-group]');
      group.querySelectorAll('input.perm').forEach(b => { if (!b.disabled) b.checked = e.target.checked; });
    }
    refresh();
  });

  document.getElementById('selectAll').addEventListener('click', () => { editable().forEach(b => { b.checked = true; }); refresh(); });
  document.getElementById('clearAll').addEventListener('click', () => { editable().forEach(b => { b.checked = false; }); refresh(); });

  const presetSelect = document.getElementById('applyPreset');
  presetSelect.addEventListener('change', () => {
    const p = presets[presetSelect.value];
    if (!p) return;
    const set = new Set(p.permissions);
    editable().forEach(b => { b.checked = set.has(b.value); });
    if (adminBox && !adminBox.disabled) adminBox.checked = set.has(adminBox.value);
    presetSelect.value = '';
    refresh();
  });

  refresh();
})();
