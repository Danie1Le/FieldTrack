// API load test (#47, spec #21 story 93): 50 Operators use the dashboard API for 5 minutes while the simulator
// publishes at demo rate. Pass bar: p95 < 150 ms and < 0.1% failed requests.
//
// Every virtual user signs in through the real endpoint (ADR 0002) as the Demo Operator, then browses with its own
// access token. Credentials come from the environment, never from this file.
//
// Run from the official image on the compose network (NOTES.md, "API load test"):
//   docker run --rm --network <project>_default -v "$PWD/bench:/bench" -w /bench/k6 \
//     -e K6_BASE_URL=http://api:8080 -e K6_EMAIL=... -e K6_PASSWORD=... \
//     grafana/k6:1.4.2 run dashboard.js
import http from 'k6/http';
import { check, sleep } from 'k6';

const BASE = __ENV.K6_BASE_URL || 'http://localhost:8080';
const DAY_MS = 24 * 60 * 60 * 1000;
// Diagnostic only: K6_SKIP_HISTORY=1 drops the 24 h history requests to show what the other endpoints do alone.
// Its thresholds then have no samples, so a run with it is never the pass/fail result.
const SKIP_HISTORY = Boolean(__ENV.K6_SKIP_HISTORY);

export const options = {
  scenarios: {
    operators: { executor: 'constant-vus', vus: Number(__ENV.K6_VUS || 50), duration: __ENV.K6_DURATION || '5m' },
  },
  thresholds: {
    http_req_duration: ['p(95)<150'],
    http_req_failed: ['rate<0.001'],
    checks: ['rate>0.999'],
    'http_req_duration{name:assets-live}': ['p(95)<150'],
    'http_req_duration{name:alerts-active}': ['p(95)<150'],
    'http_req_duration{name:alerts-history}': ['p(95)<150'],
    'http_req_duration{name:readings-24h}': ['p(95)<150'],
  },
};

// Sign-in is a request like any other: its latency and failures count against the bar.
function signIn() {
  const response = http.post(
    `${BASE}/auth/sign-in`,
    JSON.stringify({ email: __ENV.K6_EMAIL, password: __ENV.K6_PASSWORD }),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'sign-in' } });
  check(response, { 'signed in': (r) => r.status === 200 });
  return response.status === 200 ? response.json('accessToken') : null;
}

export function setup() {
  const token = signIn();
  const live = http.get(`${BASE}/assets/live`, { headers: { Authorization: `Bearer ${token}` } });
  const ids = live.json().map((asset) => asset.id);
  if (ids.length === 0) {
    throw new Error('No Assets: seed the fleet before the load test.');
  }
  return { ids };
}

// Module state is per virtual user: one sign-in each, and the 15 minute access token outlives the test.
let token = null;

export default function (data) {
  token ??= signIn();
  const get = (path, name) => check(
    http.get(`${BASE}${path}`, { headers: { Authorization: `Bearer ${token}` }, tags: { name } }),
    { [name]: (r) => r.status === 200 });

  // One dashboard refresh: Assets with Asset Status and latest positions, then the Alert feed.
  get('/assets/live', 'assets-live');
  get('/alerts?active=true', 'alerts-active');
  sleep(1);

  // Now and then the History tab, and one Asset's last 24 hours.
  if (Math.random() < 0.3) {
    get('/alerts?active=false', 'alerts-history');
  }
  if (!SKIP_HISTORY) {
    const assetId = data.ids[Math.floor(Math.random() * data.ids.length)];
    const to = new Date();
    const from = new Date(to.getTime() - DAY_MS);
    get(`/assets/${assetId}/readings?from=${from.toISOString()}&to=${to.toISOString()}`, 'readings-24h');
  }
  sleep(1);
}

export function handleSummary(data) {
  const out = `/bench/results/k6-dashboard-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
  const { http_reqs, http_req_duration, http_req_failed, checks } = data.metrics;
  const text = [
    `requests ${http_reqs.values.count}  p95 ${http_req_duration.values['p(95)'].toFixed(1)} ms`,
    `failed ${(http_req_failed.values.rate * 100).toFixed(3)}%  checks ${(checks.values.rate * 100).toFixed(3)}%`,
    '',
  ].join('\n');
  return { [out]: JSON.stringify(data, null, 2), stdout: text };
}
