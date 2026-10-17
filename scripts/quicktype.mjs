/**
 * 快速类型/语法检查兜底（v1.4.0）：本机 tsc / vitest / esbuild native 段错误（RC=139）时，
 * 用 Node 24 内置 stripTypeScriptTypes（--experimental-strip-types 的底层 API）
 * 逐个文件做**语法级**校验，至少能抓「语法错 / 未闭合引号 / 重复声明 / 非法 TS 语法」。
 *
 * 限制：只做语法与类型擦除检查，不做跨文件类型检查（那是 tsc 的事）。
 * 用法：node scripts/quicktype.mjs
 */
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { stripTypeScriptTypes } from 'node:module'

const ROOT = join(dirname(dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:?\/)/, '$1')))

function walk(dir, out = []) {
  for (const n of readdirSync(dir)) {
    const p = join(dir, n)
    const st = statSync(p)
    if (st.isDirectory()) walk(p, out)
    else if (/\.(ts|tsx)$/.test(n)) out.push(p)
  }
  return out
}

const files = [...walk(join(ROOT, 'electron')), ...walk(join(ROOT, 'src'))]
let fail = 0
for (const f of files) {
  const src = readFileSync(f, 'utf8')
  try {
    stripTypeScriptTypes(src, { mode: 'strip' })
  } catch (e) {
    fail++
    console.error(`FAIL ${f.replace(ROOT, '').replace(/^\//, '')}: ${String(e.message).split('\n')[0]}`)
  }
}
console.log(`checked ${files.length} files, ${fail} syntax errors（stripTypeScriptTypes 语法级，非完整类型检查）`)
process.exit(fail ? 1 : 0)
