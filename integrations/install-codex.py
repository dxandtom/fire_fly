"""Register the Codex bridge alongside existing user hooks on Windows."""
import json
import os
from pathlib import Path
import shutil
import tempfile

home = Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex")
config = home / "hooks.json"
exe = Path.home() / ".claude/firefly-pet/app/FireflyPet.exe"
node = shutil.which("node")
if not exe.is_file() or not node:
    raise SystemExit("Install FireflyPet and Node.js first.")
bridge = Path(__file__).with_name("codex-hook.cjs").resolve()
command = f'node "{bridge.as_posix()}"'
original = config.read_bytes() if config.exists() else None
settings = json.loads(original.decode("utf-8-sig")) if original else {}
hooks = settings.setdefault("hooks", {})
for event in ("SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse",
              "PermissionRequest", "SubagentStart", "SubagentStop", "PreCompact",
              "Stop", "SessionEnd"):
    groups = hooks.setdefault(event, [])
    if any(h.get("command") == command for g in groups for h in g["hooks"]):
        continue
    groups.append({"hooks": [{"type": "command", "command": command,
                              "timeout": 5, "async": event != "SessionEnd"}]})
if original and settings == json.loads(original.decode("utf-8-sig")):
    print("Already installed.")
    raise SystemExit(0)
home.mkdir(parents=True, exist_ok=True)
if original:
    with tempfile.NamedTemporaryFile(dir=home, prefix="hooks.before-firefly-", suffix=".bak", delete=False) as backup:
        backup.write(original)
with tempfile.NamedTemporaryFile(dir=home, suffix=".tmp", delete=False, mode="w", encoding="utf-8") as temp:
    json.dump(settings, temp, ensure_ascii=False, indent=2)
    temp.write("\n")
if (config.read_bytes() if config.exists() else None) != original:
    Path(temp.name).unlink()
    raise SystemExit("Hooks changed during installation. Retry.")
os.replace(temp.name, config)
print("Installed FireflyPet Codex hooks.")

