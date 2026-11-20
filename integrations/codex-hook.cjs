// Codex sends one event on stdin. Forward only fields used by the pet.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

function forward(event, exe, data) {
  const project = path.basename(event.cwd || '') || '会话';
  const record = { cwd: '[Codex] ' + project };
  for (const key of ['hook_event_name', 'session_id', 'tool_name', 'source', 'reason', 'agent_type']) {
    if (typeof event[key] === 'string') record[key] = event[key];
  }
  if (record.tool_name === 'Bash') {
    record.tool_input = { command: event.tool_input?.command || '' };
    const code = event.tool_response?.exit_code ?? event.tool_response?.exitCode;
    if (Number.isInteger(code)) record.tool_response = { exit_code: code };
  }
  const args = ['--hook'];
  if (data) args.push('--data', data);
  const result = spawnSync(exe, args, { input: JSON.stringify(record), windowsHide: true, timeout: 4000, stdio: ['pipe', 'ignore', 'ignore'] });
  return result.status === 0;
}

if (require.main === module) {
  try {
    const exe = path.join(os.homedir(), '.claude', 'firefly-pet', 'app', 'FireflyPet.exe');
    if (!forward(JSON.parse(fs.readFileSync(0, 'utf8')), exe)) process.exitCode = 1;
  } catch { process.exitCode = 1; }
}
module.exports = { forward };
