"""PowerShell 靜態語法檢查(無 pwsh 的環境,例如 Linux 容器/CI):括號/大括號/方括號配對、單/雙引號字串、here-string(@" "@ / @' '@)、註解 <# #>、
反引號續行;另檢查含非 ASCII 字元的檔案有 UTF-8 BOM(Windows PowerShell 5.1 需要)。只能抓結構性錯誤,不能取代 pwsh 的解析器。用法:python3 pscheck.py <檔案>..."""
import sys, re
def check(path):
    raw = open(path, 'rb').read()
    s = raw.decode('utf-8-sig')
    i, n, line = 0, len(s), 1
    stack = []  # (char, line)
    errs = []
    # 含非 ASCII 字元(中文註解/訊息)的 .ps1 必須有 UTF-8 BOM:Windows PowerShell 5.1(船上筆電以 powershell -File 執行)
    # 把沒有 BOM 的檔案當成系統 ANSI 字碼頁(繁體中文 Windows 為 Big5)讀取 → 中文亂碼,雙位元組尾碼還可能吃掉引號或括號造成語法錯誤
    if any(b >= 0x80 for b in raw) and not raw.startswith(b'\xef\xbb\xbf'):
        errs.append('1: 含非 ASCII 字元但沒有 UTF-8 BOM(Windows PowerShell 5.1 會以 Big5 讀取 → 亂碼或語法錯誤);請以「UTF-8 with BOM」存檔')
    pairs = {')': '(', '}': '{', ']': '['}
    while i < n:
        c = s[i]
        if c == '\n': line += 1; i += 1; continue
        # 區塊註解
        if s.startswith('<#', i):
            j = s.find('#>', i + 2)
            if j < 0: errs.append(f'{line}: 區塊註解未結束'); break
            line += s.count('\n', i, j); i = j + 2; continue
        # 行註解
        if c == '#':
            j = s.find('\n', i); i = n if j < 0 else j; continue
        # here-string
        if s.startswith('@"', i) or s.startswith("@'", i):
            q = s[i + 1]
            j = s.find('\n' + q + '@', i + 2)
            if j < 0: errs.append(f'{line}: here-string 未結束'); break
            line += s.count('\n', i, j + 1); i = j + 3; continue
        # 字串
        if c == '"' or c == "'":
            q = c; j = i + 1
            while j < n:
                if s[j] == '`' and q == '"': j += 2; continue
                if s[j] == q:
                    if j + 1 < n and s[j + 1] == q: j += 2; continue   # 連續兩個引號 = 跳脫
                    break
                if s[j] == '\n': line += 1
                j += 1
            if j >= n: errs.append(f'{line}: 字串未結束'); break
            i = j + 1; continue
        if c in '({[': stack.append((c, line))
        elif c in ')}]':
            if not stack or stack[-1][0] != pairs[c]: errs.append(f'{line}: 多餘或不配對的 {c}'); 
            else: stack.pop()
        i += 1
    for ch, ln in stack: errs.append(f'{ln}: {ch} 未關閉')
    # 常見手誤(雙引號字串內;都是合法語法,解析器不會報錯,執行時變數展開成空字串,StrictMode 下丟例外):
    #  (1) $var: 需寫 ${var}: 或 `:(半形冒號會被解析成範圍限定詞;全形冒號沒有這個問題)— 逐一檢查每個 $name:,
    #      同一字串內另有 ${…} 不代表其他的 $name: 沒問題;
    #  (2) $var 緊接中文字或全形數字(例如 "$root內"):PowerShell 把這些字元當成變數名稱的一部分,需寫 ${var}。
    scopes = ('script', 'env', 'global', 'local', 'private', 'using', 'function', 'variable', 'alias')
    for m in re.finditer(r'"(?:[^"`\n]|`.)*"', s):
        txt = m.group(0)
        ln = s.count(chr(10), 0, m.start()) + 1
        for v in re.finditer(r'(?<!`)\$([A-Za-z_][A-Za-z0-9_]*)(.?)', txt):
            name, nxt = v.group(1), v.group(2)
            if nxt == ':' and name.lower() not in scopes:
                errs.append(f'{ln}: 字串中 ${name}: 會被解析成範圍限定詞(改寫 ${{{name}}}:):{txt[:60]}')
            elif nxt and ord(nxt) > 127 and (nxt.isalnum() or nxt == '_'):
                errs.append(f'{ln}: 字串中 ${name} 緊接「{nxt}」會被當成變數名稱的一部分(改寫 ${{{name}}}):{txt[:60]}')
    return errs
bad = 0
for p in sys.argv[1:]:
    e = check(p)
    print(('FAIL ' if e else 'ok   ') + p)
    for x in e: print('   ', x)
    bad += bool(e)
sys.exit(1 if bad else 0)
