/**
 * Element picker, injected on demand from the popup.
 *
 * Hover to highlight, click to select, tweak the generated selector, then
 * "Hide" saves `<host>##<selector>` to the user's filters. The cosmetic
 * content script picks the new rule up from storage and hides the element.
 *
 * All UI lives in a closed shadow root so page styles cannot leak in.
 */
(() => {
  if (globalThis.__adblockerPicker) return;
  globalThis.__adblockerPicker = true;

  const BLOCKED_EVENTS = ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'auxclick', 'dblclick', 'contextmenu'];

  /* ------------------------------ DOM helpers --------------------------- */

  // DOM built with createElement rather than innerHTML so pages that enforce
  // Trusted Types do not break the picker.
  function h(tag, props = {}, ...children) {
    const el = document.createElement(tag);
    for (const [key, value] of Object.entries(props)) {
      if (key === 'class') el.className = value;
      else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
      else el.setAttribute(key, value);
    }
    el.append(...children);
    return el;
  }

  const STYLE = `
    :host { all: initial; }
    * { box-sizing: border-box; }
    .box {
      position: fixed; pointer-events: none; border-radius: 3px;
      border: 2px solid #e5484d; background: rgb(229 72 77 / 0.16);
      box-shadow: 0 0 0 99999px rgb(0 0 0 / 0.12);
    }
    .hint {
      position: fixed; top: 12px; left: 50%; transform: translateX(-50%);
      padding: 8px 14px; border-radius: 999px; pointer-events: none;
      background: #17181c; color: #fff; box-shadow: 0 6px 20px rgb(0 0 0 / 0.25);
      font: 500 13px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif; white-space: nowrap;
    }
    kbd { font: inherit; padding: 1px 6px; border-radius: 4px; background: rgb(255 255 255 / 0.16); }
    .panel {
      position: fixed; right: 16px; bottom: 16px; width: 360px; max-width: calc(100vw - 32px);
      padding: 16px; border-radius: 14px; pointer-events: auto;
      background: #fff; color: #17181c; border: 1px solid #e3e3e8;
      box-shadow: 0 16px 40px rgb(0 0 0 / 0.22);
      font: 13px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif;
    }
    .title { margin: 0 0 10px; font-size: 15px; font-weight: 650; }
    label { display: block; margin-bottom: 4px; color: #5e606a; font-size: 12px; font-weight: 500; }
    textarea {
      display: block; width: 100%; min-height: 64px; resize: vertical; margin: 0;
      padding: 8px 10px; border-radius: 8px; border: 1px solid #d5d5dc; background: #f7f7f8; color: inherit;
      font: 12px/1.5 ui-monospace, "Cascadia Code", "SF Mono", Consolas, monospace;
    }
    textarea:focus { outline: 2px solid rgb(217 45 58 / 0.4); outline-offset: 1px; border-color: #d92d3a; }
    .count { margin: 6px 0 12px; color: #5e606a; font-size: 12px; min-height: 18px; }
    .count.bad { color: #c62a2f; }
    .row { display: flex; gap: 8px; align-items: center; }
    .spacer { flex: 1; }
    button {
      font: 500 13px/1 system-ui, -apple-system, "Segoe UI", sans-serif; cursor: pointer;
      padding: 8px 12px; border-radius: 8px; border: 1px solid #d5d5dc; background: #fff; color: inherit;
    }
    button:hover { background: #f1f1f3; }
    button:focus-visible { outline: 2px solid rgb(217 45 58 / 0.5); outline-offset: 2px; }
    button.primary { background: #d92d3a; border-color: #d92d3a; color: #fff; }
    button.primary:hover { background: #b42330; }
    button:disabled { opacity: 0.5; cursor: default; }
    .note { margin: 12px 0 0; color: #8b8d98; font-size: 11.5px; }
    [hidden] { display: none !important; }
    @media (prefers-color-scheme: dark) {
      .panel { background: #19191c; color: #ededef; border-color: #2e2e33; }
      label, .count { color: #a9aab3; }
      textarea { background: #111113; border-color: #3a3a40; }
      button { background: #222226; border-color: #3a3a40; }
      button:hover { background: #2c2c31; }
      .note { color: #7c7d86; }
    }
  `;

  /* ------------------------------ UI ------------------------------------ */

  const host = document.createElement('adblocker-picker');
  host.setAttribute('style', 'all: initial !important; position: fixed !important; inset: 0 !important; z-index: 2147483647 !important; pointer-events: none !important;');
  const root = host.attachShadow({ mode: 'closed' });

  const box = h('div', { class: 'box', hidden: '' });
  const hint = h('div', { class: 'hint' }, 'Click an element to hide it · ', h('kbd', {}, 'Esc'), ' to cancel');
  const textarea = h('textarea', { id: 'selector', spellcheck: 'false', rows: '3', oninput: () => preview() });
  const count = h('p', { class: 'count', 'aria-live': 'polite' });
  const hideButton = h('button', { type: 'submit', class: 'primary' }, 'Hide element');
  const siteName = h('b', {}, location.hostname.replace(/^www\./, ''));
  const panel = h(
    'form',
    { class: 'panel', hidden: '', onsubmit: onSubmit },
    h('p', { class: 'title' }, 'Hide element'),
    h('label', { for: 'selector' }, 'CSS selector'),
    textarea,
    count,
    h(
      'div',
      { class: 'row' },
      h('button', { type: 'button', title: 'Widen the selection (↑)', onclick: selectParent }, 'Select parent'),
      h('span', { class: 'spacer' }),
      h('button', { type: 'button', onclick: close }, 'Cancel'),
      hideButton,
    ),
    h('p', { class: 'note' }, 'Saved to your filters for ', siteName, '. You can remove it later in Settings → My filters.'),
  );

  root.append(h('style', {}, STYLE), box, hint, panel);
  document.documentElement.append(host);

  /* ------------------------------ Picking ------------------------------- */

  let hovered = null;
  let selected = null;

  function elementAt(x, y) {
    const el = document.elementFromPoint(x, y);
    if (!el || el === host || el === document.documentElement || el === document.body) return null;
    return el;
  }

  function highlight(el) {
    if (!el?.isConnected) {
      box.hidden = true;
      return;
    }
    const r = el.getBoundingClientRect();
    Object.assign(box.style, { left: `${r.left}px`, top: `${r.top}px`, width: `${r.width}px`, height: `${r.height}px` });
    box.hidden = false;
  }

  function select(el) {
    selected = el;
    textarea.value = selectorFor(el);
    hint.hidden = true;
    panel.hidden = false;
    preview();
    textarea.focus();
  }

  function selectParent() {
    const parent = selected?.parentElement;
    if (parent && parent !== document.body && parent !== document.documentElement) select(parent);
  }

  function preview() {
    const selector = textarea.value.trim();
    let matched = [];
    try {
      matched = selector ? [...document.querySelectorAll(selector)].filter((el) => el !== host) : [];
    } catch {
      count.textContent = 'Not a valid CSS selector.';
      count.classList.add('bad');
      hideButton.disabled = true;
      return;
    }
    const n = matched.length;
    count.classList.toggle('bad', n === 0);
    count.textContent = n === 0 ? 'Matches nothing on this page.' : `Matches ${n} element${n === 1 ? '' : 's'} on this page.`;
    hideButton.disabled = n === 0;
    highlight(matched.includes(selected) ? selected : matched[0]);
  }

  async function onSubmit(event) {
    event.preventDefault();
    hideButton.disabled = true;
    try {
      const response = await chrome.runtime.sendMessage({ type: 'picker:addRule', selector: textarea.value.trim() });
      if (!response?.ok) throw new Error(response?.error ?? 'No response from the extension');
      close();
    } catch (err) {
      count.textContent = `Could not save: ${err.message}`;
      count.classList.add('bad');
      hideButton.disabled = false;
    }
  }

  /* ------------------------------ Events -------------------------------- */

  const fromPicker = (event) => event.composedPath().includes(host);

  function onMove(event) {
    if (!panel.hidden || fromPicker(event)) return;
    hovered = elementAt(event.clientX, event.clientY);
    highlight(hovered);
  }

  function onBlockedEvent(event) {
    if (fromPicker(event)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (event.type !== 'click') return;
    // Clicking the page while the panel is open picks a different element.
    const el = elementAt(event.clientX, event.clientY);
    if (el) select(el);
  }

  function onKey(event) {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopImmediatePropagation();
      close();
    } else if (event.key === 'ArrowUp' && selected && !panel.hidden && !fromPicker(event)) {
      event.preventDefault();
      event.stopImmediatePropagation();
      selectParent();
    }
  }

  // Keep typing and clicking inside the panel from reaching page handlers
  // (e.g. YouTube pausing the video when "k" is typed into the selector box).
  for (const type of [...BLOCKED_EVENTS, 'keydown', 'keyup', 'keypress']) {
    host.addEventListener(type, (event) => event.stopPropagation());
  }

  function onViewportChange() {
    highlight(panel.hidden ? hovered : selected);
  }

  const listeners = [
    ['mousemove', onMove],
    ['keydown', onKey],
    ['scroll', onViewportChange],
    ['resize', onViewportChange],
    ...BLOCKED_EVENTS.map((type) => [type, onBlockedEvent]),
  ];
  listeners.forEach(([type, fn]) => window.addEventListener(type, fn, { capture: true }));

  function close() {
    listeners.forEach(([type, fn]) => window.removeEventListener(type, fn, { capture: true }));
    host.remove();
    delete globalThis.__adblockerPicker;
  }

  /* ------------------------------ Selectors ----------------------------- */

  // Class names and ids generated by CSS-in-JS tools change on every deploy,
  // so a rule built on them would stop working quickly.
  const looksGenerated = (name) =>
    name.length > 30 ||
    /\d{3,}/.test(name) ||
    /^(css|sc|jsx|emotion|styled)-/.test(name) ||
    (/^_?[a-zA-Z0-9]{5,8}$/.test(name) && /\d/.test(name) && /[A-Z]/.test(name));

  const isUnique = (selector) => {
    try {
      return document.querySelectorAll(selector).length === 1;
    } catch {
      return false;
    }
  };

  function selectorFor(target) {
    const parts = [];
    for (let el = target; el && el !== document.documentElement && el !== document.body; el = el.parentElement) {
      if (el.id && !looksGenerated(el.id)) {
        parts.unshift(`#${CSS.escape(el.id)}`);
        if (isUnique(parts.join(' > '))) break;
        continue;
      }

      let part = el.localName;
      const classes = [...el.classList].filter((c) => !looksGenerated(c)).slice(0, 3);
      part += classes.map((c) => `.${CSS.escape(c)}`).join('');

      const parent = el.parentElement;
      if (parent) {
        const siblings = [...parent.children].filter((s) => s.localName === el.localName);
        const lookalikes = siblings.filter((s) => s.matches(part));
        if (lookalikes.length > 1) part += `:nth-of-type(${siblings.indexOf(el) + 1})`;
      }

      parts.unshift(part);
      if (isUnique(parts.join(' > '))) break;
    }
    return parts.join(' > ');
  }
})();
