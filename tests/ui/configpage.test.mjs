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
      Domain: '', AccountEmail: '', Challenge: 'Http', DnsProvider: 'None', DnsApiToken: '',
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
      Domain: '', PublicIp: '108.194.46.197', HttpPort: 8096, ResolvedAddresses: ['1.2.3.4'],
      DomainMatchesPublicIp: false, ZoneOk: null, ZoneName: null, ZoneError: null,
      ChallengeListenerExpectedPort: 80, ChallengeListenerPort: 80, ChallengeListenerError: null
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
    getPluginConfiguration: () => {
      window.__state.configFetches = (window.__state.configFetches || 0) + 1;
      return Promise.resolve(JSON.parse(JSON.stringify(window.__state.config)));
    },
    // A real server persists what it is sent, so later reads see it. Keeping that
    // faithful matters: the page now re-reads configuration before each save.
    updatePluginConfiguration: (id, c) => {
      window.__saved = c;
      window.__state.config = JSON.parse(JSON.stringify(c));
      return Promise.resolve({});
    }
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
const saved1 = await page.evaluate(() => window.__saved);
assert(saved1.Domain === 'jellyfin.example.com', 'config saved on continue');
assert(saved1.Challenge === 'Http', 'server proof (HTTP-01) is the saved default');
assert(saved1.Enabled === true, 'plugin enabled once the address is in');
assert(await hasClass('#bpStep1', 'bp-done'), 'step 1 done after save');
assert(await hasClass('#bpStep2', 'bp-active'), 'step 2 unlocks');
assert(await hasClass('#bpStep3', 'bp-done'), 'step 3 done by default — nothing to create');
assert(await hasClass('#bpStep4', 'bp-active'), 'step 4 unlocks with zero extra input');
assert(await page.isVisible('#bpHttpFields'), 'server-proof explanation shown');
assert((await page.textContent('#bpHttpPort')) === '8096', 'names the Jellyfin HTTP port not to forward');
const httpFields = await page.textContent('#bpHttpFields');
assert(httpFields.includes("does not expose Jellyfin"), 'the port-80 forward is explained as safe');
assert((await page.textContent('#bpListenerStatus')).includes('Listening on port 80'),
  'listener reported as holding its port');
const aRecord = await page.textContent('#bpARecord');
assert(aRecord.includes('108.194.46.197'), 'A record shows detected public IP');
assert((await page.textContent('#bpDnsCheck')).includes('1.2.3.4'), 'mismatch warning names the wrong IP');
assert((await page.$$('#bpARecord .bp-copy')).length === 2, 'A record has copy buttons');

// DNS now matches after "check again".
await page.evaluate(() => { window.__state.check.DomainMatchesPublicIp = true; });
await page.click('#bpRecheck');
await page.waitForTimeout(300);
assert(await hasClass('#bpStep2', 'bp-done'), 'step 2 done when domain matches');

// Manual DNS fallback also counts as ready.
await page.selectOption('#bpProvider', 'Manual');
await page.waitForTimeout(200);
assert(await hasClass('#bpStep3', 'bp-done'), 'manual mode completes step 3');
assert(await hasClass('#bpStep4', 'bp-active'), 'step 4 stays unlocked');
assert(await page.isVisible('#bpManualNote'), 'manual explanation shown');
assert(!(await page.isVisible('#bpHttpFields')), 'server-proof text hidden in manual mode');

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
const savedManual = await page.evaluate(() => window.__saved);
assert(savedManual.Challenge === 'Dns' && savedManual.DnsProvider === 'Manual', 'manual choice saved as DNS challenge');
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

// After a run finishes, the page must pick the server's writes back up rather than
// keeping the copy it fetched at load — otherwise the next save reverts them.
const afterRun = await page.evaluate(() => window.__state.configFetches);
assert(afterRun > 1, 'config refetched after the run finished');

// Regression: the server owns some of these fields. A later save must not post the
// stale page-load snapshot back over them (that once re-enabled staging and wiped the
// ACME account key, which would swap a real certificate for an untrusted one).
await page.evaluate(() => {
  window.__state.config = Object.assign({}, window.__state.config, {
    AccountKeyPem: '-----BEGIN EC PRIVATE KEY-----',
    UseStaging: false,
    CertificateExpiryUtc: '2026-12-01T00:00:00Z'
  });
});
await page.evaluate(() => { document.querySelector('details.bp-advanced').open = true; });
await page.click('#bpSaveAdvanced');
await page.waitForTimeout(300);
const savedLater = await page.evaluate(() => window.__saved);
assert(savedLater.AccountKeyPem === '-----BEGIN EC PRIVATE KEY-----', 'save preserves the server-written account key');
assert(savedLater.UseStaging === false, 'save does not re-enable staging behind the user');
assert(savedLater.CertificateExpiryUtc === '2026-12-01T00:00:00Z', 'save preserves the recorded expiry');

// A base URL redirects the proof path away from the CA: warn instead of failing later.
await page.evaluate(() => {
  window.__state.check = Object.assign({}, window.__state.check, { BaseUrl: '/jellyfin' });
});
await page.selectOption('#bpProvider', 'Http');
await page.click('#bpRecheck');
await page.waitForTimeout(400);
assert(await page.isVisible('#bpBaseUrlWarning'), 'base URL warning shown for HTTP proof');
const warnText = await page.textContent('#bpBaseUrlWarning');
assert(warnText.includes('/jellyfin'), 'warning names the configured base URL');

// A port that failed to bind is silent until a renewal fails months later, so the page
// has to say it out loud, with the reason.
await page.evaluate(() => {
  window.__state.check = Object.assign({}, window.__state.check, {
    ChallengeListenerPort: 0,
    ChallengeListenerExpectedPort: 80,
    ChallengeListenerError: 'Port 80 is privileged, and this server is not running as root.'
  });
});
await page.click('#bpRecheck');
await page.waitForTimeout(400);
const listenText = await page.textContent('#bpListenerStatus');
assert(listenText.includes('is not open'), 'a failed bind is reported');
assert(listenText.includes('privileged'), 'the failed bind explains itself');

// The HTTP-port settings must round-trip, including a deliberate zero.
await page.evaluate(() => { document.querySelector('details.bp-advanced').open = true; });
await page.fill('#bpListenPortInput', '8080');
await page.fill('#bpHttpsPortInput', '8920');
await page.fill('#bpHstsDays', '0');
await page.click('#bpSaveAdvanced');
await page.waitForTimeout(300);
const advanced = await page.evaluate(() => window.__state.config);
assert(advanced.ChallengeListenPort === 8080, 'listen port saved');
assert(advanced.PublicHttpsPort === 8920, 'public HTTPS port saved');
assert(advanced.HstsMaxAgeDays === 0, 'a deliberate zero is not replaced by the default');
assert(advanced.ServeHttpRedirect === true, 'the listener stays on by default');

await browser.close();
if (errors.length) {
  console.error('\nFAILURES:');
  errors.forEach(e => console.error('  ' + e));
  process.exit(1);
}
console.log('\nAll UI assertions passed, no console/page errors.');
