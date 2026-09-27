'use strict';

// Shared by the Chromium service worker and the Firefox background script.
const api = globalThis.browser ?? globalThis.chrome;
const HOST = 'com.atratech.downloadsolutions';
const CONFIG_MAX_AGE_MS = 60 * 1000;
const HOST_MISSING_RETRY_MS = 60 * 1000;
const REPLY_TIMEOUT_MS = 25 * 1000;
const POLL_ALARM = 'hello';
const POLL_PERIOD_MINUTES = 1;
// Keep under the app's ConnectedWindow (BrowserIntegrationViewModel).
const HEALTHY_POLL_MS = 5 * 60 * 1000;
const IS_FIREFOX = typeof globalThis.browser !== 'undefined' && typeof globalThis.browser.runtime?.getBrowserInfo === 'function';

const inFlight = new Set();

function browserName() {
  if (IS_FIREFOX) return 'Firefox';
  if (navigator.brave) return 'Brave';
  const brands = (navigator.userAgentData?.brands ?? []).map(b => b.brand);
  if (brands.includes('Microsoft Edge')) return 'Edge';
  if (brands.includes('Opera') || brands.includes('Opera GX')) return 'Opera';
  if (/Vivaldi/i.test(navigator.userAgent)) return 'Vivaldi';
  if (brands.includes('Google Chrome')) return 'Chrome';
  return brands.includes('Chromium') ? 'Chromium' : 'Chrome';
}

const BROWSER = browserName();
const EXTENSION_VERSION = api.runtime.getManifest().version;

async function loadState() {
  const { status } = await api.storage.local.get('status');
  return status ?? {};
}

async function saveState(patch) {
  const status = { ...(await loadState()), ...patch };
  await api.storage.local.set({ status });
  return status;
}

function withTimeout(promise, ms) {
  let timer;
  const timeout = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('timeout')), ms); });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

// Sends one request to the native host. Never throws; failures come back as { ok: false }.
async function send(kind, extra = {}) {
  const message = { kind, browser: BROWSER, extensionVersion: EXTENSION_VERSION, ...extra };
  let reply;
  try {
    reply = await withTimeout(api.runtime.sendNativeMessage(HOST, message), REPLY_TIMEOUT_MS);
  } catch (err) {
    const text = String(err?.message ?? err);
    const hostMissing = /not found|no such native application|access to the specified native messaging host is forbidden/i.test(text);
    const timedOut = text === 'timeout';
    await saveState({ hostMissing, notResponding: !hostMissing, checkedAt: Date.now() });
    console.warn(`Download Solutions: ${kind} failed: ${text}`);
    return { ok: false, hostMissing, timedOut, message: timedOut ? "The app didn't answer in time." : text };
  }

  const patch = { hostMissing: false, notResponding: !reply?.ok, checkedAt: Date.now(), appRunning: reply?.appRunning !== false };
  if (reply?.appVersion) patch.appVersion = reply.appVersion;
  if (reply?.config) {
    patch.config = reply.config;
    patch.configAt = Date.now();
  }
  await saveState(patch);
  return reply ?? { ok: false, message: 'Empty reply.' };
}

async function currentConfig() {
  const state = await loadState();
  if (state.hostMissing && Date.now() - (state.checkedAt ?? 0) < HOST_MISSING_RETRY_MS) {
    return { hostMissing: true };
  }

  if (!state.config || Date.now() - (state.configAt ?? 0) > CONFIG_MAX_AGE_MS) {
    const reply = await send('GetConfig');
    if (reply.hostMissing) return { hostMissing: true };
    return { config: reply.config ?? state.config ?? null };
  }

  return { config: state.config };
}

function hostOf(url) {
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.hostname.toLowerCase().replace(/\.$/, '') : null;
  } catch {
    return null;
  }
}

// Mirrors BrowserHandoffPolicy.SiteMatches: "example.com" covers subdomains; "*.example.com" is subdomains only.
function siteMatches(host, pattern) {
  if (!host || !pattern) return false;
  if (pattern.startsWith('*.')) return host.endsWith(pattern.slice(1));
  return host === pattern || host.endsWith('.' + pattern);
}

function isExcluded(config, urls) {
  const sites = config?.excludedSites ?? [];
  return urls.map(hostOf).some(host => host && sites.some(p => siteMatches(host, p)));
}

async function collectCookies(item, urls) {
  const seen = new Map();
  for (const url of new Set(urls.filter(u => hostOf(u)))) {
    const query = { url };
    if (IS_FIREFOX) {
      if (item.cookieStoreId) query.storeId = item.cookieStoreId;
      query.firstPartyDomain = null;
    }

    try {
      for (const c of await api.cookies.getAll(query)) {
        seen.set(`${c.domain}|${c.path}|${c.name}`, {
          name: c.name,
          value: c.value,
          domain: c.domain,
          path: c.path,
          secure: c.secure,
          hostOnly: c.hostOnly,
          expirationDate: c.session ? undefined : c.expirationDate
        });
      }
    } catch (err) {
      console.warn(`Download Solutions: could not read cookies: ${err?.message ?? err}`);
    }
  }

  return [...seen.values()].slice(0, 300);
}

function baseName(path) {
  return path ? path.split(/[\\/]/).pop() : undefined;
}

async function notify(fileName, bytes, url) {
  const size = bytes > 0 ? ` \u00b7 ${(bytes / 1024 / 1024 >= 1024 ? (bytes / 1024 ** 3).toFixed(1) + ' GB' : (bytes / 1024 ** 2).toFixed(1) + ' MB')}` : '';
  try {
    await api.notifications.create({
      type: 'basic',
      iconUrl: api.runtime.getURL('icons/icon-128.png'),
      title: 'Sent to AtraTech Download Solutions',
      message: `${fileName ?? 'Download'}${size}\nFrom ${hostOf(url) ?? 'the web'}`
    });
  } catch (err) {
    console.warn(`Download Solutions: notification failed: ${err?.message ?? err}`);
  }
}

async function onCreated(item) {
  if (item.state !== 'in_progress' || item.incognito || inFlight.has(item.id)) return;
  const url = item.finalUrl || item.url;
  if (!hostOf(url)) return;

  const { config, hostMissing } = await currentConfig();
  if (hostMissing || (config && !config.enabled)) return;
  if (config && isExcluded(config, [url, item.url, item.referrer])) return;
  if (config && item.totalBytes > 0 && item.totalBytes < config.minimumBytes) return;

  inFlight.add(item.id);
  try {
    try {
      await api.downloads.pause(item.id);
    } catch {
      return; // Already finished or not pausable: leave it to the browser.
    }

    const [latest] = await api.downloads.search({ id: item.id });
    const current = latest ?? item;
    const urls = [url, item.url];
    const cookies = config?.useCookies === false ? [] : await collectCookies(current, urls);
    const handoff = {
      url,
      fileName: baseName(current.filename),
      referrer: current.referrer || undefined,
      totalBytes: current.totalBytes > 0 ? current.totalBytes : undefined,
      mimeType: current.mime || undefined,
      userAgent: navigator.userAgent,
      incognito: !!current.incognito,
      cookies
    };

    const reply = await send('Handoff', { handoff });
    const fileName = handoff.fileName ?? baseName(new URL(url).pathname) ?? 'download';
    if (reply.ok && reply.accepted) {
      await api.downloads.cancel(item.id).catch(() => {});
      await api.downloads.erase({ id: item.id }).catch(() => {});
      await saveState({ last: { fileName, sent: true, at: Date.now() } });
      if ((reply.config ?? config)?.notify) await notify(fileName, handoff.totalBytes, url);
      return;
    }

    await api.downloads.resume(item.id).catch(err => console.warn(`Download Solutions: resume failed: ${err?.message ?? err}`));
    const failed = !reply.ok || reply.reason === 'serverRefused' || reply.reason === 'timeout' || reply.reason === 'appNotRunning';
    if (failed) {
      await saveState({ last: { fileName, sent: false, message: reply.message, at: Date.now() } });
    }
  } finally {
    inFlight.delete(item.id);
  }
}

async function hello() {
  await send('Hello');
}

// Checks every minute while the host is missing, not answering, or the app is closed (so the app sees this
// browser soon after it starts); otherwise every HEALTHY_POLL_MS. Any request counts as a check.
async function poll() {
  const state = await loadState();
  const healthy = !state.hostMissing && !state.notResponding && state.appRunning !== false;
  if (healthy && Date.now() - (state.checkedAt ?? 0) < HEALTHY_POLL_MS) return;
  await hello();
}

// Alarms survive worker restarts but not every upgrade path, so re-check on each start.
async function ensurePollAlarm() {
  const existing = await api.alarms.get(POLL_ALARM);
  if (existing?.periodInMinutes !== POLL_PERIOD_MINUTES) {
    await api.alarms.create(POLL_ALARM, { periodInMinutes: POLL_PERIOD_MINUTES });
  }
}

api.downloads.onCreated.addListener(item => { onCreated(item).catch(err => console.error('Download Solutions:', err)); });
api.runtime.onStartup.addListener(() => { hello(); });
api.runtime.onInstalled.addListener(() => { hello(); });
api.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === POLL_ALARM) poll().catch(err => console.error('Download Solutions:', err));
});
ensurePollAlarm().catch(err => console.error('Download Solutions: could not schedule checks:', err));

// Popup and options page requests.
api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== api.runtime.id) return false;
  (async () => {
    switch (message?.cmd) {
      case 'status': {
        const reply = await send('Hello');
        return { reply, state: await loadState() };
      }
      case 'setEnabled':
        return { reply: await send('SetEnabled', { enabled: !!message.enabled }), state: await loadState() };
      case 'setSiteExcluded':
        return { reply: await send('SetSiteExcluded', { site: String(message.site), excluded: !!message.excluded }), state: await loadState() };
      case 'openApp':
        return { reply: await send('OpenApp') };
      case 'openSettings':
        return { reply: await send('OpenSettings') };
      default:
        return { reply: { ok: false, message: 'Unknown command.' } };
    }
  })().then(sendResponse, err => sendResponse({ reply: { ok: false, message: String(err?.message ?? err) } }));
  return true;
});
