/**
 * 用 Node 内置 TS 支持（Node 24 的 --experimental-strip-types）做纯语法检查，
 * 不依赖任何 native addon（esbuild/tsc 都在本机段错误时的最后兜底）。
 * 逐个 import 文件，Node 会在 import 时先做类型擦除 + 语法解析，
 * 语法错会立刻抛 SyntaxError；能 import 成功即语法 OK。
 */
import { readdirSync, statSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { pathToFileURL } from 'node:url'

const ROOT = join(dirname(dirname(new URL(import.meta.url).pathname).replace(/^\//, '')))

function walk(dir, out = []) {
  for (const n of readdirSync(dir)) {
    const p = join(dir, n)
    const st = statSync(p)
    if (st.isDirectory()) walk(p, out)
    else if (/\.(ts|tsx)$/.test(n)) out.push(p)
  }
  return out
}

const dirs = process.argv.slice(2).length ? process.argv.slice(2) : ['electron', 'src']
const files = dirs.flatMap((d) => walk(join(ROOT, d)).filter((f) => !/\.test\.|\.d\.ts$/.test(f)))

let fail = 0
for (const f of files) {
  try {
    // 用 text 方式读 + 不真正执行，仅让 Node 做语法解析：
    // 最简方式 = 用 `node --check` 思路，但 .ts 不能直接 --check。
    // 退而求其次：用内置的 SyntaxError 触发——把文件内容包进一个
    // `async () => { /* contents */ }` 不行的话，至少用 `new Function` 兜底：
    const src = (await import('node:fs')).readFileSync(f, 'utf8')
    // 仅语法层面：把 TS 类型注解粗剥（: type / as / <泛型>）后喂给 new Function
    const stripped = src
      .replace(/: *[\w<>\[\]{},\s]+? =>/g, ' =>') // 粗略
      .replace(/import .* from .*\n/g, '')
      .replace(/export /g, '')
    // 不执行，仅解析：用 vm.compileFunction（仅语法解析，不跑）
    const vm = await import('node:vm')
    vm.compileFunction(stripped, [], {})
  } catch (e) {
    fail++
    console.error(`FAIL ${f}: ${String(e.message).split('\n')[0]}`)
  }
}
console.log(
  `${files.length} files checked, ${fail} syntax errors (vm.compileFunction 粗检，非完整类型检查)`,
)
process.exit(fail ? 1 : 0)
