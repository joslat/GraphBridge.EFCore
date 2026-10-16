import { existsSync } from 'node:fs';
import { freemem } from 'node:os';
import { resolve } from 'node:path';

export const root = resolve(import.meta.dirname, '../..');
export function requireMemory(gib = 6) {
  const available = freemem() / 2 ** 30;
  if (available < gib) throw new Error(`Verification deferred: ${available.toFixed(2)} GiB available; ${gib} GiB required to preserve the 5 GiB reserve.`);
}
export function executable(variable, defaults) {
  const selected = process.env[variable];
  if (selected) {
    if (!existsSync(selected)) throw new Error(`${variable} does not identify an installed executable.`);
    return selected;
  }
  const found = defaults.find(p => p && existsSync(p));
  if (!found) throw new Error(`Set ${variable} to an installed executable. Tests do not download browsers or editors automatically.`);
  return found;
}
export function chromeExecutable() {
  return executable('GRAPHBRIDGE_CHROME_EXECUTABLE', [
    process.env.ProgramFiles && resolve(process.env.ProgramFiles, 'Google/Chrome/Application/chrome.exe'),
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', '/usr/bin/google-chrome', '/usr/bin/chromium']);
}
export function codeExecutable() {
  return executable('GRAPHBRIDGE_VSCODE_EXECUTABLE', [
    process.env.LOCALAPPDATA && resolve(process.env.LOCALAPPDATA, 'Programs/Microsoft VS Code/Code.exe'),
    '/Applications/Visual Studio Code.app/Contents/MacOS/Electron', '/usr/share/code/code']);
}
