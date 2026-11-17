// Staff screens for attendance and academics: ordered row editors, roll call tiles,
// schedule mode switch and promotion checkboxes. Vanilla JS, no build step.
(function () {
  'use strict';

  // ------------------------------------------------------------------ Ordered rows: <div data-rows="Prefix">
  // Rows are <tr data-row> inside [data-rows-body]; a <template data-row-template> uses __i__ for the index.
  // Inputs are renamed Prefix[0]…, Prefix[n] after every change so MVC binds them in order.
  document.querySelectorAll('[data-rows]').forEach(initRows);

  function initRows(root) {
    const prefix = root.dataset.rows;
    const min = parseInt(root.dataset.min || '0', 10);
    const body = root.querySelector('[data-rows-body]');
    const template = root.querySelector('template[data-row-template]');
    const colors = root.dataset.colors ? JSON.parse(root.dataset.colors) : null;
    const pattern = new RegExp('^' + prefix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\[(\\d+|__i__)\\]');

    function rows() { return [...body.querySelectorAll(':scope > [data-row]')]; }
    function renumber() {
      rows().forEach((row, i) => {
        row.querySelectorAll('[name]').forEach(el => { el.name = el.name.replace(pattern, `${prefix}[${i}]`); });
        const idx = row.querySelector('[data-row-index]');
        if (idx) idx.textContent = i + 1;
      });
      const all = rows();
      all.forEach((row, i) => {
        const up = row.querySelector('[data-row-up]'); if (up) up.disabled = i === 0;
        const down = row.querySelector('[data-row-down]'); if (down) down.disabled = i === all.length - 1;
        const rm = row.querySelector('[data-row-remove]'); if (rm) rm.disabled = all.length <= min;
      });
    }
    function paintBelt(select) {
      if (!colors) return;
      const c = colors[select.value];
      select.style.borderLeft = c ? `10px solid ${c}` : '';
    }

    root.addEventListener('click', e => {
      const btn = e.target.closest('button');
      if (!btn || !root.contains(btn)) return;
      const row = btn.closest('[data-row]');
      if (btn.matches('[data-row-add]')) {
        const html = template.innerHTML.replaceAll('__i__', rows().length);
        body.insertAdjacentHTML('beforeend', html);
        const added = rows().at(-1);
        renumber();
        added.querySelectorAll('select[data-belt-select]').forEach(paintBelt);
        const first = added.querySelector('select, input');
        if (first) first.focus();
      } else if (btn.matches('[data-row-remove]') && row) {
        if (rows().length > min) { row.remove(); renumber(); }
      } else if (btn.matches('[data-row-up]') && row?.previousElementSibling) {
        row.parentNode.insertBefore(row, row.previousElementSibling); renumber();
      } else if (btn.matches('[data-row-down]') && row?.nextElementSibling) {
        row.parentNode.insertBefore(row.nextElementSibling, row); renumber();
      }
    });
    root.addEventListener('change', e => { if (e.target.matches('select[data-belt-select]')) paintBelt(e.target); });
    root.querySelectorAll('select[data-belt-select]').forEach(paintBelt);

    // Drag to reorder by the grip handle (mouse and touch via pointer events).
    let dragging = null;
    body.addEventListener('pointerdown', e => {
      const handle = e.target.closest('.drag-handle');
      if (!handle) return;
      dragging = handle.closest('[data-row]');
      dragging.classList.add('dragging');
      handle.setPointerCapture(e.pointerId);
      e.preventDefault();
    });
    body.addEventListener('pointermove', e => {
      if (!dragging) return;
      const over = document.elementFromPoint(e.clientX, e.clientY)?.closest('[data-row]');
      if (!over || over === dragging || over.parentNode !== body) return;
      const rect = over.getBoundingClientRect();
      body.insertBefore(dragging, e.clientY < rect.top + rect.height / 2 ? over : over.nextSibling);
    });
    const endDrag = () => { if (dragging) { dragging.classList.remove('dragging'); dragging = null; renumber(); } };
    body.addEventListener('pointerup', endDrag);
    body.addEventListener('pointercancel', endDrag);

    renumber();
  }

  // ------------------------------------------------------------------ Program schedule: same time vs per day
  const modes = document.querySelectorAll('[data-schedule-mode]');
  modes.forEach(m => m.addEventListener('change', () => {
    document.querySelectorAll('[data-schedule-panel]').forEach(p => { p.hidden = p.dataset.schedulePanel !== m.value; });
    if (m.value === 'perDay') {
      const body = document.querySelector('[data-rows="Rows"] [data-rows-body]');
      if (body && !body.querySelector('[data-row]')) document.querySelector('[data-rows="Rows"] [data-row-add]')?.click();
    }
  }));

  // ------------------------------------------------------------------ Roll call
  const rollForm = document.getElementById('rollForm');
  if (rollForm) {
    const grid = document.getElementById('rollGrid');
    const count = document.getElementById('presentCount');
    const total = document.getElementById('rosterCount');
    const dirty = document.getElementById('rollDirty');
    const update = () => {
      const tiles = grid.querySelectorAll('.roll-tile');
      count.textContent = grid.querySelectorAll('.roll-check:checked').length;
      total.textContent = tiles.length;
    };
    const markDirty = () => { dirty.hidden = false; update(); };
    grid.addEventListener('change', markDirty);
    rollForm.querySelectorAll('[data-roll-all]').forEach(b => b.addEventListener('click', () => {
      const on = b.dataset.rollAll === 'true';
      grid.querySelectorAll('.roll-check:not(:disabled)').forEach(c => { c.checked = on; });
      markDirty();
    }));

    const add = document.getElementById('addStudent');
    const addBtn = document.getElementById('addStudentBtn');
    const template = document.getElementById('rollTileTemplate');
    const addStudent = () => {
      const id = add.value;
      if (!id) return;
      const existing = grid.querySelector(`.roll-check[value="${CSS.escape(id)}"]`);
      if (existing) { existing.checked = true; markDirty(); return; }
      const label = (add.tomselect ? add.tomselect.options[id]?.text : add.selectedOptions[0]?.textContent) || '';
      const tile = template.content.firstElementChild.cloneNode(true);
      tile.querySelector('.roll-check').value = id;
      tile.querySelector('.roll-name').textContent = label.split(' · ')[0];
      grid.append(tile);
      document.getElementById('rollEmpty')?.remove();
      if (add.tomselect) { add.tomselect.removeOption(id); add.tomselect.clear(); } else { add.selectedOptions[0]?.remove(); add.value = ''; }
      markDirty();
      tile.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    };
    addBtn?.addEventListener('click', addStudent);
    add?.addEventListener('change', addStudent);

    let submitting = false;
    rollForm.addEventListener('submit', () => { submitting = true; });
    window.addEventListener('beforeunload', e => { if (!dirty.hidden && !submitting) { e.preventDefault(); e.returnValue = ''; } });
  }

  // ------------------------------------------------------------------ Promotions: select all + count
  const promoteForm = document.getElementById('promoteForm');
  if (promoteForm) {
    const button = document.getElementById('promoteSelected');
    const selCount = document.getElementById('selCount');
    const refresh = () => {
      const n = promoteForm.querySelectorAll('input[name="studentIds"]:checked').length;
      if (selCount) selCount.textContent = n;
      if (button) button.disabled = n === 0;
    };
    promoteForm.querySelectorAll('[data-check-all]').forEach(all => all.addEventListener('change', () => {
      promoteForm.querySelectorAll(`input[data-group="${all.dataset.checkAll}"]`).forEach(c => { c.checked = all.checked; });
      refresh();
    }));
    promoteForm.addEventListener('change', refresh);
    promoteForm.addEventListener('submit', e => {
      const single = e.submitter?.dataset.confirmPromote;
      const n = promoteForm.querySelectorAll('input[name="studentIds"]:checked').length;
      const msg = single || `Promote ${n} student${n === 1 ? '' : 's'} to their next belt?`;
      if (!confirm(msg)) e.preventDefault();
    });
    refresh();
  }
})();
