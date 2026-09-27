'use strict';

// Read-only mirror of the app's settings; the app is the single source of truth.
const api = globalThis.browser ?? globalThis.chrome;
const $ = id => document.getElementById(id);
const onOff = value => (value ? 'On' : 'Off');

function formatMegabytes(bytes) {
  const mb = bytes / (1024 * 1024);
  return `${Number.isInteger(mb) ? mb : mb.toFixed(2)} MB`;
}

async function load() {
  const { reply, state } = (await api.runtime.sendMessage({ cmd: 'status' })) ?? {};
  const status = $('appStatus');
  if (reply?.hostMissing) {
    status.textContent = '\u25CF Not found';
    status.className = 'err';
  } else if (!reply?.ok) {
    status.textContent = '\u25CF Not responding';
    status.className = 'err';
  } else if (reply.appRunning === false) {
    status.textContent = '\u25CF Not running';
    status.className = 'warn';
  } else {
    status.textContent = `\u25CF Connected${reply.appVersion ? ` (${reply.appVersion})` : ''}`;
    status.className = 'ok';
  }

  const config = reply?.config ?? state?.config;
  if (!config) return;

  $('enabled').textContent = onOff(config.enabled);
  $('minimum').textContent = formatMegabytes(config.minimumBytes);
  $('startApp').textContent = config.startAppWhenNotRunning ? 'Start it and take over' : 'Let the browser download';
  $('cookies').textContent = onOff(config.useCookies);
  $('notify').textContent = onOff(config.notify);
  $('excludedCount').textContent = String(config.excludedSites.length);
  const list = $('excludedList');
  list.replaceChildren(...config.excludedSites.map(site => {
    const li = document.createElement('li');
    li.textContent = site;
    return li;
  }));
}

$('openSettings').addEventListener('click', () => api.runtime.sendMessage({ cmd: 'openSettings' }));
load();
