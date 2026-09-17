import { get } from './api.js';

const $ = (id) => document.getElementById(id);

async function showStatus() {
  const status = $('status');
  try {
    const { version } = await get('/status');
    status.dataset.state = 'on';
    status.textContent = `Working with AdBlocker for Windows ${version}.`;
  } catch {
    status.dataset.state = 'off';
    status.textContent =
      'Can’t reach AdBlocker for Windows (1.3.0 or later), so no tabs are closed. You can check it with “adblocker status”.';
  }
}

async function showStats() {
  const { stats } = await chrome.storage.session.get('stats');
  if (!stats) return;
  $('closed').textContent = stats.closed.toLocaleString();
  $('redirects').textContent = stats.redirects.toLocaleString();
  if (stats.recent.length === 0) return;

  const items = stats.recent.map(({ kind, host, time }) => {
    const item = document.createElement('li');
    const when = document.createElement('time');
    when.textContent = new Date(time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const what = document.createElement('span');
    what.textContent = `${kind === 'closed' ? 'Closed' : 'Went back from'} ${host}`;
    item.append(when, what);
    return item;
  });
  $('recent').replaceChildren(...items);
}

showStatus();
showStats();
