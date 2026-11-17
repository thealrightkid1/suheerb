// Billing and accounting behaviours: editable line tables, voucher totals, payment allocation, contract pricing, chart tree.
(function () {
  'use strict';

  const money = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
  const num = v => { const n = parseFloat(String(v ?? '').replace(/[$,\s]/g, '')); return isNaN(n) ? 0 : Math.round(n * 100) / 100; };
  const cents = n => Math.round(n * 100);
  window.bbMoney = n => money.format(n);

  // Print buttons: <button data-print>.
  document.querySelectorAll('[data-print]').forEach(b => b.addEventListener('click', () => window.print()));

  // Selects that reload the page with their value: <select data-reload="studentId">.
  document.querySelectorAll('select[data-reload]').forEach(sel => sel.addEventListener('change', () => {
    const url = new URL(window.location.href);
    if (sel.value) url.searchParams.set(sel.dataset.reload, sel.value); else url.searchParams.delete(sel.dataset.reload);
    window.location.href = url.toString();
  }));

  // ── Editable line tables ──────────────────────────────────────────────
  // <table data-lines="Lines"> with <tbody data-lines-body>, <template data-line-template> (names use __i__),
  // [data-add-line] buttons and [data-remove-line] buttons in each row. Names are renumbered so the server sees 0..n-1.
  document.querySelectorAll('table[data-lines]').forEach(table => {
    const prefix = table.dataset.lines;
    const body = table.querySelector('[data-lines-body]');
    const template = document.querySelector(`template[data-line-template="${prefix}"]`);
    const pattern = new RegExp('^' + prefix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\[(\\d+|__i__)\\]');
    const changed = () => table.dispatchEvent(new CustomEvent('lines:changed', { bubbles: true }));

    const renumber = () => {
      [...body.rows].forEach((row, i) => {
        row.querySelectorAll('[name]').forEach(el => { el.name = el.name.replace(pattern, `${prefix}[${i}]`); });
        const no = row.querySelector('.line-no'); if (no) no.textContent = i + 1;
      });
    };
    const add = () => {
      const fragment = template.content.cloneNode(true);
      const row = fragment.querySelector('tr');
      body.appendChild(fragment);
      renumber();
      if (window.bbEnhanceSelects) window.bbEnhanceSelects(row);
      const first = row.querySelector('select, input'); if (first && first.tomselect) first.tomselect.focus(); else if (first) first.focus();
      changed();
      return row;
    };
    document.querySelectorAll(`[data-add-line="${prefix}"]`).forEach(b => b.addEventListener('click', e => { e.preventDefault(); add(); }));
    body.addEventListener('click', e => {
      const btn = e.target.closest('[data-remove-line]');
      if (!btn) return;
      e.preventDefault();
      btn.closest('tr').remove();
      if (body.rows.length === 0) add();
      renumber();
      changed();
    });
    body.addEventListener('input', changed);
    body.addEventListener('change', changed);
    renumber();
    table.bbAddLine = add;
  });

  // ── Voucher totals ────────────────────────────────────────────────────
  const voucher = document.querySelector('[data-voucher-lines]');
  if (voucher) {
    const out = name => document.querySelector(`[data-total="${name}"]`);
    const update = () => {
      let dr = 0, cr = 0;
      voucher.querySelectorAll('[data-debit]').forEach(i => dr += num(i.value));
      voucher.querySelectorAll('[data-credit]').forEach(i => cr += num(i.value));
      const diff = (cents(dr) - cents(cr)) / 100;
      out('debit').textContent = money.format(dr);
      out('credit').textContent = money.format(cr);
      const d = out('difference');
      d.textContent = diff === 0 ? (dr > 0 ? 'Balanced' : '–') : money.format(Math.abs(diff)) + (diff > 0 ? ' more debit' : ' more credit');
      d.className = 'money ' + (diff === 0 && dr > 0 ? 'diff-ok' : diff === 0 ? '' : 'diff-bad');
      const post = document.querySelector('[data-post-button]');
      if (post) post.title = diff === 0 && dr > 0 ? '' : 'Debits and credits must be equal before posting. You can still save a draft.';
    };
    // A line is a debit or a credit: typing in one clears the other.
    voucher.addEventListener('input', e => {
      const row = e.target.closest('tr');
      if (!row || !e.target.value) return;
      if (e.target.matches('[data-debit]')) row.querySelector('[data-credit]').value = '';
      if (e.target.matches('[data-credit]')) row.querySelector('[data-debit]').value = '';
    });
    voucher.addEventListener('lines:changed', update);
    update();
  }

  // ── Manual invoice total ──────────────────────────────────────────────
  const invoiceLines = document.querySelector('[data-invoice-lines]');
  if (invoiceLines) {
    const update = () => {
      let total = 0;
      invoiceLines.querySelectorAll('[data-amount]').forEach(i => total += num(i.value));
      document.querySelector('[data-total="invoice"]').textContent = money.format(total);
    };
    invoiceLines.addEventListener('lines:changed', update);
    update();
  }

  // ── Payment allocation ───────────────────────────────────────────────
  // Fills invoice amounts oldest first as the amount is typed, until someone edits an invoice amount by hand.
  const payment = document.querySelector('[data-payment-form]');
  if (payment) {
    const amount = payment.querySelector('[data-payment-amount]');
    const inputs = [...payment.querySelectorAll('[data-allocation]')];
    let manual = false;
    const summary = () => {
      const paid = num(amount.value);
      const applied = inputs.reduce((s, i) => s + num(i.value), 0);
      const left = (cents(paid) - cents(applied)) / 100;
      const el = payment.querySelector('[data-allocation-summary]');
      if (!el) return;
      if (left < 0) { el.className = 'diff-bad'; el.textContent = `You've applied ${money.format(applied)}, which is ${money.format(-left)} more than the payment.`; }
      else if (left > 0) { el.className = 'muted'; el.textContent = `${money.format(applied)} applied to invoices. ${money.format(left)} will be kept as credit on the student's account.`; }
      else { el.className = 'diff-ok'; el.textContent = paid > 0 ? `${money.format(applied)} applied to invoices.` : ''; }
    };
    const autoFill = () => {
      if (manual) return summary();
      let left = cents(num(amount.value));
      inputs.forEach(i => {
        const owed = cents(num(i.dataset.balance));
        const apply = Math.min(owed, Math.max(0, left));
        i.value = apply > 0 ? (apply / 100).toFixed(2) : '';
        left -= apply;
      });
      summary();
    };
    amount.addEventListener('input', autoFill);
    inputs.forEach(i => i.addEventListener('input', () => { manual = true; summary(); }));
    payment.querySelectorAll('[data-pay-full]').forEach(b => b.addEventListener('click', e => {
      e.preventDefault();
      const due = inputs.reduce((s, i) => s + num(i.dataset.balance), 0);
      manual = false;
      amount.value = due.toFixed(2);
      autoFill();
    }));
    payment.querySelectorAll('[data-reset-allocation]').forEach(b => b.addEventListener('click', e => { e.preventDefault(); manual = false; autoFill(); }));
    if (inputs.some(i => i.value)) { manual = true; summary(); } else autoFill();
  }

  // ── Contract pricing ─────────────────────────────────────────────────
  const contract = document.querySelector('[data-contract-form]');
  if (contract) {
    const programs = JSON.parse(contract.dataset.programs || '[]');
    const f = name => contract.querySelector(`[name="${name}"]`);
    const today = contract.dataset.today;
    const preview = () => {
      const monthly = num(f('MonthlyFee').value), discount = num(f('MonthlyDiscount').value);
      const net = Math.max(0, monthly - discount);
      const term = parseInt(f('TermMonths').value, 10) || 0;
      const startText = f('StartDate').value;
      contract.querySelector('[data-preview="net"]').textContent = money.format(net);
      contract.querySelector('[data-preview="total"]').textContent = money.format(net * term);
      const endEl = contract.querySelector('[data-preview="end"]');
      const signupEl = contract.querySelector('[data-preview="signup"]');
      if (!startText || !term) { endEl.textContent = '–'; signupEl.textContent = '–'; return; }
      const [y, m, d] = startText.split('-').map(Number);
      const end = new Date(y, m - 1 + term, d - 1);
      endEl.textContent = end.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
      const firstMonthDue = startText.slice(0, 7) <= today.slice(0, 7);
      const signup = num(f('AdmissionFee').value) + num(f('SecurityFee').value) + (firstMonthDue ? net : 0);
      signupEl.textContent = money.format(signup) + (firstMonthDue ? ' (includes the first month)' : '');
    };
    f('CourseId').addEventListener('change', () => {
      const p = programs.find(x => String(x.id) === f('CourseId').value);
      if (!p) return preview();
      if (p.hasFeeStructure) {
        f('AdmissionFee').value = p.admissionFee.toFixed(2);
        f('SecurityFee').value = p.securityFee.toFixed(2);
        f('MonthlyFee').value = p.monthlyFee.toFixed(2);
      }
      f('TermMonths').value = p.minimumTermMonths;
      preview();
    });
    contract.addEventListener('input', preview);
    contract.addEventListener('change', preview);
    preview();
  }

  // ── Cancellation fee suggestion ──────────────────────────────────────
  const cancel = document.querySelector('[data-cancel-form]');
  if (cancel) {
    const date = cancel.querySelector('[name="date"]');
    const fee = cancel.querySelector('[name="fee"]');
    const note = cancel.querySelector('[data-quote-note]');
    date.addEventListener('change', async () => {
      if (!date.value) return;
      const res = await fetch(`${cancel.dataset.quoteUrl}?date=${encodeURIComponent(date.value)}`, { headers: { Accept: 'application/json' } });
      if (!res.ok) return;
      const q = await res.json();
      fee.value = q.suggestedFee.toFixed(2);
      note.textContent = `${q.remainingMonths} month(s) left after this date: ${money.format(q.remainder)}. Suggested fee is the lesser of $250.00 and that.`;
    });
  }

  // ── Chart of accounts tree ────────────────────────────────────────────
  const tree = document.querySelector('[data-tree]');
  if (tree) {
    const setOpen = (btn, open) => {
      btn.setAttribute('aria-expanded', open);
      const id = btn.dataset.target;
      tree.querySelectorAll(`[data-parent~="${id}"]`).forEach(r => {
        // Rows stay hidden while any ancestor is collapsed.
        const hidden = r.dataset.parent.split(' ').some(p => tree.querySelector(`[data-target="${p}"]`)?.getAttribute('aria-expanded') === 'false');
        r.classList.toggle('d-none', hidden);
      });
    };
    tree.addEventListener('click', e => {
      const btn = e.target.closest('.tree-toggle');
      if (btn) setOpen(btn, btn.getAttribute('aria-expanded') === 'false');
    });
    document.querySelectorAll('[data-tree-all]').forEach(b => b.addEventListener('click', () => {
      const open = b.dataset.treeAll === 'open';
      tree.querySelectorAll('.tree-toggle').forEach(t => t.setAttribute('aria-expanded', open));
      tree.querySelectorAll('[data-parent]').forEach(r => r.classList.toggle('d-none', !open));
    }));
  }
})();
