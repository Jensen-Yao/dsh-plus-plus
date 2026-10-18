import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { once } from 'node:events';
import http from 'node:http';

const hostScript = fileURLToPath(new URL('../scripts/runtime-host.mjs', import.meta.url));
const registerScript = pathToFileURL(hostScript).href;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
async function until(probe, timeout = 12000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const value = await probe();
    if (value) return value;
    await sleep(50);
  }
  throw new Error('Condition did not become true before deadline.');
}
function request(port, path = '/', headers = {}) {
  return new Promise((resolve) => {
    const call = http.get({ hostname: '127.0.0.2', port, path, headers, agent: false }, (response) => {
      let body = '';
      response.on('data', (chunk) => (body += chunk));
      response.on('end', () => resolve({ code: response.statusCode, body }));
    });
    call.on('error', () => resolve({ code: 0, body: '' }));
    call.setTimeout(1500, () => call.destroy());
  });
}
function alive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}
async function fixture(grace = 5) {
  const directory = mkdtempSync(join(tmpdir(), 'dshpp-runtime-test-'));
  const bin = join(directory, 'fake-dsh.mjs');
  const source = `
    import http from 'node:http';
    import { spawn } from 'node:child_process';
    const server = http.createServer((request, response) => {
      if (request.url === '/restart') {
        const helper = spawn(process.execPath, ['-e',
          'setTimeout(()=>{require("node:child_process").spawn(process.execPath,JSON.parse(process.argv[1]),{env:process.env,detached:true,stdio:"ignore"}).unref()},2500)',
          JSON.stringify(process.argv.slice(1))], { env:process.env,detached:true,stdio:'ignore',windowsHide:true });
        helper.unref();
        response.end('restarting');
        setTimeout(()=>process.exit(0),50);
      } else response.end(JSON.stringify({pid:process.pid,headers:request.headers}));
    });
    server.on('upgrade', (request,socket) => {
      socket.write('HTTP/1.1 101 Switching Protocols\\r\\nUpgrade: test\\r\\nConnection: Upgrade\\r\\n\\r\\n');
      socket.on('data',data=>socket.write(data));
    });
    server.listen(Number(process.argv[2]),'127.0.0.1');
  `;
  writeFileSync(bin, source);
  const probe = http.createServer();
  probe.listen(0, '127.0.0.1');
  await once(probe, 'listening');
  const port = probe.address().port;
  await new Promise((resolve) => probe.close(resolve));
  const plan = {
    bin,
    arguments: [bin, String(port)],
    directory,
    port,
    bind: '127.0.0.2',
    log: join(directory, 'runtime.log'),
    state: join(directory, 'state.json'),
    sessionRoot: join(directory, 'sessions'),
    restartGraceSeconds: grace,
    environment: {},
  };
  writeFileSync(join(directory, 'plan.json'), JSON.stringify(plan));
  const host = spawn(process.execPath, [hostScript, join(directory, 'plan.json')], {
    windowsHide: true,
    stdio: 'ignore',
  });
  const exited = once(host, 'exit');
  const state = () => (existsSync(plan.state) ? JSON.parse(readFileSync(plan.state, 'utf8')) : {});
  await until(async () => (await request(port)).code === 200);
  return {
    plan,
    host,
    exited,
    state,
    port,
    stop: async () => {
      if (host.exitCode !== null) return;
      writeFileSync(join(state().session, 'stop'), 'stop');
      await exited;
    },
  };
}

test(
  'remote relay follows registered DSH replacements and preserves request fencing',
  { timeout: 30000 },
  async (t) => {
    const f = await fixture();
    t.after(() => f.stop());
    const original = f.state().pid;
    const response = await request(f.port, '/', { 'X-Forwarded-For': 'attacker', Host: 'original.example' });
    const echoed = JSON.parse(response.body);
    assert.equal(echoed.headers.host, 'original.example');
    assert.notEqual(echoed.headers['x-forwarded-for'], 'attacker');
    assert.equal(echoed.headers['x-forwarded-host'], 'original.example');
    await t.test('HTTP upgrade remains bidirectional', async () => {
      await new Promise((resolve, reject) => {
        const call = http.request({
          hostname: '127.0.0.2',
          port: f.port,
          headers: { Connection: 'Upgrade', Upgrade: 'test' },
        });
        call.on('upgrade', (_, socket) => {
          socket.on('error', reject);
          socket.once('data', (data) => {
            assert.equal(data.toString(), 'ping');
            socket.destroy();
            resolve();
          });
          socket.write('ping');
        });
        call.on('error', reject);
        call.end();
      });
    });
    await request(f.port, '/restart');
    await until(async () => (await request(f.port)).code === 503);
    await t.test('unregistered listener is not exposed while DSH is absent', async () => {
      let foreign;
      // Windows may release the process handle before releasing its listening socket.
      await until(async () => {
        const candidate = http.createServer((_, response) => response.end('foreign server'));
        const listening = once(candidate, 'listening');
        candidate.listen(f.port, '127.0.0.1');
        try {
          await listening;
          foreign = candidate;
          return true;
        } catch (error) {
          if (error.code !== 'EADDRINUSE') throw error;
          return false;
        }
      }, 1500);
      try {
        assert.equal((await request(f.port)).code, 503);
      } finally {
        await new Promise((resolve) => foreign.close(resolve));
      }
    });
    await until(async () => f.state().pid !== original && (await request(f.port)).code === 200);
    const replacement = f.state().pid;
    assert.notEqual(replacement, original);
    assert.equal(f.state().hostPid, f.host.pid);
    await f.stop();
    await until(() => !alive(replacement));
    assert.equal((await request(f.port)).code, 0);
    await t.test('late replacement cannot resurrect an explicitly stopped session', async () => {
      const late = spawn(process.execPath, [f.plan.bin, String(f.port)], {
        windowsHide: true,
        stdio: 'ignore',
        env: {
          ...process.env,
          DSHPP_RUNTIME_SESSION: f.state().session,
          NODE_OPTIONS: `--import="${registerScript}"`,
        },
      });
      const [code] = await once(late, 'exit');
      assert.equal(code, 0);
      assert.equal((await request(f.port)).code, 0);
    });
  },
);

test('relay releases its port when no replacement registers', { timeout: 15000 }, async (t) => {
  const f = await fixture(1);
  t.after(() => f.stop());
  process.kill(f.state().pid);
  await f.exited;
  assert.equal(f.state().status, 'failed');
  assert.equal((await request(f.port)).code, 0);
});
