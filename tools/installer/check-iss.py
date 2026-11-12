"""Inno Setup 腳本(tools/installer/SimosaBRM.iss)的靜態檢查:容器/CI 沒有 Inno Setup 時,以純 Python 解析區段與必要鍵,
抓結構性錯誤與本專案的部署規則(規劃書第 5.5 節、第 11.2 節;docs/ops/安裝與啟動.md 第 2.8 節):
  - 區段:[Setup]、[Tasks]、[Dirs]、[Files]、[Icons]、[Run]、[UninstallDelete]、[Code] 皆須存在;[Setup] 必要鍵齊全、每行為 鍵=值
  - 參數式區段([Files]、[Icons]、[Run]…)每行為 參數: 值; …,引號成對
  - 預設安裝位置由 GetDefaultDir 決定:D:\\Simosa BRM,其次 G:\\Simosa BRM;裝在系統磁碟須明確確認(/ALLOWSYSTEMDRIVE=1 或對話框);
    腳本中(註解外)不得出現 C:\\;UninstallFilesDir 在 {app} 內
  - [Files] 來源皆為 {#SourceDir}、目的地皆在 {app};config\\ 以 onlyifdoesntexist 安裝
  - 捷徑:start-ship-laptop.ps1 -Shell、start-instructor-laptop.ps1、stop-all.ps1
  - 防火牆:8765、8766、8767、8768、7300 各有規則,remoteip 限 DirectLinkSubnet、profile=any
  - WebView2 離線安裝檔在 [Run] 中以 FileExists 條件納入並 Check: not WebView2RuntimeInstalled
  - [UninstallDelete] 只允許刪 {app}\\build\\logs 與 {app}\\build\\tmp(白名單;不得刪 records、state、store、config、data,
    也不得刪瀏覽器端資料 build\\webview2、build\\browser 與 build\\downloads,或它們的上層、萬用字元)
  - 前置處理指令 #if/#ifdef/#ifndef/#ifexist/#ifnexist 與 #endif 成對;[Code] 的 begin/case/try/record 與 end 數量相等、括號成對
只能抓結構性錯誤,不能取代 ISCC 編譯(需 Windows + Inno Setup 6.3+)。用法:python3 -I tools/installer/check-iss.py <檔案>...
"""
import re
import sys

SETUP_REQUIRED = [
    'AppId', 'AppName', 'AppVersion', 'DefaultDirName', 'DefaultGroupName', 'OutputDir', 'OutputBaseFilename', 'PrivilegesRequired',
    'ArchitecturesInstallIn64BitMode', 'MinVersion', 'Compression', 'UninstallFilesDir', 'WizardStyle',
]
SECTIONS_REQUIRED = ['Setup', 'Tasks', 'Dirs', 'Files', 'Icons', 'Run', 'UninstallDelete', 'Code']
FIREWALL_PORTS = ['8765', '8766', '8767', '8768', '7300']
PREPROC_OPEN = re.compile(r'^\s*#\s*(if|ifdef|ifndef|ifexist|ifnexist)\b')
PREPROC_CLOSE = re.compile(r'^\s*#\s*endif\b')
PREPROC_ANY = re.compile(r'^\s*#\s*(define|if|ifdef|ifndef|ifexist|ifnexist|else|elif|endif|error|include|pragma|undef|emit|expr|insert|for|sub|endsub|file)\b')


def strip_pascal(code, keep_strings=False):
    """去掉 [Code] 的字串與註解(// 行註解、{ } 與 (* *) 區塊註解),保留換行以利行號。
    keep_strings=True 時只去掉註解、字串原樣保留(供結構檢查比對字串常值;字串內的 { } // 不會被當成註解)。"""
    out, i, n = [], 0, len(code)
    while i < n:
        c = code[i]
        if c == "'":
            j = i + 1
            while j < n and code[j] != '\n':
                if code[j] == "'":
                    if j + 1 < n and code[j + 1] == "'":
                        j += 2
                        continue
                    break
                j += 1
            out.append(code[i:j + 1] if keep_strings else ' ')
            i = j + 1
            continue
        if code.startswith('//', i):
            j = code.find('\n', i)
            i = n if j < 0 else j
            continue
        if c == '{':
            j = code.find('}', i)
            if j < 0:
                return ''.join(out), ['[Code] 區塊註解 { 未結束']
            out.append('\n' * code.count('\n', i, j))
            i = j + 1
            continue
        if code.startswith('(*', i):
            j = code.find('*)', i)
            if j < 0:
                return ''.join(out), ['[Code] 區塊註解 (* 未結束']
            out.append('\n' * code.count('\n', i, j))
            i = j + 2
            continue
        out.append(c)
        i += 1
    return ''.join(out), []


def split_params(line):
    """參數式區段的一行 → {參數名: 值}(值去引號;"" 還原為 ")。"""
    params, i, n = {}, 0, len(line)
    while i < n:
        m = re.match(r'\s*([A-Za-z]+)\s*:\s*', line[i:])
        if not m:
            return None
        key = m.group(1)
        i += m.end()
        if i < n and line[i] == '"':
            j = i + 1
            val = []
            while j < n:
                if line[j] == '"':
                    if j + 1 < n and line[j + 1] == '"':
                        val.append('"')
                        j += 2
                        continue
                    break
                val.append(line[j])
                j += 1
            if j >= n:
                return None
            params[key] = ''.join(val)
            i = j + 1
            m2 = re.match(r'\s*(;|$)', line[i:])
            if not m2:
                return None
            i += m2.end()
        else:
            j = line.find(';', i)
            if j < 0:
                j = n
            params[key] = line[i:j].strip()
            i = j + 1
    return params


def check(path):
    errs = []
    raw = open(path, 'rb').read()
    try:
        text = raw.decode('utf-8-sig')
    except UnicodeDecodeError as ex:
        return [f'不是 UTF-8:{ex}']
    lines = text.splitlines()
    # 區段切分(保留行號);[Code] 整段原文另存
    sections, order, cur, depth, code_lines = {}, [], None, 0, []
    for no, line in enumerate(lines, 1):
        if cur != 'Code':
            if PREPROC_OPEN.match(line):
                depth += 1
            elif PREPROC_CLOSE.match(line):
                depth -= 1
                if depth < 0:
                    errs.append(f'{no}: 多餘的 #endif')
                    depth = 0
        m = re.match(r'^\[([A-Za-z]+)\]\s*$', line)
        if m and (cur != 'Code' or m.group(1) in SECTIONS_REQUIRED + ['Languages', 'CustomMessages', 'Messages', 'Registry', 'InstallDelete', 'UninstallRun', 'Types', 'Components', 'Components']):
            cur = m.group(1)
            if cur in sections:
                errs.append(f'{no}: 區段 [{cur}] 重複')
            sections[cur] = []
            order.append(cur)
            continue
        if cur is None:
            if line.strip() and not line.lstrip().startswith(';') and not PREPROC_ANY.match(line):
                errs.append(f'{no}: 區段之外的內容:{line.strip()[:60]}')
            continue
        if cur == 'Code':
            code_lines.append(line)
        sections[cur].append((no, line))
    if depth != 0:
        errs.append(f'前置處理 #if… 與 #endif 不成對(差 {depth})')
    for s in SECTIONS_REQUIRED:
        if s not in sections:
            errs.append(f'缺少區段 [{s}]')
    if errs:
        return errs

    def entries(name):
        for no, line in sections.get(name, []):
            st = line.strip()
            if not st or st.startswith(';') or PREPROC_ANY.match(st):
                continue
            yield no, st

    # 註解外不得出現 C:\(全檔,含 [Code] 的字串:安裝位置不可寫死在 C:\)
    for no, line in enumerate(lines, 1):
        body = line.split(';', 1)[0] if not line.lstrip().startswith(';') else ''
        if re.search(r'(?i)\bC:\\', body):
            errs.append(f'{no}: 註解外出現 C:\\(所有輸出必須留在安裝根目錄,不寫 C:\\):{line.strip()[:60]}')

    # [Setup]
    setup = {}
    for no, st in entries('Setup'):
        m = re.match(r'^([A-Za-z0-9]+)\s*=\s*(.*)$', st)
        if not m:
            errs.append(f'{no}: [Setup] 應為 鍵=值:{st[:60]}')
            continue
        if m.group(1) in setup:
            errs.append(f'{no}: [Setup] 鍵 {m.group(1)} 重複')
        setup[m.group(1)] = m.group(2).strip()
    for k in SETUP_REQUIRED:
        if k not in setup:
            errs.append(f'[Setup] 缺少 {k}')
    if setup.get('PrivilegesRequired', '').lower() != 'admin':
        errs.append('[Setup] PrivilegesRequired 應為 admin(防火牆規則與 Runtime 安裝)')
    if setup.get('DefaultDirName') != '{code:GetDefaultDir}':
        errs.append('[Setup] DefaultDirName 應為 {code:GetDefaultDir}(有 D: 時 D:\\Simosa BRM,其次 G:\\Simosa BRM,可改)')
    if not setup.get('UninstallFilesDir', '').startswith('{app}'):
        errs.append('[Setup] UninstallFilesDir 應在 {app} 內')
    if not re.match(r'^\{\{[0-9A-Fa-f-]{36}\}$', setup.get('AppId', '')):
        errs.append(f"[Setup] AppId 應為 {{{{GUID}}:{setup.get('AppId')}")
    if '{#AppVersion}' not in setup.get('OutputBaseFilename', ''):
        errs.append('[Setup] OutputBaseFilename 應含 {#AppVersion}')
    if setup.get('DisableDirPage', 'no').lower() != 'no':
        errs.append('[Setup] DisableDirPage 應為 no(安裝位置可改)')
    if 'VersionInfoVersion' in setup:
        errs.append('[Setup] 不要設 VersionInfoVersion:預設版本 0.yyyymmdd.hhmm 的第 2 段超過 65535,請用 VersionInfoProductTextVersion')

    # 參數式區段
    parsed = {}
    for name in ['Tasks', 'Dirs', 'Files', 'Icons', 'Run', 'UninstallDelete', 'Languages']:
        parsed[name] = []
        for no, st in entries(name):
            if st.count('"') % 2 == 1:
                errs.append(f'{no}: [{name}] 引號不成對:{st[:60]}')
                continue
            p = split_params(st)
            if p is None:
                errs.append(f'{no}: [{name}] 無法解析(應為 參數: 值; …):{st[:60]}')
                continue
            parsed[name].append((no, p))

    # [Files]
    files = parsed['Files']
    if not files:
        errs.append('[Files] 沒有項目')
    config_entry = False
    for no, p in files:
        if not p.get('Source', '').startswith('{#SourceDir}'):
            errs.append(f'{no}: [Files] Source 應以 {{#SourceDir}} 起始:{p.get("Source")}')
        if not p.get('DestDir', '').startswith('{app}'):
            errs.append(f'{no}: [Files] DestDir 應在 {{app}} 內:{p.get("DestDir")}')
        if p.get('DestDir') == '{app}\\config':
            config_entry = True
            if 'onlyifdoesntexist' not in p.get('Flags', ''):
                errs.append(f'{no}: [Files] config\\ 應以 onlyifdoesntexist 安裝(不覆寫安裝時修改過的設定)')
        elif 'Excludes' in p and '\\config\\*' not in p['Excludes']:
            errs.append(f'{no}: [Files] 主項目的 Excludes 應排除 \\config\\*')
    if not config_entry:
        errs.append('[Files] 缺少 config\\ 的獨立項目')
    manifest_in_excludes = any('manifest' in p.get('Excludes', '') for _, p in files)
    if manifest_in_excludes:
        errs.append('[Files] 不得排除 manifest.json(安裝後 release verify 需要)')

    # [Icons]
    icon_params = ' '.join(p.get('Parameters', '') + ' ' + p.get('Filename', '') for _, p in parsed['Icons'])
    for need, desc in [('start-ship-laptop.ps1" -Shell', '操船台外殼捷徑(start-ship-laptop.ps1 -Shell)'), ('start-instructor-laptop.ps1', '教官站捷徑'), ('stop-all.ps1', '停止捷徑')]:
        if need not in icon_params:
            errs.append(f'[Icons] 缺少{desc}')
    if not any(p.get('Name', '').startswith('{autodesktop}') and p.get('Tasks') == 'desktopicon' for _, p in parsed['Icons']):
        errs.append('[Icons] 桌面捷徑應綁 Tasks: desktopicon')
    for no, p in parsed['Icons']:
        if p.get('Filename', '').endswith('.ps1'):
            errs.append(f'{no}: [Icons] 捷徑不可直接指向 .ps1(預設關聯為記事本),應以 powershell.exe -File 啟動')
    tasks = {p.get('Name') for _, p in parsed['Tasks']}
    for t in ['desktopicon', 'firewall']:
        if t not in tasks:
            errs.append(f'[Tasks] 缺少 {t}')

    # [Run]:WebView2 離線安裝檔(條件納入)
    run_text = '\n'.join(line for _, line in sections['Run'])
    if 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe' not in run_text and '{#WebView2Installer}' not in run_text:
        errs.append('[Run] 缺少 WebView2 Runtime 離線安裝檔的執行項目')
    wv = [p for _, p in parsed['Run'] if 'WebView2' in p.get('Filename', '') or 'webview2' in p.get('Filename', '')]
    for p in wv:
        if p.get('Check', '').replace(' ', '') != 'notWebView2RuntimeInstalled':
            errs.append('[Run] WebView2 安裝應有 Check: not WebView2RuntimeInstalled')
        if '/silent' not in p.get('Parameters', '') or '/install' not in p.get('Parameters', ''):
            errs.append('[Run] WebView2 安裝參數應為 /silent /install')
    if not re.search(r'#if\s+FileExists\([^\n]*(WebView2Installer|MicrosoftEdgeWebView2RuntimeInstallerX64)', '\n'.join(lines)):
        errs.append('WebView2 離線安裝檔應以 #if FileExists(…) 條件納入(發行版沒有該檔時仍可編譯)')

    # [UninstallDelete]:白名單 — 只允許刪 log 與暫存。其餘一律保留:練習紀錄與設定(records、state、store、config、data)、
    # 瀏覽器端資料(build\webview2、build\browser:草稿、參與者資料、講評報告站的訓練紀錄)、build\downloads;
    # 以名稱比對會漏掉「刪它們的上層」({app}\build、{app})與萬用字元({app}\build\*),所以改用白名單
    allowed_delete = {'{app}\\build\\logs', '{app}\\build\\tmp'}
    for no, p in parsed['UninstallDelete']:
        n = p.get('Name', '').lower().replace('/', '\\').rstrip('\\')
        if '*' in n or '?' in n or n not in allowed_delete:
            errs.append(f'{no}: [UninstallDelete] 只允許 {{app}}\\build\\logs 與 {{app}}\\build\\tmp(解除安裝保留紀錄、設定與瀏覽器端資料;不得用萬用字元或刪上層):{p.get("Name")}')
    if not any(p.get('Name', '').startswith('{app}') for _, p in parsed['Dirs']):
        errs.append('[Dirs] 應建立 {app} 下的紀錄/log 目錄')

    # [Code]
    code = '\n'.join(code_lines)
    stripped, cerr = strip_pascal(code)
    errs += cerr
    if not cerr:
        words = re.findall(r'\b(begin|case|try|record|end)\b', stripped, flags=re.IGNORECASE)
        opens = sum(1 for w in words if w.lower() != 'end')
        closes = sum(1 for w in words if w.lower() == 'end')
        if opens != closes:
            errs.append(f'[Code] begin/case/try/record({opens})與 end({closes})數量不相等')
        for o, c in [('(', ')'), ('[', ']')]:
            if stripped.count(o) != stripped.count(c):
                errs.append(f'[Code] {o}{c} 不成對')
        for fn in ['GetDefaultDir', 'WebView2RuntimeInstalled', 'AddFirewallRules', 'RemoveFirewallRules', 'NextButtonClick', 'CurStepChanged', 'CurUninstallStepChanged']:
            if not re.search(r'\b(function|procedure)\s+' + fn + r'\b', stripped):
                errs.append(f'[Code] 缺少 {fn}')
        for kw in ['label', 'goto', 'type', 'object', 'class', 'interface']:
            if re.search(r'(?i)\b(const|var|procedure|function)\b[^;\n]*\b' + kw + r'\s*[:,)]', stripped):
                errs.append(f'[Code] 以保留字 {kw} 作為識別字')
    # 以下檢查在「去掉註解、保留字串」的程式碼上做(被註解掉的程式碼不算數),並以正規表示式綁住結構
    nocomment, _ = strip_pascal(code, keep_strings=True)

    def body_of(fn):
        m = re.search(r'\b(?:function|procedure)\s+' + fn + r'\b.*?\nend;', nocomment, flags=re.S)
        return m.group(0) if m else ''

    default_dir = body_of('GetDefaultDir')
    if not re.search(r"if\s+DirExists\('D:\\'\)\s+then\s+Result\s*:=\s*'D:\\Simosa BRM'\s+"
                     r"else\s+if\s+DirExists\('G:\\'\)\s+then\s+Result\s*:=\s*'G:\\Simosa BRM'\s+"
                     r"else\s+Result\s*:=\s*ExpandConstant\('\{sd\}\\Simosa BRM'\)\s*;", default_dir):
        errs.append('[Code] GetDefaultDir 的結構應為:有 D:\\ → D:\\Simosa BRM;否則有 G:\\ → G:\\Simosa BRM;否則 {sd}\\Simosa BRM')
    next_click = body_of('NextButtonClick')
    if not re.search(r"else\s+if\s+Pos\(AddBackslash\(Lowercase\(ExpandConstant\('\{sd\}'\)\)\),\s*Dir\)\s*=\s*1\s+then\s+begin\s+"
                     r"if\s+ExpandConstant\('\{param:ALLOWSYSTEMDRIVE\|0\}'\)\s*<>\s*'1'\s+then\s+begin\s+"
                     r"if\s+SuppressibleMsgBox\((?:(?!SuppressibleMsgBox).)*?mbConfirmation,\s*MB_YESNO\s+or\s+MB_DEFBUTTON2,\s*IDNO\)\s*<>\s*IDYES\s+then\s+"
                     r"Result\s*:=\s*False\s*;", next_click, flags=re.S):
        errs.append('[Code] NextButtonClick 的系統磁碟分支結構應為:位置在 {sd} 之下且 /ALLOWSYSTEMDRIVE 不是 1 → SuppressibleMsgBox(…, mbConfirmation, '
                    'MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES → Result := False')
    if "'D:\\Simosa BRM'" not in code:
        errs.append('[Code] GetDefaultDir 應回傳 D:\\Simosa BRM')
    if "'G:\\Simosa BRM'" not in code:
        errs.append('[Code] GetDefaultDir 在沒有 D: 時應回傳 G:\\Simosa BRM(守則:本機資料放 D: 或 G:)')
    # 預設位置的優先順序:D: → G: → 系統磁碟(最後才是 {sd})
    order = [code.find("'D:\\Simosa BRM'"), code.find("'G:\\Simosa BRM'"), code.find("{sd}\\Simosa BRM")]
    if -1 in order or order != sorted(order):
        errs.append('[Code] GetDefaultDir 的順序應為 D: → G: → 系統磁碟({sd})')
    # 系統磁碟須明確確認:/ALLOWSYSTEMDRIVE=1 或確認對話框(預設「否」);不得靜默裝到系統磁碟
    if '{param:ALLOWSYSTEMDRIVE|0}' not in code or 'SuppressibleMsgBox' not in code or 'IDNO' not in code:
        errs.append('[Code] NextButtonClick 應在安裝位置位於系統磁碟({sd})時要求確認(/ALLOWSYSTEMDRIVE=1 或 SuppressibleMsgBox 預設 IDNO)')
    if 'Users' not in code or 'wpSelectDir' not in code:
        errs.append('[Code] NextButtonClick 應在 wpSelectDir 拒絕使用者資料夾(Users)之下的安裝位置')
    # 防火牆
    for port in FIREWALL_PORTS:
        if not re.search(r"Ports\s*:=\s*'" + port + r"'", code):
            errs.append(f'[Code] 缺少埠 {port} 的防火牆規則')
    if "remoteip=' + DirectLinkSubnet" not in code:
        errs.append('[Code] 防火牆規則應以 remoteip 限直連私有網段(DirectLinkSubnet)')
    if 'profile=any' not in code:
        errs.append('[Code] 防火牆規則應 profile=any(直連被判為公用網路)')
    if 'RemoveFirewallRules' not in code or 'usUninstall' not in code:
        errs.append('[Code] 解除安裝時應移除防火牆規則')
    if 'usPostUninstall' not in code or 'records' not in code:
        errs.append('[Code] 解除安裝後應提示保留的紀錄位置')
    rule_count = re.search(r'RuleCount\s*=\s*(\d+)', code)
    cases = len(re.findall(r"^\s*\d+:\s*begin\s+Name\s*:=", code, flags=re.M))
    if rule_count and int(rule_count.group(1)) != cases:
        errs.append(f'[Code] RuleCount({rule_count.group(1)})與 GetRule 的規則數({cases})不符')
    return errs


def main(argv):
    bad = 0
    if not argv:
        print(__doc__.strip().splitlines()[-1])
        return 2
    for p in argv:
        e = check(p)
        print(('FAIL ' if e else 'ok   ') + p)
        for x in e:
            print('    ' + x)
        bad += bool(e)
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
