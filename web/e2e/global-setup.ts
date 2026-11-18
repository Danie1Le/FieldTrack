import { execFileSync } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

/** The Asset whose Device suffers the scripted Overheat, from the demo fleet. */
export const faultyAsset = 'Haul truck 1';

/** The Threshold rule the Overheat breaches. */
export const ruleName = 'Smoke: engine overheat';

const repo = resolve(__dirname, '../..');
const api = process.env['E2E_API_URL'] ?? 'http://localhost:8080';
const web = process.env['E2E_BASE_URL'] ?? 'http://localhost:8081';

/**
 * Prepares a fresh compose stack for the smoke test: waits until the API and the web server answer, replaces the
 * first Admin's temporary password, seeds the demo fleet, adds a Threshold rule on {@link faultyAsset}, then runs the
 * simulator with an Overheat Fault that breaches it. The run stops at the Overheat's peak, so the Alert stays Active.
 *
 * Needs dotnet (the simulator) and, as for compose: FIELDTRACK_ADMIN_EMAIL, FIELDTRACK_ADMIN_PASSWORD (the temporary
 * password the API seeded) and E2E_ADMIN_PASSWORD (the one replacing it). E2E_API_URL, E2E_BASE_URL and E2E_MQTT_PORT
 * override compose's ports. The stack must be fresh: the temporary Admin password works only once.
 */
export default async function globalSetup(): Promise<void> {
  await waitUntilOk(`${api}/health`);
  await waitUntilOk(web);

  const email = required('FIELDTRACK_ADMIN_EMAIL');
  const password = required('E2E_ADMIN_PASSWORD');
  const { accessToken } = await send<{ accessToken: string }>('POST', '/auth/change-password', undefined, {
    email,
    currentPassword: required('FIELDTRACK_ADMIN_PASSWORD'),
    newPassword: password,
  });

  const fleet = join(repo, 'src/FieldTrack.Simulator/fleets/demo.json');
  simulator(['seed', '--fleet', fleet, '--api', api], password);

  const assets = await send<{ id: string; name: string }[]>('GET', '/assets', accessToken);
  const asset = assets.find(a => a.name === faultyAsset) ?? fail(`Seeding made no Asset named ${faultyAsset}.`);
  await send('POST', '/alert-rules', accessToken, {
    name: ruleName,
    kind: 'Threshold',
    assetId: asset.id,
    severity: 'Critical',
    sensor: 'EngineTempC',
    operator: 'GreaterThan',
    threshold: 100,
    hysteresis: 5,
  });

  // The Overheat ramps from 85 °C to 120 °C over its first 24 s: past 100 °C after ~10 s, still at its peak at 30 s.
  const mqttPort = process.env['E2E_MQTT_PORT'] ?? '1883';
  const scenario = join(__dirname, 'overheat-scenario.json');
  simulator(['run', '--fleet', fleet, '--scenario', scenario, '--rate', '10', '--duration', '30', '--mqtt-port', mqttPort], password);
}

/** Polls until the URL answers 2xx: containers report started well before they serve. */
async function waitUntilOk(url: string): Promise<void> {
  const deadline = Date.now() + 3 * 60_000;
  for (;;) {
    try {
      if ((await fetch(url, { signal: AbortSignal.timeout(5_000) })).ok) {
        return;
      }
    } catch {
      // Not listening yet.
    }
    if (Date.now() > deadline) {
      fail(`${url} did not answer 2xx within 3 minutes.`);
    }
    await new Promise(done => setTimeout(done, 1_000));
  }
}

async function send<T = unknown>(method: string, path: string, token?: string, body?: unknown): Promise<T> {
  const response = await fetch(`${api}${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(30_000),
  });
  if (!response.ok) {
    fail(`${method} ${path}: ${response.status} ${await response.text()}`);
  }
  return (await response.json()) as T;
}

/** Runs the simulator in a fresh working directory, so no Boot state from an earlier run leaks in. */
function simulator(args: string[], adminPassword: string): void {
  execFileSync('dotnet', ['run', '--project', join(repo, 'src/FieldTrack.Simulator'), '--', ...args], {
    cwd: mkdtempSync(join(tmpdir(), 'fieldtrack-smoke-')),
    env: { ...process.env, FIELDTRACK_ADMIN_PASSWORD: adminPassword },
    stdio: 'inherit',
    timeout: 5 * 60_000,
  });
}

function required(name: string): string {
  return process.env[name] || fail(`Set ${name}.`);
}

function fail(message: string): never {
  throw new Error(message);
}
