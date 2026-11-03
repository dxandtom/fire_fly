# hook 测试：耗时、事件字段、测试/编译命令识别、转义与中文。只用临时数据目录。
# 用法：python tests/t_hook.py [exe 路径]
import json, os, shutil, subprocess, sys, tempfile, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'plugins', 'firefly-pet', 'bin', 'FireflyPet.exe')
data = tempfile.mkdtemp(prefix='ff-hook-')
open(os.path.join(data, 'config.json'), 'w').write('{"AutoStart":false}')


def hook(ev):
    s = time.perf_counter()
    subprocess.run([EXE, '--hook', '--data', data], input=json.dumps(ev).encode('utf-8'), stderr=subprocess.DEVNULL)
    return (time.perf_counter() - s) * 1000


def last():
    with open(os.path.join(data, 'events.jsonl'), encoding='utf-8') as f:
        return json.loads(f.read().strip().split('\n')[-1])


fails = 0


def check(name, ok, info=''):
    global fails
    print(('PASS ' if ok else 'FAIL ') + name + ('' if ok else '  ' + str(info)))
    if not ok:
        fails += 1


def bash(cmd, ev='PostToolUse', resp=None):
    e = {'hook_event_name': ev, 'session_id': 's1', 'cwd': 'D:\\proj\\demo', 'tool_name': 'Bash',
         'tool_input': {'command': cmd, 'description': ''}}
    if resp is not None:
        e['tool_response'] = resp
    hook(e)
    return last()


times = [hook({'hook_event_name': 'PreToolUse', 'session_id': 's1', 'cwd': 'D:/p/x', 'tool_name': 'Read',
               'tool_input': {'file_path': 'C:\\a\\b.cs'}}) for _ in range(8)]
times = sorted(times[1:])
print('hook 耗时（毫秒，去掉首次）中位数 %.0f，最快 %.0f' % (times[len(times) // 2], times[0]))
check('hook 中位耗时 < 70ms', times[len(times) // 2] < 70, times)

r = last()
check('基本字段', r.get('ev') == 'PreToolUse' and r.get('sid') == 's1' and r.get('cwd') == 'x' and r.get('detail') == 'b.cs', r)

hook({'hook_event_name': 'Notification', 'session_id': 's1', 'message': '需要你的确认 "引号" \\ 反斜杠\n换行 😀'})
r = last()
check('中文、引号、反斜杠、表情原样保留（换行按设计压成空格）', r.get('msg', '') == '需要你的确认 "引号" \\ 反斜杠 换行 😀', r)

cases = [
    ('pytest -q tests/', 'test'), ('npm test', 'test'), ('npm run test -- --watch=false', 'test'),
    ('cd app && go test ./...', 'test'), ('powershell -File tests/t_safety.ps1', 'test'),
    ('python -m unittest discover', 'test'), ('dotnet test', 'test'),
    ('node build.js', 'build'), ('npm run build', 'build'), ('cargo build --release', 'build'),
    ('make', 'build'), ('cd x; make -j8', 'build'), ('tsc -p .', 'build'),
    ('git status', None), ('ls -la', None), ('echo make sure', None), ('cat latest.txt', None),
]
for cmd, want in cases:
    r = bash(cmd)
    check('识别 %-34s -> %s' % (cmd, want), r.get('kind') == want, r)

r = bash('npm test', resp={'stdout': '', 'exitCode': 1})
check('退出码非 0 记为失败', r.get('fail') == 1 and r.get('kind') == 'test', r)
r = bash('npm test', resp={'stdout': 'ok', 'exitCode': 0})
check('退出码 0 不算失败', 'fail' not in r, r)
r = bash('npm test', ev='PostToolUseFailure', resp='Error: Exit code 1')
check('PostToolUseFailure 也带 kind', r.get('ev') == 'PostToolUseFailure' and r.get('kind') == 'test', r)

hook({'hook_event_name': 'PostToolUseFailure', 'session_id': 's1', 'tool_name': 'Edit', 'tool_input': {'file_path': 'a.txt'},
      'error': {'code': 5, 'why': 'x'}})
r = last()
check('error 为对象时写成 JSON 文本', r.get('error', '').startswith('{"code":5'), r)

TP = 'C:' + chr(92) + 'Users' + chr(92) + 'x' + chr(92) + 's1.jsonl'
hook({'hook_event_name': 'Stop', 'session_id': 's1', 'transcript_path': TP})
check('Stop 带会话记录路径（体力条用）', last().get('tp') == TP, last())
hook({'hook_event_name': 'PreToolUse', 'session_id': 's1', 'tool_name': 'Read', 'transcript_path': TP, 'tool_input': {}})
check('其他事件不带路径，事件文件保持精简', 'tp' not in last(), last())

with open(os.path.join(data, 'events.jsonl'), 'ab') as f:
    f.write(b'')
subprocess.run([EXE, '--hook', '--data', data], input=b'not json', stderr=subprocess.DEVNULL)
check('非法输入也不崩，记为 Unknown', last().get('ev') == 'Unknown', last())

shutil.rmtree(data, ignore_errors=True)
print('全部通过' if fails == 0 else '%d 项失败' % fails)
sys.exit(1 if fails else 0)
