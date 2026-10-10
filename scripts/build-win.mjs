#!/usr/bin/env node
/**
 * Windows 三架构打包编排（x86 / x64 / arm64）。
 *
 * 为什么不让 electron-builder 一次打完三个架构：
 *   ① 同一次调用里给多个 arch，NSIS/portable 会产出**内嵌多架构的「胖」安装包**
 *      （v1.2.0 的 release/ 里实测有 196MB 的 `WinShare Panel-1.2.0-Setup.exe` 与
 *      `-portable.exe`，而单架构分别是 101MB / 96MB）——用户被迫下载用不到的另一套 Electron；
 *   ② 架构标识没法按架构分别命名（Electron 内部 arch id 是 ia32，对外要求写 x86）；
 *   ③ arm64 的 MSI 会被 electron-builder 静默降级成 x64 包装（见 electron-builder.yml 注释）。
 * 因此：electron-vite 构建一次（与架构无关），electron-builder 每个架构单独调用，
 * 各自输出到 release/arch-<label>/，再由本脚本收敛产物、写校验和、**校验 PE 头架构**。
 *
 * 用法：
 *   node scripts/build-win.mjs                    # 三个架构全打
 *   node scripts/build-win.mjs --arch x64         # 只打一个（可重复：--arch x64 --arch arm64）
 *   node scripts/build-win.mjs --arch x86 --arch arm64
 *   node scripts/build-win.mjs --skip-vite        # 跳过 electron-vite（复用上次 out/）
 *   node scripts/build-win.mjs --targets nsis     # 只打指定 target（迭代用）
 *
 * 解包目录按架构保留在 release/arch-<label>/ 下，便于核对 PE 架构与排查。
 *
 * 产物命名（架构标识即对外承诺）：
 *   WinShare.Panel-<version>-Setup-x86.exe      32 位安装包（Electron ia32）
 *   WinShare.Panel-<version>-Setup-x64.exe      64 位安装包
 *   WinShare.Panel-<version>-Setup-arm64.exe    Windows on ARM64 安装包
 *   + 同名的 portable-<label>.exe 与 <label>.msi（MSI 仅 x86/x64）
 *   release/SHA256SUMS.txt                      以上产物的 SHA-256
 */
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { fileURLToPath } from 'node:url'
import { execFileSync } from 'node:child_process'

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const RELEASE = path.join(ROOT, 'release')

/** PE 头 Machine 常量（校验产物架构用） */
const PE_MACHINE = { ia32: 0x014c, x64: 0x8664, arm64: 0xaa64 }

/**
 * 三架构定义。label 是**对外架构标识**（写进文件名），electronArch 是 Electron 内部 id。
 * targets：arm64 不含 msi（理由见 electron-builder.yml）。
 */
const ARCHES = {
  x86: { label: 'x86', flag: '--ia32', electronArch: 'ia32', targets: ['nsis', 'portable', 'msi'] },
  x64: { label: 'x64', flag: '--x64', electronArch: 'x64', targets: ['nsis', 'portable', 'msi'] },
  arm64: {
    label: 'arm64',
    flag: '--arm64',
    electronArch: 'arm64',
    targets: ['nsis', 'portable'],
  },
}
/** 用户输入别名：ia32/32/x86 → x86；amd64 → x64；aarch64/arm → arm64 */
const ARCH_ALIAS = {
  x86: 'x86',
  ia32: 'x86',
  32: 'x86',
  '32bit': 'x86',
  x64: 'x64',
  amd64: 'x64',
  64: 'x64',
  arm64: 'arm64',
  aarch64: 'arm64',
  arm: 'arm64',
}

function parseArgs(argv) {
  const wanted = []
  const opts = { skipVite: false, targets: null }
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    if (a === '--arch') {
      const key = ARCH_ALIAS[String(argv[++i]).toLowerCase()]
      if (!key) throw new Error(`未知架构：${argv[i]}（可用 x86/x64/arm64）`)
      wanted.push(key)
    } else if (a === '--targets') {
      // 迭代/试验用：只打指定 target（仍受该架构的目标集合限制）
      opts.targets = String(argv[++i])
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean)
    } else if (a === '--skip-vite') opts.skipVite = true
    else if (a === '--help' || a === '-h') opts.help = true
    else throw new Error(`未知参数：${a}`)
  }
  opts.arches = wanted.length ? [...new Set(wanted)] : Object.keys(ARCHES)
  return opts
}

function run(command, args, { env, cwd = ROOT } = {}) {
  execFileSync(command, args, { cwd, env: env ?? process.env, stdio: 'inherit' })
}

/** 同步睡眠（构建脚本全程同步，不引依赖） */
function sleepSync(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms)
}

/**
 * 带一次重试的打包调用 —— 针对下载类瞬时故障（Electron 二进制从镜像拉取超时/包不完整，
 * v1.3.0 首次 CI 发布即在 arm64 上遇到：npmmirror 返回的包解不出 electron.exe）。
 * 不掩盖真实失败：两次都失败即抛出原始错误并中止。
 */
function runPackagingWithRetry(label, command, args, opts, attempts = 2) {
  for (let attempt = 1; attempt <= attempts; attempt++) {
    try {
      run(command, args, opts)
      return
    } catch (e) {
      if (attempt === attempts) throw e
      const first = String(e?.message ?? e).split('\n')[0]
      console.log(
        `⚠ ${label} 第 ${attempt} 次失败，10 秒后重试一次（多为 Electron 二进制下载瞬时故障）：${first}`,
      )
      sleepSync(10_000)
    }
  }
}

function mb(bytes) {
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/** 读取 PE 头 Machine 字段；非 PE 文件返回 null */
function peMachine(file) {
  const fd = fs.openSync(file, 'r')
  try {
    const buf = Buffer.alloc(0x400)
    fs.readSync(fd, buf, 0, buf.length, 0)
    if (buf.readUInt16LE(0) !== 0x5a4d) return null // 'MZ'
    const peOff = buf.readUInt32LE(0x3c)
    const head = Buffer.alloc(6)
    fs.readSync(fd, head, 0, 6, peOff)
    if (head.toString('latin1', 0, 4) !== 'PE\0\0') return null
    return head.readUInt16LE(4)
  } finally {
    fs.closeSync(fd)
  }
}

/**
 * 校验每个架构解包目录里的主程序 PE 架构。
 * 这是「安装包标识」与「安装包内容」一致性的硬门禁：宁可构建失败，
 * 也不发出一个名字写 arm64、里面装 x64 的包。
 */
function verifyUnpackedArch(arch) {
  // electron-builder 的解包目录命名：x64 是 win-unpacked，其余带架构后缀
  const dirName = arch.electronArch === 'x64' ? 'win-unpacked' : `win-${arch.electronArch}-unpacked`
  const unpacked = path.join(RELEASE, `arch-${arch.label}`, dirName)
  const exe = path.join(unpacked, 'WinShare Panel.exe')
  if (!fs.existsSync(exe)) {
    throw new Error(`未找到解包主程序，无法校验架构：${exe}`)
  }
  const machine = peMachine(exe)
  const expected = PE_MACHINE[arch.electronArch]
  if (machine !== expected) {
    throw new Error(
      `架构校验失败：${exe} 的 PE Machine=0x${(machine ?? 0).toString(16)}，` +
        `期望 0x${expected.toString(16)}（${arch.electronArch}）——产物与标识不符，已中止`,
    )
  }
  return { exe, machine: `0x${machine.toString(16)}` }
}

function sha256(file) {
  const h = crypto.createHash('sha256')
  h.update(fs.readFileSync(file))
  return h.digest('hex')
}

function main() {
  const opts = parseArgs(process.argv.slice(2))
  const pkg = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'))
  const version = pkg.version
  if (opts.help) {
    console.log(
      '用法：node scripts/build-win.mjs [--arch x86|x64|arm64]... [--targets nsis,portable,msi] [--skip-vite]\n' +
        '  缺省打全部三个架构；每个架构单独打包，产物名带 x86/x64/arm64 标识。',
    )
    return
  }

  // 清掉「本次要构建的架构 + 同一版本号」的旧产物，避免上一轮残留混进校验和与上传清单。
  // 只清本次构建的架构：否则单独重建一个架构会顺手删掉其他架构刚打好的包
  // （实测踩过：只重建 x86 后 SHA256SUMS 从 8 条变成 3 条，x64/arm64 产物已被删）。
  // 其他版本号的产物一概不动，历史包仍有据可查。
  fs.mkdirSync(RELEASE, { recursive: true })
  const labels = opts.arches.map((k) => ARCHES[k].label)
  const cleanedVersions = []
  for (const name of fs.readdirSync(RELEASE)) {
    if (!name.includes(`-${version}-`)) continue
    if (!labels.some((l) => name.includes(`-${l}.`))) continue
    fs.rmSync(path.join(RELEASE, name), { force: true })
    cleanedVersions.push(name)
  }
  if (cleanedVersions.length) {
    console.log(`已清理 ${cleanedVersions.length} 个同版本旧产物（本次构建：${labels.join(', ')}）`)
  }

  const electronViteBin = path.join(
    ROOT,
    'node_modules',
    'electron-vite',
    'bin',
    'electron-vite.js',
  )
  const electronBuilderBin = path.join(
    ROOT,
    'node_modules',
    'electron-builder',
    'out',
    'cli',
    'cli.js',
  )
  for (const bin of [electronViteBin, electronBuilderBin]) {
    if (!fs.existsSync(bin)) throw new Error(`缺少构建入口：${bin}（先 pnpm install）`)
  }

  if (!opts.skipVite) {
    console.log('▶ electron-vite build（渲染层/主进程/预加载，与架构无关，只做一次）')
    run(process.execPath, [electronViteBin, 'build'])
  }

  const summary = []
  for (const key of opts.arches) {
    const arch = ARCHES[key]
    const targets = opts.targets
      ? arch.targets.filter((t) => opts.targets.includes(t))
      : arch.targets
    if (!targets.length) {
      throw new Error(`${arch.label} 没有可打的 target（请求：${opts.targets?.join(',')}）`)
    }
    const outDir = path.join(RELEASE, `arch-${arch.label}`)
    fs.rmSync(outDir, { recursive: true, force: true })
    console.log(
      `\n▶ 打包 ${arch.label}（Electron ${arch.electronArch}，targets: ${targets.join(', ')}）`,
    )
    runPackagingWithRetry(
      `${arch.label} 打包`,
      process.execPath,
      [
        electronBuilderBin,
        '--win',
        ...targets,
        arch.flag,
        `-c.directories.output=${outDir}`,
        // 不让 electron-builder 自己发布：CI 有 GH_TOKEN 时会自动上传，与 release.yml 的上传步骤重复
        '--publish',
        'never',
      ],
      // 架构标识注入：artifactName 里的 ${env.WINSHARE_ARCH_LABEL}
      { env: { ...process.env, WINSHARE_ARCH_LABEL: arch.label } },
    )

    const { exe, machine } = verifyUnpackedArch(arch)
    console.log(
      `✓ ${arch.label} 解包主程序架构校验通过：${path.basename(exe)} PE Machine=${machine}`,
    )

    // 收敛产物到 release/ 根（CI 的 release/*.exe|*.msi 通配与用户直觉都以根目录为准）
    const collected = []
    for (const name of fs.readdirSync(outDir)) {
      if (!/\.(exe|msi|blockmap)$/i.test(name)) continue
      const from = path.join(outDir, name)
      if (!fs.statSync(from).isFile()) continue
      const to = path.join(RELEASE, name)
      fs.copyFileSync(from, to)
      collected.push(name)
    }
    summary.push({ arch, collected })
  }

  // === SHA-256 校验和（只覆盖本版本安装包本体，不含 blockmap）===
  const all = fs.readdirSync(RELEASE)
  const isInstaller = (n) =>
    /\.(exe|msi)$/i.test(n) && /-Setup-|-portable-|-(x86|x64|arm64)\.msi$/i.test(n)
  const installers = all.filter((n) => n.includes(`-${version}-`) && isInstaller(n)).sort()
  const stale = all.filter((n) => isInstaller(n) && !n.includes(`-${version}-`)).sort()
  if (stale.length) {
    console.log(`\n注意：release/ 里另有 ${stale.length} 个历史版本产物（未纳入本次校验和/清单）：`)
    for (const n of stale) console.log(`  - ${n}`)
  }
  const lines = installers.map((n) => `${sha256(path.join(RELEASE, n))}  ${n}`)
  const sumsPath = path.join(RELEASE, 'SHA256SUMS.txt')
  fs.writeFileSync(sumsPath, lines.join('\n') + '\n')

  console.log('\n===== 产物汇总（release/）=====')
  for (const n of installers) {
    const size = fs.statSync(path.join(RELEASE, n)).size
    console.log(`  ${n.padEnd(46)} ${mb(size).padStart(9)}`)
  }
  console.log(`\n校验和已写入 ${path.relative(ROOT, sumsPath)}（${lines.length} 个安装包）`)
  for (const { arch, collected } of summary) {
    if (collected.length === 0) {
      throw new Error(`${arch.label} 未产出任何安装包——构建静默失败，已中止`)
    }
  }
}

main()
