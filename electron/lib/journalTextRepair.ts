import iconv from 'iconv-lite'

// ===== 历史乱码修复（v2.3，与 WPF 侧 JournalTextRepair 同算法）=====
// 旧版 Electron 的 PowerShell 链路把 UTF-8 字节按系统代码页（中文 Windows=GBK/936）解码，
// journal 台账的 name/detail 存成乱码（如 "鍏变韩宸插垹闄わ紙" 实为 "共享已删除（"）。
// 逆转：乱码 --GBK编码--> 原始字节 --严格 UTF-8 解码--> 原文。
// 逐段贪心：整段不可逆时切最长可解段，单字符不可解则保留——最大化救回中文。
// 幂等：修复后的文本无可解段，再跑不变。当前代码库已无 GBK 解码路径，本模块只修历史数据。

const CJK = /[\u4e00-\u9fff]/

function tryDecode(seg: string): string | null {
  try {
    const buf = iconv.encode(seg, 'gbk')
    const t = new TextDecoder('utf-8', { fatal: true }).decode(buf)
    if (!t || t === seg) return null
    if (!CJK.test(t)) return null
    return t
  } catch {
    return null
  }
}

export function repairJournalText(s: string | null | undefined): string {
  if (!s) return (s ?? '') as string
  if (!CJK.test(s)) return s

  let out = ''
  let i = 0
  const n = s.length
  while (i < n) {
    let best: string | null = null
    let bestEnd = -1
    for (let j = n; j > i; j--) {
      if (best !== null && j - i <= bestEnd - i) break
      const t = tryDecode(s.slice(i, j))
      if (t !== null) {
        best = t
        bestEnd = j
        break
      }
    }
    if (best !== null) {
      out += best
      i = bestEnd
    } else {
      out += s[i]
      i++
    }
  }
  return out
}
