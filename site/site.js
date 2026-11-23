// АЗС Хаб landing: the pump display counts up once, the request form posts to /api/leads
// (Caddy maps it to the central service), Яндекс Метрика loads only when its id is set.
(function () {
  'use strict';
  var script = document.currentScript;

  // ---------------------------------------------------------------- pump display
  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var digits = document.querySelectorAll('.pump__digits[data-count]');
  if (!reduce && digits.length) {
    var start = null, duration = 1600;
    var targets = Array.prototype.map.call(digits, function (el) {
      return { el: el.querySelector('.pump__value'), to: parseFloat(el.dataset.count), dec: parseInt(el.dataset.decimals || '0', 10) };
    });
    var draw = function (k) {
      targets.forEach(function (t) { t.el.textContent = (t.to * k).toFixed(t.dec); });
    };
    draw(0);
    var tick = function (now) {
      if (start === null) start = now;
      var p = Math.min(1, (now - start) / duration);
      // a pump runs fast first and slows down at the end of the fill
      draw(1 - Math.pow(1 - p, 3));
      if (p < 1) requestAnimationFrame(tick);
    };
    // start once the display font is there, so the digits do not jump
    var go = function () { setTimeout(function () { requestAnimationFrame(tick); }, 250); };
    if (document.fonts && document.fonts.load) document.fonts.load('700 1em DSEG7').then(go, go); else go();
  }

  // ---------------------------------------------------------------- request form
  var form = document.getElementById('lead');
  if (form) {
    var status = form.querySelector('.form__status');
    var button = form.querySelector('button[type=submit]');
    var fail = function (msg, field) {
      status.textContent = msg;
      status.classList.add('is-error');
      if (field) { field.setAttribute('aria-invalid', 'true'); field.focus(); }
    };
    form.addEventListener('input', function (e) { e.target.removeAttribute('aria-invalid'); });
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      status.textContent = '';
      status.classList.remove('is-error');
      var f = form.elements;
      if (!f.name.value.trim()) return fail('Напишите, как к вам обращаться.', f.name);
      var contact = f.contact.value.trim();
      if ((contact.match(/\d/g) || []).length < 5 && contact.indexOf('@') < 0)
        return fail('Укажите телефон, ник в Telegram (с @) или почту.', f.contact);
      if (!f.consent.checked) return fail('Отметьте согласие на обработку персональных данных — без него мы не можем принять заявку.', f.consent);

      var body = {
        name: f.name.value, contact: contact,
        stations: f.stations.value ? parseInt(f.stations.value, 10) : null,
        city: f.city.value || null, consent: true, website: f.website.value || null,
        page: (location.pathname + location.search).slice(0, 500)
      };
      button.disabled = true;
      button.textContent = 'Отправляем…';
      fetch('/api/leads', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
        .then(function (r) {
          if (r.ok) return done();
          return r.json().catch(function () { return {}; }).then(function (p) {
            throw new Error(r.status === 429
              ? 'Слишком много заявок с вашего адреса. Напишите нам в Telegram или на почту.'
              : (p && p.title) || 'Не получилось отправить заявку. Попробуйте ещё раз или напишите нам.');
          });
        })
        .catch(function (err) {
          fail(err && err.message && err.message !== 'Failed to fetch' ? err.message
            : 'Нет связи с сервером. Проверьте интернет и отправьте ещё раз.');
          button.disabled = false;
          button.textContent = 'Оставить заявку';
        });
    });
    var done = function () {
      form.classList.add('is-sent');
      form.innerHTML = '<div class="form__done" tabindex="-1"><h3>Заявка отправлена</h3>' +
        '<p>Мы перезвоним в течение рабочего дня. Если удобнее, напишите нам сами в Telegram или на почту.</p></div>';
      form.querySelector('.form__done').focus();
      goal('lead');
    };
  }

  // ---------------------------------------------------------------- Яндекс Метрика (with Webvisor; form fields are not recorded: ym-disable-keys)
  var counter = script && script.dataset.metrika;
  function goal(name) { if (counter && window.ym) window.ym(Number(counter), 'reachGoal', name); }
  if (counter) {
    window.ym = window.ym || function () { (window.ym.a = window.ym.a || []).push(arguments); };
    window.ym.l = +new Date();
    var s = document.createElement('script');
    s.async = true;
    s.src = 'https://mc.yandex.ru/metrika/tag.js?id=' + counter;
    document.head.appendChild(s);
    window.ym(Number(counter), 'init', { clickmap: true, trackLinks: true, accurateTrackBounce: true, webvisor: true });
  }
})();
