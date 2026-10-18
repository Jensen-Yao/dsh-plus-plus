import {
  closeSync,
  openSync,
  readFileSync,
  writeFileSync,
  appendFileSync,
  mkdirSync,
  readdirSync,
  renameSync,
  existsSync,
} from 'node:fs';
import { spawn } from 'node:child_process';
import { join, resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import http from 'node:http';

const normalize = (value) => (process.platform === 'win32' ? resolve(value).toLowerCase() : resolve(value));
const launchedAsHost =
  process.argv[1] && normalize(process.argv[1]) === normalize(fileURLToPath(import.meta.url));
// The same shipped entry serves as supervisor and inherited registration preload.
// Keeping both in one artifact also supports updates by the 3.0 four-file updater.
if (!launchedAsHost) {
  const directory = process.env.DSHPP_RUNTIME_SESSION;
  if (directory && process.argv[1]) {
    const session = JSON.parse(readFileSync(join(directory, 'session.json'), 'utf8'));
    if (normalize(process.argv[1]) === normalize(session.bin)) {
      const stopping = () => existsSync(join(directory, 'stop'));
      if (stopping()) process.exit(0);
      const registration = {
        pid: process.pid,
        started: new Date().toISOString(),
        instance: session.instance,
      };
      const destination = join(directory, process.pid + '.json');
      writeFileSync(destination + '.tmp', JSON.stringify(registration));
      renameSync(destination + '.tmp', destination);
      // Detached restart helpers can outlive an explicit controller stop.
      setInterval(() => {
        if (stopping()) process.exit(0);
      }, 250).unref();
    }
  }
} else {
  const plan = JSON.parse(readFileSync(process.argv[2], 'utf8'));
  const instance = randomUUID();
  const session = join(plan.sessionRoot, instance);
  mkdirSync(session, { recursive: true });
  writeFileSync(join(session, 'session.json'), JSON.stringify({ instance, bin: plan.bin }));
  const register = pathToFileURL(fileURLToPath(import.meta.url)).href;
  const output = openSync(plan.log, 'w');
  const environment = { ...process.env, ...plan.environment, DSHPP_RUNTIME_SESSION: session };
  environment.NODE_OPTIONS = `${environment.NODE_OPTIONS ?? ''} --import="${register}"`;
  const child = spawn(process.execPath, plan.arguments, {
    cwd: plan.directory,
    env: environment,
    detached: true,
    windowsHide: true,
    stdio: ['ignore', output, output],
  });
  const connections = new Set();
  const registrations = new Map();
  let relay,
    timer,
    current,
    unavailableSince,
    terminating = false,
    outputClosed = false;
  const state = {
    hostPid: process.pid,
    bin: plan.bin,
    bind: plan.bind,
    session,
    instance,
    relayReady: false,
  };
  function log(error) {
    appendFileSync(plan.log, '\ndsh++ runtime: ' + error.message + '\n');
  }
  function save() {
    const temporary = plan.state + '.' + process.pid + '.tmp';
    writeFileSync(temporary, JSON.stringify(state));
    renameSync(temporary, plan.state);
  }
  function alive(pid) {
    try {
      process.kill(pid, 0);
      return true;
    } catch (error) {
      if (error.code === 'ESRCH') return false;
      throw error;
    }
  }
  function closeOutput() {
    if (!outputClosed) {
      outputClosed = true;
      closeSync(output);
    }
  }
  function shutdown(code) {
    if (terminating) return;
    terminating = true;
    writeFileSync(join(session, 'stop'), 'stop');
    clearInterval(timer);
    relay?.close();
    for (const socket of connections) socket.destroy();
    for (const entry of registrations.values()) {
      try {
        process.kill(entry.pid);
      } catch (error) {
        if (error.code !== 'ESRCH') log(error);
      }
    }
    // The directly spawned child may not have reached its registration preload yet.
    if (child.exitCode === null && child.signalCode === null) child.kill();
    state.status = code === 0 ? 'stopped' : 'failed';
    state.relayReady = false;
    save();
    process.exitCode = code;
  }
  function scan() {
    if (terminating) return;
    if (readdirSync(session).includes('stop')) {
      shutdown(0);
      return;
    }
    for (const name of readdirSync(session).filter((name) => /^\d+\.json$/.test(name))) {
      if (registrations.has(name)) continue;
      const registration = JSON.parse(readFileSync(join(session, name), 'utf8'));
      if (registration.instance !== instance || !Number.isInteger(registration.pid) || registration.pid <= 0)
        continue;
      registrations.set(name, registration);
      if (
        alive(registration.pid) &&
        (!current || Date.parse(registration.started) > Date.parse(current.started))
      ) {
        current = registration;
        Object.assign(state, registration, { status: 'running' });
        save();
      }
    }
    if (current && alive(current.pid)) {
      unavailableSince = undefined;
      return;
    }
    if (!unavailableSince) {
      unavailableSince = Date.now();
      state.status = 'restarting';
      save();
    }
    if (Date.now() - unavailableSince > plan.restartGraceSeconds * 1000) {
      log(new Error('DSH exited; no registered replacement arrived before the restart deadline.'));
      shutdown(1);
    }
  }
  function available() {
    return !terminating && current && alive(current.pid);
  }
  function forwardHeaders(request) {
    return {
      ...request.headers,
      'x-forwarded-for': request.socket.remoteAddress ?? '',
      'x-forwarded-host': request.headers.host ?? '',
      'x-forwarded-proto': 'http',
    };
  }
  child.once('error', (error) => {
    log(error);
    closeOutput();
    shutdown(1);
  });
  child.once('spawn', () => {
    Object.assign(state, { pid: child.pid, started: new Date().toISOString(), status: 'starting' });
    save();
    closeOutput();
    timer = setInterval(() => {
      try {
        scan();
      } catch (error) {
        log(error);
        shutdown(1);
      }
    }, 200);
    scan();
    if (plan.bind === '127.0.0.1') return;
    relay = http.createServer((request, response) => {
      if (!available()) {
        response.writeHead(503, { 'Retry-After': '2' });
        response.end('DSH is restarting.');
        return;
      }
      const upstream = http.request(
        {
          hostname: '127.0.0.1',
          port: plan.port,
          path: request.url,
          method: request.method,
          headers: forwardHeaders(request),
        },
        (result) => {
          response.writeHead(result.statusCode, result.headers);
          result.pipe(response);
          response.once('close', () => result.destroy());
        },
      );
      upstream.once('error', () => {
        if (!response.headersSent) response.writeHead(502);
        response.end('DSH upstream is unavailable.');
      });
      response.once('close', () => upstream.destroy());
      request.once('aborted', () => upstream.destroy());
      request.pipe(upstream);
    });
    relay.on('connection', (socket) => {
      connections.add(socket);
      socket.once('close', () => connections.delete(socket));
    });
    relay.on('upgrade', (request, client, head) => {
      if (!available()) {
        client.end('HTTP/1.1 503 Restarting\r\nConnection: close\r\n\r\n');
        return;
      }
      const upstream = http.request({
        hostname: '127.0.0.1',
        port: plan.port,
        path: request.url,
        method: request.method,
        headers: forwardHeaders(request),
      });
      upstream.on('upgrade', (response, socket, receivedHead) => {
        client.write(
          'HTTP/1.1 101 Switching Protocols\r\n' +
            response.rawHeaders.reduce(
              (text, value, index, headers) =>
                index % 2 === 0 ? text + value + ': ' + headers[index + 1] + '\r\n' : text,
              '',
            ) +
            '\r\n',
        );
        if (receivedHead.length) client.write(receivedHead);
        if (head.length) socket.write(head);
        socket.pipe(client).pipe(socket);
        client.once('error', () => socket.destroy());
        socket.once('error', () => client.destroy());
        client.once('close', () => socket.destroy());
        socket.once('close', () => client.destroy());
      });
      upstream.on('response', (response) => {
        client.end('HTTP/1.1 ' + response.statusCode + ' Rejected\r\nConnection: close\r\n\r\n');
        response.resume();
      });
      upstream.once('error', () => client.destroy());
      client.once('close', () => upstream.destroy());
      upstream.end();
    });
    relay.once('error', (error) => {
      log(error);
      shutdown(1);
    });
    relay.listen(plan.port, plan.bind, () => {
      state.relayReady = true;
      save();
    });
  });
  process.on('SIGTERM', () => shutdown(0));
  process.on('SIGINT', () => shutdown(0));
}
