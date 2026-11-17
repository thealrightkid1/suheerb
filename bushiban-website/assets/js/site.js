/* Bushi Ban marketing site: small, dependency-free behaviours. */
(function () {
  "use strict";
  var cfg = window.SITE_CONFIG || {};

  // Point every "Log in" link at the configured login URL.
  if (cfg.loginUrl) {
    document.querySelectorAll("[data-login]").forEach(function (a) { a.setAttribute("href", cfg.loginUrl); });
  }

  // Phone number from config (falls back to what's in the HTML).
  if (cfg.phone) {
    var digits = cfg.phone.replace(/[^\d+]/g, "");
    document.querySelectorAll("[data-phone]").forEach(function (a) {
      a.textContent = cfg.phone;
      a.setAttribute("href", "tel:" + (digits.charAt(0) === "+" ? digits : "+1" + digits));
    });
  }

  // Current year in the footer.
  document.querySelectorAll("[data-year]").forEach(function (el) { el.textContent = new Date().getFullYear(); });

  // Mobile menu.
  var toggle = document.querySelector(".menu-toggle");
  var nav = document.getElementById("mainNav");
  function setMenu(open) {
    document.body.classList.toggle("menu-open", open);
    if (toggle) {
      toggle.setAttribute("aria-expanded", String(open));
      toggle.setAttribute("aria-label", open ? "Close menu" : "Open menu");
    }
  }
  if (toggle) toggle.addEventListener("click", function () { setMenu(!document.body.classList.contains("menu-open")); });
  if (nav) nav.addEventListener("click", function (e) { if (e.target.closest("a")) setMenu(false); });
  document.addEventListener("keydown", function (e) { if (e.key === "Escape") setMenu(false); });

  // Program filter.
  var tabs = document.querySelectorAll(".filter-tabs .tab");
  var cards = document.querySelectorAll(".program-card");
  tabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      var filter = tab.getAttribute("data-filter");
      tabs.forEach(function (t) {
        var active = t === tab;
        t.classList.toggle("is-active", active);
        t.setAttribute("aria-pressed", String(active));
      });
      var grid = document.querySelector(".program-grid");
      if (grid) grid.classList.toggle("is-filtered", filter !== "all");
      cards.forEach(function (card) {
        var tags = (card.getAttribute("data-tags") || "").split(" ");
        card.hidden = filter !== "all" && tags.indexOf(filter) === -1;
      });
    });
  });

  // Clicking a program pre-selects it in the trial form.
  var programSelect = document.getElementById("program");
  document.querySelectorAll("[data-program]").forEach(function (el) {
    el.addEventListener("click", function () {
      if (programSelect) programSelect.value = el.getAttribute("data-program");
    });
  });

  // Trial form.
  var form = document.getElementById("trialForm");
  if (form) {
    var status = form.querySelector(".form-status");
    var parentField = form.querySelector("[data-parent-field]");
    var parentInput = document.getElementById("parentName");

    function syncParent() {
      var who = form.querySelector("input[name=forWhom]:checked");
      var isAdult = who && who.value === "Adult";
      parentField.hidden = isAdult;
      parentInput.required = !isAdult;
    }
    form.querySelectorAll("input[name=forWhom]").forEach(function (r) { r.addEventListener("change", syncParent); });
    syncParent();

    function setStatus(text, kind) {
      status.textContent = text;
      status.className = "form-status" + (kind ? " is-" + kind : "");
    }

    form.addEventListener("submit", function (e) {
      e.preventDefault();
      var firstInvalid = null;
      form.querySelectorAll(".invalid").forEach(function (el) { el.classList.remove("invalid"); });
      form.querySelectorAll("input, select, textarea").forEach(function (el) {
        if (el.closest("[hidden]") || el.type === "radio") return;
        if (!el.checkValidity()) {
          (el.type === "checkbox" ? el.closest("label") : el).classList.add("invalid");
          firstInvalid = firstInvalid || el;
        }
      });
      if (firstInvalid) {
        setStatus("Please fill in the highlighted fields.", "error");
        firstInvalid.focus();
        return;
      }

      var data = {};
      new FormData(form).forEach(function (v, k) { data[k] = v; });
      data.source = "Website";
      data.submittedAt = new Date().toISOString();

      var button = form.querySelector("button[type=submit]");
      button.disabled = true;
      setStatus("Sending…");

      var send = cfg.trialFormEndpoint
        ? fetch(cfg.trialFormEndpoint, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(data) })
            .then(function (r) { if (!r.ok) throw new Error(r.status); })
        : new Promise(function (resolve) {
            // Not connected yet: the developer sets SITE_CONFIG.trialFormEndpoint.
            console.info("[Bushi Ban] Trial form (no endpoint configured):", data);
            setTimeout(resolve, 400);
          });

      send.then(function () {
        form.reset();
        syncParent();
        setStatus("Thank you! Your local Bushi Ban team will be in touch soon to book your class.", "success");
      }).catch(function () {
        setStatus("Sorry, that didn't go through. Please try again or give us a call.", "error");
      }).finally(function () { button.disabled = false; });
    });

    form.addEventListener("input", function (e) {
      var el = e.target.type === "checkbox" ? e.target.closest("label") : e.target;
      if (el.classList.contains("invalid") && e.target.checkValidity()) el.classList.remove("invalid");
    });
  }

  // Gentle reveal on scroll.
  if ("IntersectionObserver" in window && !window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
    var targets = document.querySelectorAll(".section-head, .program-card, .split-row, .why-list li, .steps li, .belt-path, .mosaic, .location-groups, .faq, .cta-grid, .stat");
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) { entry.target.classList.add("is-visible"); io.unobserve(entry.target); }
      });
    }, { rootMargin: "0px 0px -8% 0px" });
    targets.forEach(function (el) { el.classList.add("reveal"); io.observe(el); });
  }
})();
