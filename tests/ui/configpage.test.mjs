// Drives the guided setup page end to end in headless Chromium against a stubbed
// Jellyfin dashboard runtime: fresh install → address → A record + live DNS check →
// manual DNS mode → issuance progress → TXT confirmation → success → step 5.
// Run: node tests/ui/configpage.test.mjs  (needs the playwright package + chromium)
import { chromium } from 'playwright';
import { readFileSync } from 'fs';

let html = readFileSync(
  new URL('../../Jellyfin.Plugin.Backporch/Configuration/configPage.html', import.meta.url),
  'utf8');

const browser = await chromium.launch();
const page = await browser.newPage();
const errors = [];
page.on('pageerror', e => errors.push('pageerror: ' + e.message));
page.on('console', m => { if (m.type() === 'error') errors.push('console: ' + m.text()); });

// Stub the Jellyfin dashboard runtime before the page script runs. addInitScript
// does not apply to setContent documents, so splice the stub into the HTML itself.
const stub = () => {
  window.__calls = [];
  window.__saved = null;
  window.__state = {
    config: {
      Domain: '', AccountEmail: '', DnsProvider: 'None', DnsApiToken: '',
      UseStaging: true, Enabled: false, CertificatePath: '',
      RenewDaysBeforeExpiry: 30, DnsPropagationSeconds: 60, DirectoryUrl: ''
    },
    status: {
      LastResult: 'Never run', CertificateExpiryUtc: null, RenewalDue: true,
      UsingStaging: true, Enabled: false, Domain: '', CertificatePath: '/data/backporch/certificate.pfx',
      HasCertificateFile: false, Phase: 'Idle', PhaseDetail: '', Running: false,
      IsTestRun: false, PendingRecordName: null, PendingRecordValue: null
    },
    check: {
      Domain: '', PublicIp: '108.194.46.197', ResolvedAddresses: ['1.2.3.4'],
      DomainMatchesPublicIp: false, ZoneOk: null, ZoneName: null, ZoneError: null
    }
  };
  window.ApiClient = {
    getUrl: (p, params) => '/' + p + (params && params.guided ? '?guided=true' : ''),
    ajax: (opts) => {
      window.__calls.push(opts.type + ' ' + opts.url);
      const s = window.__state;
      if (opts.url.includes('Status')) return Promise.resolve(JSON.parse(JSON.stringify(s.status)));
      if (opts.url.includes('Check')) return Promise.resolve(JSON.parse(JSON.stringify(s.check)));
      if (opts.url.includes('Request')) { return Promise.resolve(JSON.parse(JSON.stringify(s.status))); }
      if (opts.url.includes('ConfirmDns')) return Promise.resolve(JSON.parse(JSON.stringify(s.status)));
      return Promise.reject(new Error('unknown url ' + opts.url));
    },
    getPluginConfiguration: () => Promise.resolve(JSON.parse(JSON.stringify(window.__state.config))),
    updatePluginConfiguration: (id, c) => { window.__saved = c; return Promise.resolve({}); }
  };
  window.Dashboard = {
    showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {}
  };
};

html = html.replace(
  '<script type="text/javascript">',
  '<script>(' + stub.toString() + ')();</script>\n<script type="text/javascript">');
await page.setContent(html, { waitUntil: 'load' });
await page.evaluate(() =>
  document.querySelector('#BackporchConfigPage').dispatchEvent(new Event('pageshow')));
await page.waitForTimeout(300);

const assert = (cond, label) => {
  if (!cond) { errors.push('ASSERT FAILED: ' + label); }
  else { console.log('ok - ' + label); }
};
const hasClass = (sel, cls) => page.$eval(sel, (el, c) => el.classList.contains(c), cls);

// Fresh install: step 1 active, the rest locked.
assert(await hasClass('#bpStep1', 'bp-active'), 'step 1 starts active');
assert(await hasClass('#bpStep2', 'bp-locked'), 'step 2 starts locked');
assert(await hasClass('#bpStep4', 'bp-locked'), 'step 4 starts locked');

// Enter address, continue.
await page.fill('#bpDomain', 'jellyfin.example.com');
await page.fill('#bpEmail', 'bob@example.com');
await page.click('#bpSaveStep1');
await page.waitForTimeout(300);
assert((await page.evaluate(() => window.__saved)).Domain === 'jellyfin.example.com', 'config saved on continue');
assert(await hasClass('#bpStep1', 'bp-done'), 'step 1 done after save');
assert(await hasClass('#bpStep2', 'bp-active'), 'step 2 unlocks');
const aRecord = await page.textContent('#bpARecord');
assert(aRecord.includes('108.194.46.197'), 'A record shows detected public IP');
assert((await page.textContent('#bpDnsCheck')).includes('1.2.3.4'), 'mismatch warning names the wrong IP');
assert((await page.$$('#bpARecord .bp-copy')).length === 2, 'A record has copy buttons');

// DNS now matches after "check again".
await page.evaluate(() => { window.__state.check.DomainMatchesPublicIp = true; });
await page.click('#bpRecheck');
await page.waitForTimeout(300);
assert(await hasClass('#bpStep2', 'bp-done'), 'step 2 done when domain matches');

// Manual DNS mode unlocks step 4.
await page.selectOption('#bpProvider', 'Manual');
await page.waitForTimeout(200);
assert(await hasClass('#bpStep3', 'bp-done'), 'manual mode completes step 3');
assert(await hasClass('#bpStep4', 'bp-active'), 'step 4 unlocks');
assert(await page.isVisible('#bpManualNote'), 'manual explanation shown');

// Cloudflare mode wants a token.
await page.selectOption('#bpProvider', 'Cloudflare');
await page.waitForTimeout(200);
assert(await hasClass('#bpStep3', 'bp-active'), 'cloudflare without token keeps step 3 open');
assert(await page.isVisible('#bpCloudflareFields'), 'token field shown');
await page.selectOption('#bpProvider', 'Manual');
await page.waitForTimeout(200);

// Issue: goes to running with a pending manual record.
await page.evaluate(() => {
  window.__state.status = Object.assign(window.__state.status, {
    Running: true, Phase: 'AwaitingDnsRecord', IsTestRun: true,
    PhaseDetail: 'Add the TXT record shown, then confirm.',
    PendingRecordName: '_acme-challenge.jellyfin.example.com',
    PendingRecordValue: 'digest-abc123'
  });
});
await page.click('#bpIssue');
await page.waitForTimeout(500);
const calls = await page.evaluate(() => window.__calls);
assert(calls.some(c => c === 'POST /Backporch/Request?guided=true'), 'request POSTed with guided=true');
assert(await page.isVisible('#bpManualRecord'), 'manual TXT card visible');
const txt = await page.textContent('#bpTxtRecord');
assert(txt.includes('_acme-challenge.jellyfin.example.com') && txt.includes('digest-abc123'), 'TXT record rendered');
assert((await page.textContent('#bpPhaseText')).includes('practice run'), 'practice run labeled');
assert(!(await page.isDisabled('#bpConfirmDns')), 'confirm button enabled');
assert(await page.isDisabled('#bpIssue'), 'issue button disabled while running');

// Confirm the record.
await page.click('#bpConfirmDns');
await page.waitForTimeout(300);
assert((await page.evaluate(() => window.__calls)).some(c => c === 'POST /Backporch/ConfirmDns'), 'ConfirmDns POSTed');

// Success: banner + step 5.
await page.evaluate(() => {
  const exp = new Date(Date.now() + 89 * 86400000).toISOString();
  window.__state.status = Object.assign(window.__state.status, {
    Running: false, Phase: 'Succeeded', IsTestRun: false, UsingStaging: false,
    PhaseDetail: 'Issued a certificate for jellyfin.example.com. Renewal is automatic.',
    PendingRecordName: null, PendingRecordValue: null,
    HasCertificateFile: true, CertificateExpiryUtc: exp, Domain: 'jellyfin.example.com'
  });
});
await page.waitForTimeout(2600); // next poll tick
assert(await page.isVisible('#bpBanner'), 'success banner shown');
assert((await page.textContent('#bpBanner')).includes('renews automatically'), 'banner text');
assert(await hasClass('#bpStep4', 'bp-done'), 'step 4 done');
assert(await hasClass('#bpStep5', 'bp-active'), 'step 5 active');
assert((await page.textContent('#bpCertPath')).includes('/data/backporch/certificate.pfx'), 'cert path shown');
assert((await page.textContent('#bpFinalUrl')).includes('https://jellyfin.example.com'), 'final URL shown');
assert(!(await page.isVisible('#bpManualRecord')), 'manual card hidden after success');

await browser.close();
if (errors.length) {
  console.error('\nFAILURES:');
  errors.forEach(e => console.error('  ' + e));
  process.exit(1);
}
console.log('\nAll UI assertions passed, no console/page errors.');
