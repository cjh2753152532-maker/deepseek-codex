import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const source = dirname(fileURLToPath(import.meta.url));
const codex = process.argv[2];
assert(codex && existsSync(codex), 'Usage: node source/Verify.mjs C:\path\to\codex.exe');
const temp = mkdtempSync(join(tmpdir(), 'DeepSeek 插件验证 '));
const env = { ...process.env, CODEX_HOME: join(temp, 'codex-profile') };
mkdirSync(env.CODEX_HOME);
const compiler = join(process.env.WINDIR, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const probe = join(temp, 'Verify.exe');
const installer = join(source, '..', 'DeepSeekForCodex.Setup.exe');

function run(program, args) {
  const result = spawnSync(program, args, { cwd: source, env, windowsHide: true, timeout: 50000, stdio: 'inherit' });
  if (result.status !== 0) throw result.error ?? new Error(program + ' exited with ' + result.status);
}

run(compiler, ['/nologo', '/utf8output', '/codepage:65001', '/target:exe', '/main:Verify', '/out:' + probe,
  '/reference:System.Security.dll', '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll',
  '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', 'DeepSeekApi.cs', 'Mcp.cs', 'Verify.cs']);
run(probe, [installer, join(temp, 'Local data'), codex]);

const app = spawn(codex, ['app-server', '--stdio'], { cwd: temp, env, windowsHide: true });
const messages = createInterface({ input: app.stdout });
let stderr = '';
app.stderr.setEncoding('utf8');
app.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-12000); });
let next = 0;
const waiting = new Map();
messages.on('line', line => {
  const message = JSON.parse(line);
  const pending = waiting.get(message.id);
  if (!pending) return;
  waiting.delete(message.id);
  clearTimeout(pending.timer);
  if (message.error) pending.reject(new Error(JSON.stringify(message.error)));
  else pending.resolve(message.result);
});

function rpc(method, params) {
  return new Promise((resolve, reject) => {
    const id = ++next;
    const timer = setTimeout(() => reject(new Error(method + ' timed out.\n' + stderr)), 15000);
    waiting.set(id, { resolve, reject, timer });
    app.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}

try {
  await rpc('initialize', { clientInfo: { name: 'deepseek-verification', version: '1.0.0' }, capabilities: { experimentalApi: true } });
  app.stdin.write('{"jsonrpc":"2.0","method":"initialized"}\n');
  const status = await rpc('mcpServerStatus/list', { detail: 'toolsAndAuthOnly' });
  const server = status.data.find(entry => entry.tools?.ask_deepseek);
  assert(server, JSON.stringify(status) + '\n' + stderr);
  console.log('Codex discovered MCP:', server.name);
  const started = await rpc('thread/start', { cwd: temp, ephemeral: true });
  const call = await rpc('mcpServer/tool/call', { threadId: started.thread.id, server: server.name, tool: 'ask_deepseek', arguments: { prompt: '' } });
  assert.equal(call.isError, true);
  assert.match(call.content[0].text, /100000/);
  console.log('Codex invoked the installed executable and received its local validation result.');
} finally {
  messages.close();
  app.stdin.end();
  app.kill();
}
