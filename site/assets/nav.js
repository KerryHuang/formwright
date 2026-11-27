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
  // 瀏覽器可能在 media 回呼前就把隱藏面板內的焦點丟回 body，所以另外記住焦點是否停在面板內。
  // 元素還看得到時失焦（點空白、Tab 出去、跳錨點）才清掉；因面板被隱藏而失焦則保留。
  var focusInPanel = false;
  panel.addEventListener('focusin', function () { focusInPanel = true; });
  panel.addEventListener('focusout', function (e) { if (e.target.getClientRects().length) focusInPanel = false; });
  // 寬度回到放得下時重設狀態；縮到窄版時，停在面板內的焦點移到選單鈕（面板即將收合）
  var onChange = function () {
    if (!mq.matches) setOpen(false);
    else if (focusInPanel) { focusInPanel = false; btn.focus(); }
  };
  if (mq.addEventListener) mq.addEventListener('change', onChange); else mq.addListener(onChange);
  // 事件都綁好才啟用收合樣式；本檔載入失敗時導覽保持展開
  document.documentElement.classList.add('fw-nav-ready');
})();
