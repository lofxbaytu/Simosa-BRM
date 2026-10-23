"""PowerShell 靜態語法檢查(無 pwsh 的環境,例如 Linux 容器/CI):括號/大括號/方括號配對、單/雙引號字串、here-string(@" "@ / @' '@)、註解 <# #>、
反引號續行。只能抓結構性錯誤,不能取代 pwsh 的解析器。用法:python3 pscheck.py <檔案>..."""
import sys, re
def check(path):
    s = open(path, encoding='utf-8').read()
    i, n, line = 0, len(s), 1
    stack = []  # (char, line)
    errs = []
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
    # 常見手誤:$var: 需寫 ${var}: 或 `:
    for m in re.finditer(r'"[^"\n]*\$[A-Za-z_]\w*:[^"\n]*"', s):
        txt = m.group(0)
        if '`:' not in txt and '${' not in txt and not re.search(r'\$\((script|env|global|local):|\$(script|env|global|local):', txt): errs.append(f'{s.count(chr(10),0,m.start())+1}: 字串中 $變數: 會被解析成範圍限定詞:{txt[:60]}')
    return errs
bad = 0
for p in sys.argv[1:]:
    e = check(p)
    print(('FAIL ' if e else 'ok   ') + p)
    for x in e: print('   ', x)
    bad += bool(e)
sys.exit(1 if bad else 0)
