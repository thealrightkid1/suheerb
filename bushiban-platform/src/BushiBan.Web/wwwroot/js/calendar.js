// Branch calendar. FullCalendar runs in "UTC" mode as a no-conversion mode: the server sends and stores
// branch-local wall time without an offset, so 6 pm is 6 pm for everyone looking at this branch.
(function () {
  'use strict';
  const el = document.getElementById('calendar');
  if (!el || !window.FullCalendar) return;
  const cfg = JSON.parse(document.getElementById('calendarConfig').textContent);
  const token = document.querySelector('#calAntiforgery input[name="__RequestVerificationToken"]').value;
  const categories = Object.fromEntries(cfg.categories.map(c => [c.name, c]));
  const showClasses = document.getElementById('showClasses');
  const showBirthdays = document.getElementById('showBirthdays');
  const alertBox = document.getElementById('calAlert');

  // "Now" in the branch's time zone, kept ticking from the server's reading.
  const nowOffset = Date.parse(cfg.now + 'Z') - Date.now();

  showClasses.checked = localStorage.getItem('bb-cal-classes') !== '0';
  showBirthdays.checked = localStorage.getItem('bb-cal-birthdays') === '1';

  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const datePart = s => (s || '').slice(0, 10);
  const timePart = s => (s || '').length > 10 ? s.slice(11, 16) : '';
  const addDays = (date, n) => { const d = new Date(date + 'T00:00:00Z'); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); };
  const addMinutes = (date, time, n) => { const d = new Date(`${date}T${time}:00Z`); d.setUTCMinutes(d.getUTCMinutes() + n); return d.toISOString().slice(0, 16); };
  const wall = d => d ? d.toISOString().slice(0, 19) : null; // UTC-mode Date -> branch wall time
  const fmt = new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', weekday: 'short', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
  const fmtDay = new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' });

  function showError(message) {
    alertBox.textContent = message;
    alertBox.classList.toggle('d-none', !message);
  }

  async function post(url, payload) {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Accept': 'application/json', 'RequestVerificationToken': token },
      body: JSON.stringify(payload),
    });
    let body = null;
    try { body = await res.json(); } catch { /* not JSON */ }
    if (!res.ok) throw new Error(body?.error || (res.status === 403 ? "You don't have permission to change the calendar." : 'Something went wrong. Please try again.'));
    return body;
  }

  const calendar = new FullCalendar.Calendar(el, {
    timeZone: 'UTC',
    now: () => new Date(Date.now() + nowOffset),
    initialView: localStorage.getItem('bb-cal-view') || 'dayGridMonth',
    headerToolbar: { left: 'prev,next today', center: 'title', right: 'dayGridMonth,timeGridWeek,timeGridDay,listWeek' },
    buttonText: { today: 'Today', month: 'Month', week: 'Week', day: 'Day', list: 'List' },
    height: 'auto',
    nowIndicator: true,
    dayMaxEvents: 4,
    slotMinTime: '06:00:00',
    slotMaxTime: '22:00:00',
    scrollTime: '15:00:00',
    eventTimeFormat: { hour: 'numeric', minute: '2-digit', meridiem: 'short' },
    editable: cfg.canManage,
    selectable: cfg.canManage,
    selectMirror: true,
    eventSources: [{
      url: cfg.urls.events,
      extraParams: () => ({ classes: showClasses.checked, birthdays: showBirthdays.checked }),
      failure: () => showError("Couldn't load the calendar. Check your connection and refresh the page."),
    }],
    loading: busy => document.getElementById('calLoading').classList.toggle('d-none', !busy),
    datesSet: info => localStorage.setItem('bb-cal-view', info.view.type),
    select: info => { calendar.unselect(); openEditor({ start: info.startStr, end: info.endStr, allDay: info.allDay }); },
    eventClick: info => {
      info.jsEvent.preventDefault();
      const p = info.event.extendedProps;
      if (cfg.canManage && p.kind === 'event') openEditor(info.event); else showDetails(info.event);
    },
    eventDrop: info => move(info),
    eventResize: info => move(info),
    eventDidMount: info => {
      const p = info.event.extendedProps;
      const bits = [p.location, p.description].filter(Boolean);
      if (bits.length) info.el.title = bits.join(' · ');
    },
  });
  calendar.render();
  showError('');

  [[showClasses, 'bb-cal-classes'], [showBirthdays, 'bb-cal-birthdays']].forEach(([box, key]) =>
    box.addEventListener('change', () => { localStorage.setItem(key, box.checked ? '1' : '0'); calendar.refetchEvents(); }));

  async function move(info) {
    const e = info.event;
    try {
      showError('');
      await post(cfg.urls.move, { id: e.extendedProps.eventId, start: wall(e.start), end: wall(e.end), allDay: e.allDay });
    } catch (err) {
      info.revert();
      showError(err.message);
    }
  }

  // ----- Read-only details (classes, birthdays, or no permission to edit) -----
  function describeWhen(ev) {
    if (ev.allDay) {
      const last = ev.end ? new Date(ev.end.getTime() - 86400000) : null;
      return last && last.getTime() > ev.start.getTime() ? `${fmtDay.format(ev.start)} – ${fmtDay.format(last)}` : fmtDay.format(ev.start);
    }
    return ev.end ? `${fmt.format(ev.start)} – ${new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', hour: 'numeric', minute: '2-digit' }).format(ev.end)}` : fmt.format(ev.start);
  }

  function showDetails(ev) {
    const p = ev.extendedProps;
    const cat = p.kind === 'birthday' ? 'Birthday' : (categories[p.category]?.label ?? '');
    document.getElementById('detailsTitle').textContent = ev.title;
    document.getElementById('detailsBody').innerHTML =
      `<div class="mb-2"><i class="bi bi-clock me-1"></i>${esc(describeWhen(ev))}</div>` +
      (cat ? `<div class="mb-2"><span class="category-dot me-1" style="--c:${esc(ev.backgroundColor)}"></span>${esc(cat)}</div>` : '') +
      (p.location ? `<div class="mb-2"><i class="bi bi-geo-alt me-1"></i>${esc(p.location)}</div>` : '') +
      (p.description ? `<div class="text-body-secondary" style="white-space:pre-wrap">${esc(p.description)}</div>` : '') +
      (p.kind === 'class' ? '<div class="text-body-secondary mt-2">Class times come from the program schedule. Change them on the program.</div>' : '');
    bootstrap.Modal.getOrCreateInstance(document.getElementById('detailsModal')).show();
  }

  // ----- Editor -----
  if (!cfg.canManage) return;
  const modalEl = document.getElementById('eventModal');
  const modal = bootstrap.Modal.getOrCreateInstance(modalEl);
  const f = id => document.getElementById(id);
  const allDayBox = f('evAllDay');
  const errorBox = f('eventError');

  function syncAllDay() {
    modalEl.querySelectorAll('.timed').forEach(x => x.classList.toggle('d-none', allDayBox.checked));
    if (!allDayBox.checked && !f('evStartTime').value) {
      f('evStartTime').value = '18:00';
      f('evEndTime').value = '19:00';
      if (!f('evEndDate').value) f('evEndDate').value = f('evStartDate').value;
    }
  }
  allDayBox.addEventListener('change', syncAllDay);
  f('evStartDate').addEventListener('change', () => { if (!f('evEndDate').value || f('evEndDate').value < f('evStartDate').value) f('evEndDate').value = f('evStartDate').value; });

  // Accepts a FullCalendar event (edit) or a { start, end, allDay } range (new).
  function openEditor(src) {
    const isEvent = typeof src.setProp === 'function';
    const p = isEvent ? src.extendedProps : {};
    const start = isEvent ? wall(src.start) : src.start;
    const end = isEvent ? wall(src.end) : src.end;
    const allDay = !!src.allDay;
    errorBox.classList.add('d-none');
    f('eventModalTitle').textContent = isEvent ? 'Edit event' : 'Add event';
    f('evId').value = isEvent ? p.eventId : '';
    f('evTitle').value = isEvent ? src.title : '';
    allDayBox.checked = allDay;
    f('evStartDate').value = datePart(start);
    f('evStartTime').value = allDay ? '' : timePart(start);
    if (allDay) {
      // FullCalendar's all-day end is exclusive; the form shows the last day.
      f('evEndDate').value = end ? addDays(datePart(end), -1) : datePart(start);
      f('evEndTime').value = '';
    } else {
      const e = end || addMinutes(datePart(start), timePart(start), 60);
      f('evEndDate').value = datePart(e);
      f('evEndTime').value = timePart(e);
    }
    f('evCategory').value = isEvent ? String(categories[p.category]?.value ?? '') : f('evCategory').querySelector('[selected]')?.value ?? f('evCategory').value;
    f('evLocation').value = isEvent ? (p.location || '') : '';
    f('evDescription').value = isEvent ? (p.description || '') : '';
    f('evDelete').classList.toggle('d-none', !isEvent);
    syncAllDay();
    modal.show();
    setTimeout(() => f('evTitle').focus(), 300);
  }

  document.getElementById('addEvent')?.addEventListener('click', () => {
    const today = new Date(Date.now() + nowOffset).toISOString().slice(0, 10);
    openEditor({ start: `${today}T18:00:00`, end: `${today}T19:00:00`, allDay: false });
  });

  f('eventForm').addEventListener('submit', async e => {
    e.preventDefault();
    const title = f('evTitle').value.trim();
    const allDay = allDayBox.checked;
    const sd = f('evStartDate').value, ed = f('evEndDate').value;
    const fail = msg => { errorBox.textContent = msg; errorBox.classList.remove('d-none'); };
    if (!title) return fail('Give the event a title.');
    if (!sd) return fail('Choose when the event starts.');
    let start, end = null;
    if (allDay) {
      start = sd;
      if (ed && ed < sd) return fail("The event can't end before it starts.");
      end = ed && ed > sd ? addDays(ed, 1) : null;
    } else {
      const st = f('evStartTime').value || '00:00';
      start = `${sd}T${st}`;
      if (ed && f('evEndTime').value) end = `${ed}T${f('evEndTime').value}`;
      if (end && end < start) return fail("The event can't end before it starts.");
    }
    const id = f('evId').value;
    const save = f('evSave');
    save.disabled = true;
    try {
      await post(cfg.urls.save, {
        id: id ? Number(id) : null, title, start, end, allDay,
        category: Number(f('evCategory').value), location: f('evLocation').value, description: f('evDescription').value,
      });
      modal.hide();
      calendar.refetchEvents();
    } catch (err) { fail(err.message); }
    finally { save.disabled = false; }
  });

  f('evDelete').addEventListener('click', async () => {
    if (!confirm('Delete this event?')) return;
    try {
      await post(cfg.urls.delete, { id: Number(f('evId').value) });
      modal.hide();
      calendar.refetchEvents();
    } catch (err) { errorBox.textContent = err.message; errorBox.classList.remove('d-none'); }
  });
})();
