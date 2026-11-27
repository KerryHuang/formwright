(function () {
  var btn = document.querySelector('.fw-menu-btn');
  var panel = document.getElementById('fw-nav-panel');
  if (!btn || !panel) return;
  var mq = window.matchMedia('(max-width: 1200px)');

  function setOpen(open, refocus) {
    panel.classList.toggle('is-open', open);
    btn.setAttribute('aria-expanded', String(open));
    btn.textContent = open ? '收起' : '選單';
    if (!open && refocus) btn.focus();
  }

  btn.addEventListener('click', function () { setOpen(btn.getAttribute('aria-expanded') !== 'true'); });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && btn.getAttribute('aria-expanded') === 'true') setOpen(false, true);
  });
  // 點選單內的連結（跳到頁內錨點）後收起；主題切換留著不收
  panel.addEventListener('click', function (e) {
    if (e.target.closest && e.target.closest('a')) setOpen(false);
  });
  // 寬度回到放得下時重設狀態
  var onChange = function () { if (!mq.matches) setOpen(false); };
  if (mq.addEventListener) mq.addEventListener('change', onChange); else mq.addListener(onChange);
})();
