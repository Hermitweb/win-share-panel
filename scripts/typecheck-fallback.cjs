/**
 * 类型检查兜底（v1.4.0）：本机 tsc / esbuild / vitest 段错误（RC=139）时，
 * 用项目自带的 typescript 包直接调 API 做**完整类型检查**（绕过 pnpm exec tsc 的段错误路径）。
 * 与 tsconfig.node.json + tsconfig.json 的语义接近（strict + bundler 解析）。
 *
 * 用法：node scripts/typecheck-fallback.cjs
 */
const ts = require('typescript')
const fs = require('fs')
const path = require('path')

function walk(dir, out = []) {
  for (const n of fs.readdirSync(dir)) {
    const p = path.join(dir, n)
    if (fs.statSync(p).isDirectory()) walk(p, out)
    else if (/\.(ts|tsx)$/.test(n) && !/\.d\.ts$/.test(n)) out.push(p)
  }
  return out
}

// 主程序文件（非测试）：做完整跨文件类型检查
const mainFiles = [...walk('electron'), ...walk('src')].filter((f) => !/\.test\./.test(f))

const program = ts.createProgram(mainFiles, {
  noEmit: true,
  strict: true,
  jsx: ts.JsxEmit.ReactJSX,
  module: ts.ModuleKind.ESNext,
  target: ts.ScriptTarget.ES2022,
  moduleResolution: ts.ModuleResolutionKind.Bundler,
  skipLibCheck: true,
  esModuleInterop: true,
  paths: { '@/*': ['src/*'] },
  baseUrl: '.',
})
const diags = ts.getPreEmitDiagnostics(program).slice(0, 80)
for (const d of diags) {
  const msg = ts.flattenDiagnosticMessageText(d.messageText, '\n')
  if (d.file) {
    const { line, character } = d.file.getLineAndCharacterOfPosition(d.start)
    const rel = d.file.fileName.replace(/^.*win-share-panel[\\/]/, '')
    console.log('ERR', rel + ':' + (line + 1) + ':' + (character + 1), msg.split('\n')[0])
  } else {
    console.log('ERR', msg.split('\n')[0])
  }
}
console.log(diags.length + ' diagnostics shown (前 80；0 = 类型检查通过)')
process.exit(diags.length ? 1 : 0)
