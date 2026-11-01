// Loads the real Dashboard.config.html in headless Chromium with stubbed Jellyfin objects and checks the
// "Reliability" section: default, saving, the status texts (English and Arabic) and a missing endpoint.
const { chromium } = require('playwright');
const path = require('path');
const pagePath = path.resolve(process.argv[2] || 'Dashboard.config.html');
let failures = 0;
const check = (name, ok, detail) => { console.log((ok ? 'PASS ' : 'FAIL ') + name + (ok ? '' : '  -> ' + detail)); if (!ok) failures++; };

const stubs = function (init) {
    window.__saved = null; window.__processed = false; window.__alerts = []; window.__calls = [];
    window.__config = init.config; window.__service = init.service;
    const ok = v => Promise.resolve(JSON.parse(JSON.stringify(v === undefined ? {} : v)));
    window.ApiClient = {
        getUrl: p => '/' + p,
        getPluginConfiguration: () => ok(window.__config),
        updatePluginConfiguration: (id, c) => { window.__saved = c; window.__config = JSON.parse(JSON.stringify(c)); return ok({}); },
        getJSON: u => {
            window.__calls.push('GET ' + u);
            if (/ServiceStatus/.test(u)) { return window.__service === '404' ? Promise.reject({ status: 404, statusText: 'Not Found' }) : ok(window.__service); }
            return ok({});
        },
        ajax: o => { window.__calls.push(o.type + ' ' + o.url); return ok({}); }
    };
    window.Dashboard = { showLoadingMsg() {}, hideLoadingMsg() {}, alert(o) { window.__alerts.push(o.text); }, processPluginConfigurationUpdateResult() { window.__processed = true; } };
};
async function open(browser, init) {
    const page = await browser.newPage();
    const errors = []; page.on('pageerror', e => errors.push(e.message)); page.__errors = errors;
    await page.addInitScript(stubs, init);
    await page.goto('file://' + pagePath);
    await page.waitForFunction(() => document.querySelector('#protvServiceLine') && document.querySelector('#protvServiceLine').textContent !== '');
    return page;
}
(async () => {
    const browser = await chromium.launch();
    const base = { Username: 'u', ApiBaseUrl: 'http://x:82', StreamAnalyzeDurationMs: 1500, KnownChannels: [], ChannelOverrides: [], UiLanguage: 'en' };
    const okStatus = { state: 'ok', sinceUtc: '2026-10-25T10:00:00Z', message: 'x' };

    let p = await open(browser, { config: base, service: okStatus });
    check('page loads without script errors', p.__errors.length === 0, JSON.stringify(p.__errors));
    check('a settings file without the setting shows it switched on', await p.isChecked('#protvLiveApiResilience'), 'unchecked');
    check('status ok text', (await p.textContent('#protvServiceLine')) === 'PROTV is answering.', await p.textContent('#protvServiceLine'));
    check('every script sits inside the page element', await p.evaluate(() => [...document.querySelectorAll('script')].every(s => s.closest('[data-role="page"]'))), 'outside');
    check('no duplicate element ids', await p.evaluate(() => { const ids = [...document.querySelectorAll('[id]')].map(e => e.id); return new Set(ids).size === ids.length; }), 'dups');
    await p.click('button[type=submit]');
    await p.waitForFunction(() => window.__processed || window.__alerts.length);
    let saved = await p.evaluate(() => window.__saved);
    check('save writes LiveApiResilience = true and keeps the rest', saved && saved.LiveApiResilience === true && saved.Username === 'u' && saved.ApiBaseUrl === 'http://x:82', JSON.stringify(saved));
    check('save accepted without an error alert', await p.evaluate(() => window.__processed && window.__alerts.length === 0), JSON.stringify(await p.evaluate(() => window.__alerts)));
    await p.close();

    p = await open(browser, { config: base, service: okStatus });
    await p.uncheck('#protvLiveApiResilience');
    await p.click('button[type=submit]');
    await p.waitForFunction(() => window.__processed || window.__alerts.length);
    saved = await p.evaluate(() => window.__saved);
    check('switching it off is saved', saved.LiveApiResilience === false, JSON.stringify(saved.LiveApiResilience));
    check('and shown again after the save', !(await p.isChecked('#protvLiveApiResilience')), 'checked'); await p.close();

    p = await open(browser, { config: Object.assign({}, base, { LiveApiResilience: false }), service: okStatus });
    check('a stored false loads unchecked', !(await p.isChecked('#protvLiveApiResilience')), 'checked'); await p.close();

    p = await open(browser, { config: base, service: { state: 'unavailable', sinceUtc: '2026-10-25T10:00:00Z' } });
    let line = await p.textContent('#protvServiceLine'); check('unavailable text', /has not answered since/.test(line) && /paused/.test(line), line); await p.close();
    p = await open(browser, { config: base, service: { state: 'degraded', sinceUtc: '2026-10-25T10:00:00Z' } });
    line = await p.textContent('#protvServiceLine'); check('degraded text', /slow or not answering/.test(line), line); await p.close();

    p = await open(browser, { config: base, service: '404' });
    check('missing endpoint: a calm line and no alert', (await p.textContent('#protvServiceLine')) === 'The PROTV status is not available.' && await p.evaluate(() => window.__alerts.length === 0), 'bad');
    const before = await p.evaluate(() => window.__calls.filter(c => /ServiceStatus/.test(c)).length);
    await p.evaluate(() => { window.__service = { state: 'ok', sinceUtc: '2026-10-25T10:00:00Z' }; });
    await p.click('#protvRefreshServiceStatus');
    await p.waitForFunction(() => document.querySelector('#protvServiceLine').textContent === 'PROTV is answering.');
    check('the button asks again', (await p.evaluate(() => window.__calls.filter(c => /ServiceStatus/.test(c)).length)) === before + 1, 'no request'); await p.close();

    p = await open(browser, { config: Object.assign({}, base, { UiLanguage: 'ar' }), service: { state: 'degraded', sinceUtc: '2026-10-25T10:00:00Z' } });
    await p.waitForTimeout(250);
    const text = await p.evaluate(() => document.querySelector('#protvDashboardPage').textContent);
    check('Arabic: section and button translated', /الموثوقية/.test(text) && /فحص حالة PROTV/.test(text), 'missing');
    check('Arabic: status line translated', /PROTV بطيء أو لا يرد/.test(await p.textContent('#protvServiceLine')), await p.textContent('#protvServiceLine'));
    check('Arabic: page is right-to-left', (await p.getAttribute('#protvDashboardPage', 'dir')) === 'rtl', 'ltr');
    check('Arabic: no script errors', p.__errors.length === 0, JSON.stringify(p.__errors)); await p.close();

    await browser.close();
    console.log(failures ? failures + ' FAILED' : 'ALL PASSED'); process.exit(failures ? 1 : 0);
})();
