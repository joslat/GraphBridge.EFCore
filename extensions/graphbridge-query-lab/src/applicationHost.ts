import { spawn, type ChildProcess } from 'node:child_process';
import { realpath, stat } from 'node:fs/promises';
import { isAbsolute, relative, resolve, sep } from 'node:path';
import { randomUUID } from 'node:crypto';
import { z } from 'zod';
import { MAX_BYTES, parseEvidence, type SavedEvidence } from './evidence';

export type Operation = 'offline' | 'inspect' | 'semantic';
export interface CaptureRequest {
  formatVersion: 1; requestId: string; operation: Operation;
  includeStatements: boolean; acknowledgeDatabase: boolean; acknowledgeDisposable: boolean;
}
export function request(operation: Operation, includeStatements: boolean): CaptureRequest {
  return { formatVersion: 1, requestId: randomUUID().replaceAll('-', ''), operation, includeStatements,
    acknowledgeDatabase: operation !== 'offline', acknowledgeDisposable: operation === 'semantic' };
}
export async function resolveHost(workspace: string, selected: string): Promise<{ workspace: string; dll: string }> {
  const root = await realpath(workspace);
  const dll = await realpath(resolve(root, selected));
  const path = relative(root, dll);
  if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path) || !dll.toLowerCase().endsWith('.dll'))
    throw new Error('Select a prebuilt application DLL inside the trusted workspace.');
  if (!(await stat(dll)).isFile()) throw new Error('Select a regular application DLL.');
  // Require an executable .NET application, not a provider/library DLL.
  const runtime = dll.slice(0, -4) + '.runtimeconfig.json';
  if (!(await stat(runtime)).isFile()) throw new Error('The application runtime configuration is missing. Build your host first.');
  return { workspace: root, dll };
}
const reply = z.object({ formatVersion: z.literal(1), requestId: z.string().regex(/^[a-f0-9]{32}$/),
  result: z.unknown().optional(), error: z.enum(['invalid-request', 'configuration', 'capture-failed', 'output-limit']).optional() }).strict();
export function parseReply(text: string, expected: CaptureRequest): SavedEvidence {
  if (Buffer.byteLength(text, 'utf8') > MAX_BYTES) throw new Error('Host output exceeded its limit.');
  let raw: unknown;
  try { raw = JSON.parse(text); } catch { throw new Error('Host returned malformed JSON.'); }
  const parsed = reply.safeParse(raw);
  if (!parsed.success || parsed.data.requestId !== expected.requestId ||
      (parsed.data.result !== undefined) === (parsed.data.error !== undefined)) throw new Error('Host protocol or request identity mismatch.');
  if (parsed.data.error) throw new Error(`Host reported ${parsed.data.error}. Check the application configuration and capture requirements.`);
  const saved = parseEvidence(JSON.stringify(parsed.data.result));
  const tier = 'tier' in saved.report ? saved.report.tier : undefined;
  if (tier !== (expected.operation === 'offline' ? 'v0' : expected.operation === 'inspect' ? 'v1' : 'V2'))
    throw new Error('Host returned evidence for a different operation.');
  if (!expected.includeStatements && (saved.evidence?.sensitiveTextIncluded || saved.sources.length > 0))
    throw new Error('Host disclosed statement/source text without the requested opt-in.');
  return saved;
}

function terminate(child: ChildProcess): Promise<void> {
  if (!child.pid || child.exitCode !== null || child.signalCode !== null) return Promise.resolve();
  if (process.platform === 'win32') return new Promise(resolveKill => {
    const killer = spawn(resolve(process.env.SystemRoot ?? 'C:\\Windows', 'System32/taskkill.exe'),
      ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore', shell: false });
    killer.once('error', () => { child.kill(); resolveKill(); });
    killer.once('close', () => resolveKill());
  });
  try { process.kill(-child.pid, 'SIGKILL'); } catch { child.kill('SIGKILL'); }
  return Promise.resolve();
}

/** Trusted application execution, one bounded response. Not a process or database sandbox. */
export async function capture(host: { workspace: string; dll: string }, input: CaptureRequest,
    signal: AbortSignal, timeoutMs = 30_000): Promise<SavedEvidence> {
  if (signal.aborted) throw new Error('Capture cancelled.');
  const text = await new Promise<string>((resolveResult, reject) => {
    const child = spawn('dotnet', [host.dll, '--lab-host'], { cwd: host.workspace, shell: false,
      windowsHide: true, detached: process.platform !== 'win32', stdio: ['pipe', 'pipe', 'pipe'] });
    const chunks: Buffer[] = [];
    let stdout = 0, stderr = 0;
    let failure: string | undefined;
    let cleanup: Promise<void> | undefined;
    const stop = (reason: string) => {
      if (failure) return;
      failure = reason; chunks.length = 0;
      cleanup = terminate(child).finally(() => {
        // Release inherited pipes even if the parent exited before a deadline.
        child.stdin.destroy(); child.stdout.destroy(); child.stderr.destroy();
      });
    };
    const abort = () => stop('Capture cancelled.');
    signal.addEventListener('abort', abort, { once: true });
    const timer = setTimeout(() => stop('Host exceeded the 30-second capture deadline.'), timeoutMs);
    child.stdout.on('data', (chunk: Buffer) => {
      stdout += chunk.length;
      if (stdout > MAX_BYTES) stop('Host output exceeded its limit.');
      if (!failure) chunks.push(chunk);
    });
    child.stderr.on('data', (chunk: Buffer) => { stderr += chunk.length; if (stderr > 16384) stop('Host stderr exceeded its limit.'); });
    child.stdin.on('error', () => stop('Host closed its request stream.'));
    child.once('error', () => { failure = 'Unable to start the application host. Check the .NET installation.'; });
    child.once('close', async code => {
      clearTimeout(timer); signal.removeEventListener('abort', abort);
      await cleanup;
      if (failure || code !== 0) reject(new Error(failure ?? 'Host exited without a valid capture response.'));
      else {
        try { resolveResult(new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks))); }
        catch { reject(new Error('Host returned invalid UTF-8.')); }
      }
    });
    child.stdin.end(JSON.stringify(input));
    if (signal.aborted) abort();
  });
  return parseReply(text, input);
}
