'use strict';

const api = globalThis.browser ?? globalThis.chrome;
const GET_APP_URL = 'https://github.com/Erebus671/Download-Manager#browser-extension';
const $ = id => document.getElementById(id);

let site = null;
let config = null;

function hostOf(url) {
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.hostname.toLowerCase() : null;
  } catch {
    return null;
  }
}

function siteMatches(host, pattern) {
  if (pattern.startsWith('*.')) return host.endsWith(pattern.slice(1));
  return host === pattern || host.endsWith('.' + pattern);
}

function setStatus(text, cls) {
  const el = $('appStatus');
  el.textContent = '\u25CF ' + text;
  el.className = cls;
}

function render({ reply, state }) {
  const missing = reply?.hostMissing || state?.hostMissing;
  config = reply?.config ?? state?.config ?? null;

  $('missingPanel').hidden = !missing;
  $('connectedPanel').hidden = !!missing;
  $('getApp').hidden = !missing;
  $('openApp').hidden = !!missing;
  $('openSettings').hidden = !!missing;

  const last = state?.last;
  const lastFailed = last && !last.sent;
  $('retry').hidden = !(missing || !reply?.ok || lastFailed);

  if (missing) {
    setStatus('Not found', 'err');
    return;
  }

  if (!reply?.ok) {
    setStatus('Not responding', 'err');
  } else if (reply.appRunning === false) {
    setStatus('Not running', 'warn');
  } else {
    setStatus(`Connected${reply.appVersion ? ` (${reply.appVersion})` : ''}`, 'ok');
  }

  const enabled = !!config?.enabled;
  $('enabledToggle').checked = enabled;
  $('enabledToggle').disabled = !config;

  const excluded = !!site && (config?.excludedSites ?? []).some(p => siteMatches(site, p));
  $('siteRow').hidden = !site;
  $('siteName').textContent = site ?? '';
  $('siteToggle').checked = !excluded;
  $('siteToggle').disabled = !enabled;

  const notice = $('notice');
  notice.hidden = true;
  if (!enabled && config) {
    notice.textContent = 'Off. Turn it on here or in the app.';
    notice.hidden = false;
  } else if (excluded) {
    notice.textContent = "This site's downloads stay in the browser.";
    notice.hidden = false;
  } else if (reply.appRunning === false && reply.ok) {
    notice.textContent = config?.startAppWhenNotRunning
      ? 'The app starts when a large download begins.'
      : "Large downloads stay in the browser while the app isn't running.";
    notice.hidden = false;
  }

  $('lastRow').hidden = !last;
  $('lastMessage').hidden = true;
  if (last) {
    $('lastName').textContent = last.fileName;
    $('lastStatus').textContent = last.sent ? 'Sent' : 'Browser kept it';
    $('lastStatus').className = last.sent ? 'ok' : 'warn';
    if (!last.sent && last.message) {
      $('lastMessage').textContent = `${last.message} The browser downloaded the file instead.`;
      $('lastMessage').hidden = false;
    }
  }

  if (reply.message && !reply.ok) {
    $('lastMessage').textContent = reply.message;
    $('lastMessage').hidden = false;
  }
}

async function request(message) {
  const result = await api.runtime.sendMessage(message);
  if (result?.state || message.cmd === 'status') render(result ?? {});
  return result;
}

async function refresh() {
  setStatus('Checking...', 'dim');
  await request({ cmd: 'status' });
}

async function init() {
  try {
    const [tab] = await api.tabs.query({ active: true, currentWindow: true });
    site = hostOf(tab?.url);
  } catch {
    site = null;
  }

  $('enabledToggle').addEventListener('change', e => request({ cmd: 'setEnabled', enabled: e.target.checked }));
  $('siteToggle').addEventListener('change', e => request({ cmd: 'setSiteExcluded', site, excluded: !e.target.checked }));
  $('openApp').addEventListener('click', () => { request({ cmd: 'openApp' }); window.close(); });
  $('openSettings').addEventListener('click', () => { request({ cmd: 'openSettings' }); window.close(); });
  $('getApp').addEventListener('click', () => { api.tabs.create({ url: GET_APP_URL }); window.close(); });
  $('retry').addEventListener('click', refresh);

  await refresh();
}

init();
