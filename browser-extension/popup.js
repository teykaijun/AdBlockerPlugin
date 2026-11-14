import { get, isNewerVersion } from './api.js';

const $ = (id) => document.getElementById(id);

/** "15:30" later today, or "Sat 08:00" on another day: a pause can last up to a day. */
function clockTime(date) {
  const time = { hour: '2-digit', minute: '2-digit' };
  const today = date.toDateString() === new Date().toDateString();
  return date.toLocaleString([], today ? time : { weekday: 'short', ...time });
}

async function showStatus() {
  const status = $('status');
  try {
    const answer = await get('/status');
    // Some other program could be answering on that port.
    if (answer.app !== 'AdBlocker') throw new Error('Not AdBlocker');
    const { version, pausedUntil } = answer;
    status.dataset.state = 'on';
    status.textContent = `Working with AdBlocker for Windows ${version}.`;
    if (pausedUntil) {
      status.dataset.state = 'paused';
      status.textContent = `AdBlocker is paused until ${clockTime(new Date(pausedUntil))}, so nothing is blocked or closed.`;
    }
    // An unpacked extension cannot update itself, so point at the one in the release.
    const mine = chrome.runtime.getManifest().version;
    if (isNewerVersion(version, mine)) {
      const note = $('newer');
      note.textContent =
        `This companion is ${mine}. Unzip the AdBlocker-Companion.zip from release ${version} over its folder, ` +
        'then press Reload on the extensions page.';
      note.hidden = false;
    }
  } catch {
    status.dataset.state = 'off';
    status.textContent =
      'Can’t reach AdBlocker for Windows (1.3.0 or later), so no tabs are closed. ' +
      '“adblocker status” shows whether it is running; if it is, “adblocker doctor” checks the connection.';
  }
}

async function showStats() {
  const { stats } = await chrome.storage.session.get('stats');
  if (!stats) return;
  $('closed').textContent = stats.closed.toLocaleString();
  $('redirects').textContent = stats.redirects.toLocaleString();
  if (stats.scams > 0) {
    const scams = $('scams');
    scams.textContent = `${stats.scams.toLocaleString()} suspected scam ${stats.scams === 1 ? 'site' : 'sites'} blocked.`;
    scams.hidden = false;
  }
  if (stats.recent.length === 0) return;

  const items = stats.recent.map(({ kind, host, scam, time }) => {
    const item = document.createElement('li');
    if (scam) item.className = 'scam';
    const when = document.createElement('time');
    when.textContent = new Date(time).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    const what = document.createElement('span');
    const did = kind === 'closed' ? 'Closed' : 'Went back from';
    what.textContent = scam ? `Scam site: ${host}` : `${did} ${host}`;
    item.append(when, what);
    return item;
  });
  $('recent').replaceChildren(...items);
}

showStatus();
showStats();
