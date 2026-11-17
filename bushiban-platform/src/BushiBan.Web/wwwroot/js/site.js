// Small, dependency-free behaviours shared by every page.
(function () {
  'use strict';

  // Light / dark theme, remembered per browser.
  const toggle = document.getElementById('themeToggle');
  if (toggle) toggle.addEventListener('click', () => {
    const next = document.documentElement.dataset.bsTheme === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.bsTheme = next;
    localStorage.setItem('bb-theme', next);
  });

  // Mobile sidebar.
  document.querySelectorAll('[data-toggle-sidebar]').forEach(el =>
    el.addEventListener('click', () => document.body.classList.toggle('sidebar-open')));

  // Confirm before destructive form posts: <form data-confirm="Delete this?">.
  document.addEventListener('submit', e => {
    const msg = e.target.getAttribute && e.target.getAttribute('data-confirm');
    if (msg && !window.confirm(msg)) e.preventDefault();
  }, true);

  // Searchable dropdowns: <select data-search>. Type to filter, arrow keys, Enter.
  window.bbEnhanceSelects = function (root) {
    (root || document).querySelectorAll('select[data-search]:not(.tomselected)').forEach(sel => {
      if (sel.options.length < 8 && !sel.multiple) return;
      new TomSelect(sel, { allowEmptyOption: true, maxOptions: 500, plugins: sel.multiple ? ['remove_button'] : [] });
    });
  };
  if (window.TomSelect) window.bbEnhanceSelects();

  // Auto-submit filter forms when a select changes: <form data-autosubmit>.
  document.querySelectorAll('form[data-autosubmit] select').forEach(s => s.addEventListener('change', () => s.form.requestSubmit()));

  // Bootstrap tooltips.
  document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach(el => new bootstrap.Tooltip(el));
})();
