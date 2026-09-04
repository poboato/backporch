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
    },
    discovery: {
      Domain: '', Endpoint: '/var/run/docker.sock', Problem: null,
      Apps: [
        { Container: 'sonarr', Image: 'linuxserver/sonarr', Port: 8989, AlternatePorts: [],
          Label: 'sonarr', Hostname: '', Risk: 'Ordinary', RiskReason: '' },
        { Container: 'homepage', Image: 'gethomepage/homepage', Port: 80, AlternatePorts: [3000],
          Label: 'homepage', Hostname: '', Risk: 'Ordinary', RiskReason: '' },
        { Container: 'portainer', Image: 'portainer/portainer-ce', Port: 9000, AlternatePorts: [],
          Label: 'portainer', Hostname: '', Risk: 'Sensitive',
          RiskReason: 'it can start, stop and reconfigure every container' },
        { Container: 'jellyfin', Image: 'jellyfin/jellyfin', Port: 8096, AlternatePorts: [],
          Label: 'jellyfin', Hostname: '', Risk: 'Ordinary', RiskReason: '', IsThisServer: true }
      ]
    }
  };
  window.ApiClient = {
    getUrl: (p, params) => '/' + p + (params && params.guided ? '?guided=true' : ''),
    ajax: (opts) => {
      window.__calls.push(opts.type + ' ' + opts.url);
      const s = window.__state;
      if (opts.url.includes('Status')) return Promise.resolve(JSON.parse(JSON.stringify(s.status)));
      if (opts.url.includes('Check')) return Promise.resolve(JSON.parse(JSON.stringify(s.check)));
      if (opts.url.includes('Discover')) return Promise.resolve(JSON.parse(JSON.stringify(s.discovery)));
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

// The PEM copies are what makes one certificate usable by a proxy in front of
// everything else, so their paths must survive a save.
await page.fill('#bpPemCert', '/etc/ssl/backporch/fullchain.pem');
await page.fill('#bpPemKey', '/etc/ssl/backporch/privkey.pem');
await page.click('#bpSaveAdvanced');
await page.waitForTimeout(300);
const pem = await page.evaluate(() => window.__state.config);
assert(pem.PemCertificatePath === '/etc/ssl/backporch/fullchain.pem', 'PEM chain path saved');
assert(pem.PemPrivateKeyPath === '/etc/ssl/backporch/privkey.pem', 'PEM key path saved');

// Extra names: entered one per line, saved as a list, and blank lines dropped.
await page.fill('#bpExtraDomains', 'home.example.com\n\n  sonarr.example.com  \n');
await page.click('#bpSaveStep1');
await page.waitForTimeout(300);
const names = await page.evaluate(() => window.__saved);
assert(Array.isArray(names.ExtraDomains), 'extra names saved as a list');
assert(names.ExtraDomains.length === 2, 'blank lines are dropped, got ' + JSON.stringify(names.ExtraDomains));
assert(names.ExtraDomains[1] === 'sonarr.example.com', 'names are trimmed');
assert(names.Domain === 'jellyfin.example.com', 'the primary name is unchanged');

// Discovery: the machine's own applications, offered by name.
await page.click('#bpDiscover');
await page.waitForTimeout(300);
const discoveryText = await page.textContent('#bpDiscoverResult');
assert(discoveryText.includes('sonarr.jellyfin.example.com'),
  'a discovered app is offered as a name under the domain');
assert(discoveryText.includes('portainer.jellyfin.example.com'),
  'a sensitive app is still listed');
assert(discoveryText.includes('think twice'),
  'a sensitive app is flagged');
assert(discoveryText.includes('reconfigure every container'),
  'the flag explains itself');
assert(discoveryText.includes('also 3000'),
  'an alternate port is shown rather than silently chosen');
assert(!discoveryText.includes('jellyfin.jellyfin.example.com'),
  'this server is not offered a name under itself');
assert(discoveryText.includes('already covered by your address'),
  'this server is shown as already covered');

// Ticking one adds exactly that name; the ones typed by hand survive.
await page.evaluate(() => {
  document.querySelector('#bpExtraDomains').value = 'typed-by-hand.example.com';
});
const ticks = await page.$$('#bpDiscoverResult .bp-app-tick');
await ticks[0].check();
await page.waitForTimeout(100);
let namesBox = await page.inputValue('#bpExtraDomains');
assert(namesBox.includes('typed-by-hand.example.com'), 'a hand-typed name is not disturbed');
assert(namesBox.includes('sonarr.jellyfin.example.com'), 'ticking adds the name');

// Unticking removes only that one.
await ticks[0].uncheck();
await page.waitForTimeout(100);
namesBox = await page.inputValue('#bpExtraDomains');
assert(!namesBox.includes('sonarr.jellyfin.example.com'), 'unticking removes the name');
assert(namesBox.includes('typed-by-hand.example.com'), 'unticking leaves the rest alone');

// Ticking twice must not add the name twice - a repeated identifier fails the order.
await ticks[0].check();
await page.waitForTimeout(100);
await page.evaluate(() => {
  const box = document.querySelector('#bpExtraDomains');
  box.value = box.value + '\nsonarr.jellyfin.example.com';
});
await ticks[0].uncheck();
await ticks[0].check();
await page.waitForTimeout(100);
namesBox = await page.inputValue('#bpExtraDomains');
const occurrences = namesBox.split('\n').filter(n => n.trim() === 'sonarr.jellyfin.example.com').length;
assert(occurrences === 1, 'a name is never listed twice, got ' + occurrences);

// Re-entering the page is how the browser gets a fresh look at the server: pageshow
// re-reads the configuration and the status and re-renders from them. Used below to
// put the page in states the poll would otherwise have to be waited out for.
const refresh = async () => {
  await page.evaluate(() =>
    document.querySelector('#BackporchConfigPage').dispatchEvent(new Event('pageshow')));
  await page.waitForTimeout(300);
};

// A failed run is the commonest thing that happens in setup, and the page is the only
// place the reason is ever shown — the CA's own message lives on the server otherwise.
// Failing quietly, or leaving the button dead, would strand the user with no next move.
await page.evaluate(() => {
  window.__state.status = Object.assign({}, window.__state.status, {
    Running: false, Phase: 'Failed', IsTestRun: false, UsingStaging: true,
    PhaseDetail: 'Failed — The certificate authority could not validate the challenge '
      + '— Fetching http://jellyfin.example.com/.well-known/acme-challenge/xyz: Timeout during connect.',
    HasCertificateFile: false, CertificateExpiryUtc: null,
    PendingRecordName: null, PendingRecordValue: null
  });
});
await refresh();
const failedText = await page.textContent('#bpIssueResult');
assert(failedText.includes('Timeout during connect'), 'a failed run shows the reason it failed');
assert(failedText.includes('try again'), 'a failed run says what to do next');
assert(!(await page.isDisabled('#bpIssue')), 'the certificate button works again after a failure');
assert(!(await page.isVisible('#bpProgress')), 'the spinner stops on failure');
assert(!(await page.isVisible('#bpBanner')), 'no success banner after a failure');
assert(await hasClass('#bpStep5', 'bp-locked'), 'step 5 stays locked after a failure');

// A staging certificate is signed by a root no browser trusts. Counting one as done would
// walk the user through pointing Jellyfin at it — and every device would then refuse the
// server, with the page insisting it had succeeded.
await page.evaluate(() => {
  window.__state.status = Object.assign({}, window.__state.status, {
    Running: false, Phase: 'Succeeded', IsTestRun: false, UsingStaging: true,
    PhaseDetail: 'Issued a certificate for jellyfin.example.com. Staging certificate — not trusted by browsers.',
    HasCertificateFile: true, Domain: 'jellyfin.example.com',
    CertificateExpiryUtc: new Date(Date.now() + 89 * 86400000).toISOString()
  });
});
await refresh();
assert(!(await page.isVisible('#bpBanner')), 'a staging certificate is not announced as the real thing');
assert(await hasClass('#bpStep5', 'bp-locked'), 'step 5 stays locked on a staging certificate');
assert(await hasClass('#bpStep4', 'bp-active'), 'step 4 stays open so the real certificate can still be got');

// The record stays on screen while the CA checks it, but the run is no longer waiting on
// anyone. A live button there sends a confirmation the server answers with 409, which the
// page would show as a failure for a run that is going perfectly well.
await page.evaluate(() => {
  window.__state.status = Object.assign({}, window.__state.status, {
    Running: true, Phase: 'Validating', IsTestRun: false,
    PhaseDetail: 'The certificate authority is checking the record…',
    HasCertificateFile: false, CertificateExpiryUtc: null,
    PendingRecordName: '_acme-challenge.jellyfin.example.com',
    PendingRecordValue: 'digest-two'
  });
});
await refresh();
assert(await page.isVisible('#bpManualRecord'), 'the record stays visible while the CA checks it');
assert(await page.isDisabled('#bpConfirmDns'),
  'confirm is disabled once the run has stopped waiting for the record');
await page.evaluate(() => {
  window.__state.status = Object.assign({}, window.__state.status, {
    Running: false, Phase: 'Idle', PendingRecordName: null, PendingRecordValue: null
  });
});
await refresh();

// The Advanced escape hatch exists to issue once without the practice run — for a setup
// already proven, or a CA of one's own. Posting guided=true would make it a duplicate of
// the main button, and the only way past the rehearsal would silently not exist.
await page.evaluate(() => {
  document.querySelector('details.bp-advanced').open = true;
  window.__calls = [];
});
await page.click('#bpRequestRaw');
await page.waitForTimeout(300);
const rawCalls = await page.evaluate(() => window.__calls);
assert(rawCalls.some(c => c === 'POST /Backporch/Request'), 'the raw request is POSTed');
assert(!rawCalls.some(c => c.includes('guided=true')), 'the raw request does not ask for the practice run');

// The other half of the "do not re-enable staging behind the user" rule: when the user
// does move the checkbox it has to count, or there is no way back to staging after a real
// issuance and the box is decoration.
await page.check('#bpUseStaging');
await page.click('#bpSaveAdvanced');
await page.waitForTimeout(300);
assert((await page.evaluate(() => window.__saved)).UseStaging === true,
  'ticking the staging box is honoured');
await page.uncheck('#bpUseStaging');
await page.click('#bpSaveAdvanced');
await page.waitForTimeout(300);

// Discovery is re-read from scratch each time it is run, while the tick state lives only
// in the names box. Coming back unticked would invite the user to add a name they already
// have — and a repeated identifier makes the CA reject the whole order.
await page.evaluate(() => {
  document.querySelector('#bpExtraDomains').value = 'sonarr.jellyfin.example.com';
});
await page.click('#bpDiscover');
await page.waitForTimeout(300);
const reTicks = await page.$$('#bpDiscoverResult .bp-app-tick');
assert(await reTicks[0].isChecked(), 'a name already on the certificate comes back ticked');
assert(!(await reTicks[1].isChecked()), 'a name that was never added comes back unticked');

// Docker being unreadable is ordinary — no permission, or no Docker. The endpoint answers
// 200 with an explanation rather than an error, so a page that only rendered Apps would
// show an empty panel and the user would conclude nothing else is running.
await page.evaluate(() => {
  window.__state.discovery.Problem = 'Could not read the container list from '
    + '/var/run/docker.sock. Add the names by hand, or point this at a Docker socket '
    + 'under Advanced.';
});
await page.click('#bpDiscover');
await page.waitForTimeout(300);
const problemText = await page.textContent('#bpDiscoverResult');
assert(problemText.includes('Could not read the container list'), 'discovery reports why it could not run');
assert(problemText.includes('Add the names by hand'), 'the report says what to do instead');

// Every offered name is built under the domain in the box, so without one there is nothing
// to offer — say that rather than listing bare labels that would resolve nowhere.
await page.evaluate(() => { window.__state.discovery.Problem = null; });
await page.fill('#bpDomain', '');
await page.click('#bpDiscover');
await page.waitForTimeout(300);
assert((await page.textContent('#bpDiscoverResult')).includes('Enter your domain above first'),
  'discovery asks for the domain before offering names under it');
await page.fill('#bpDomain', 'jellyfin.example.com');

// Jellyfin persists this configuration as XML and hands enums back as numbers as readily
// as names. Read as a number and not recognised, a Cloudflare setup would show as the HTTP
// default — and the next save would write that default straight over the user's choice.
await page.evaluate(() => {
  window.__state.config = Object.assign({}, window.__state.config, {
    Challenge: 1, DnsProvider: 1, DnsApiToken: 'cf-token-value'
  });
});
await refresh();
assert((await page.inputValue('#bpProvider')) === 'Cloudflare',
  'a numeric enum still reads as the Cloudflare proof');
assert(await page.isVisible('#bpCloudflareFields'), 'the token field is shown for it');
await page.click('#bpSaveStep1');
await page.waitForTimeout(300);
const numeric = await page.evaluate(() => window.__saved);
assert(numeric.Challenge === 'Dns' && numeric.DnsProvider === 'Cloudflare',
  'saving does not quietly revert a numerically-encoded DNS setup to the HTTP default');

// Cloudflare with the token removed cannot succeed, so step 4 has to close again rather
// than leave a button that spends a request on a certain failure.
await page.fill('#bpToken', '');
await page.waitForTimeout(200);
assert(await hasClass('#bpStep3', 'bp-active'), 'step 3 reopens when the token is removed');
assert(await hasClass('#bpStep4', 'bp-locked'), 'step 4 locks again without a token');
assert(!(await page.isVisible('#bpIssue')), 'the certificate button is out of reach while the proof cannot work');

// Enabled is what lets the nightly renewal task run at all. Switching it on with half an
// address would make that task fail every night on a validation error, and leave Let's
// Encrypt with nowhere to send the warning when renewal breaks for real.
await page.evaluate(() => {
  window.__state.config = {
    Domain: '', AccountEmail: '', Challenge: 'Http', DnsProvider: 'None', DnsApiToken: '',
    UseStaging: true, Enabled: false, CertificatePath: '', ExtraDomains: [],
    RenewDaysBeforeExpiry: 30, DnsPropagationSeconds: 60, DirectoryUrl: ''
  };
  window.__state.status = Object.assign({}, window.__state.status, {
    Running: false, Phase: 'Idle', PhaseDetail: '', LastResult: 'Never run', Domain: '',
    UsingStaging: true, HasCertificateFile: false, CertificateExpiryUtc: null
  });
});
await refresh();
await page.fill('#bpDomain', 'jellyfin.example.com');
await page.fill('#bpEmail', '');
await page.click('#bpSaveStep1');
await page.waitForTimeout(300);
const halfAddress = await page.evaluate(() => window.__saved);
assert(halfAddress.Enabled !== true, 'a half-filled address does not switch the plugin on');
assert(await hasClass('#bpStep1', 'bp-active'), 'step 1 stays open until the address is complete');
assert(await hasClass('#bpStep2', 'bp-locked'), 'step 2 stays locked without an email');
assert(await hasClass('#bpStep4', 'bp-locked'), 'step 4 stays locked without an email');

// A failure to detect this server's own address is not evidence about the user's DNS.
// Step 2 only re-renders once the address is complete, so restore that first.
await page.fill('#bpDomain', 'jellyfin.example.com');
await page.fill('#bpEmail', 'bob@example.com');
await page.click('#bpSaveStep1');
await page.waitForTimeout(300);
await page.evaluate(() => {
  window.__state.check.PublicIp = null;
  window.__state.check.DomainMatchesPublicIp = null;
  window.__state.check.ResolvedAddresses = ['203.0.113.9'];
});
await page.evaluate(() =>
  document.querySelector('#BackporchConfigPage').dispatchEvent(new Event('pageshow')));
await page.waitForTimeout(400);
const dnsLine = await page.textContent('#bpDnsCheck');
assert(!dnsLine.includes('not this server yet'),
  'an undetected public IP is not reported as a DNS mismatch');
assert(dnsLine.includes('could not be detected'),
  'the page says the check could not be made');
assert(dnsLine.includes('203.0.113.9'),
  'what the domain does resolve to is still shown');

await browser.close();
if (errors.length) {
  console.error('\nFAILURES:');
  errors.forEach(e => console.error('  ' + e));
  process.exit(1);
}
console.log('\nAll UI assertions passed, no console/page errors.');
