#!/usr/bin/env node
/**
 * rcedit 兼容替代脚本（纯 JS，基于 resedit，无需 wine）。
 *
 * 用途：在无法运行 32 位 Windows 程序（wine WoW64 不可用）的 Linux 环境下，
 * 替代 electron-builder 调用的 rcedit.exe 完成 PE 资源编辑。
 * 通过在 PATH 前置一个 wine 包装脚本来拦截 rcedit 调用（示例见仓库构建说明）：
 *   case "$1" in
 *     rcedit-ia32.exe / rcedit-x64.exe) shift 后转交本脚本
 *     其余参数原样交给真实 wine
 *
 * 支持的 rcedit 参数（electron-builder 实际使用的子集）：
 *   --set-version-string <key> <value>
 *   --set-file-version <x.y.z.w>
 *   --set-product-version <x.y.z.w>
 *   --set-icon <ico>
 *   --set-requested-execution-level <level>
 */
import fs from 'node:fs'
import { NtExecutable, NtExecutableResource, Resource, Data } from 'resedit'

const { VersionInfo, IconGroupEntry } = Resource
const { RawIconItem } = Data

const args = process.argv.slice(2)
if (args.length < 1) {
  console.error('rcedit-shim: missing target file')
  process.exit(1)
}
const target = args[0]
const strings = {}
let fileVersion = null
let productVersion = null
let iconPath = null
let execLevel = null

for (let i = 1; i < args.length; i++) {
  const a = args[i]
  if (a === '--set-version-string') strings[args[++i]] = args[++i] ?? ''
  else if (a === '--set-file-version') fileVersion = args[++i]
  else if (a === '--set-product-version') productVersion = args[++i]
  else if (a === '--set-icon') iconPath = args[++i]
  else if (a === '--set-requested-execution-level') execLevel = args[++i]
  else {
    console.error(`rcedit-shim: unsupported argument: ${a}`)
    process.exit(1)
  }
}

function parseIco(buf) {
  if (buf.readUInt16LE(0) !== 0 || buf.readUInt16LE(2) !== 1) throw new Error('invalid .ico file')
  const count = buf.readUInt16LE(4)
  const items = []
  for (let i = 0; i < count; i++) {
    const off = 6 + i * 16
    const width = buf[off] === 0 ? 256 : buf[off]
    const height = buf[off + 1] === 0 ? 256 : buf[off + 1]
    const bitCount = buf.readUInt16LE(off + 6)
    const size = buf.readUInt32LE(off + 8)
    const offset = buf.readUInt32LE(off + 12)
    items.push({ width, height, bitCount, data: buf.subarray(offset, offset + size) })
  }
  return items
}

const exe = NtExecutable.from(fs.readFileSync(target))
const res = NtExecutableResource.from(exe)

// ---- 版本信息 ----
if (fileVersion || productVersion || Object.keys(strings).length > 0) {
  let vi = VersionInfo.fromEntries(res.entries)[0]
  if (vi == null) vi = VersionInfo.createEmpty()
  if (fileVersion != null) vi.setFileVersion(fileVersion)
  if (productVersion != null) vi.setProductVersion(productVersion)
  const lang = vi.getAllLanguagesForStringValues()[0] ?? { lang: 1033, codepage: 1200 }
  for (const [key, value] of Object.entries(strings)) vi.setStringValue(lang, key, value)
  vi.outputToResourceEntries(res.entries)
}

// ---- 图标 ----
if (iconPath != null) {
  const items = parseIco(fs.readFileSync(iconPath))
  // 移除既有 RT_ICON / RT_GROUP_ICON，再整体替换
  for (let i = res.entries.length - 1; i >= 0; i--) {
    if (res.entries[i].type === 3 || res.entries[i].type === 14) res.entries.splice(i, 1)
  }
  const icons = items.map(
    it => new RawIconItem(it.data, it.width, it.height, it.bitCount)
  )
  IconGroupEntry.replaceIconsForResource(res.entries, 1, 1033, icons)
}

// ---- 清单（UAC 执行级别）----
if (execLevel != null) {
  const entry = res.entries.find(e => e.type === 24)
  if (entry == null) {
    const xml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="${execLevel}" uiAccess="false"/></requestedPrivileges></security></trustInfo></assembly>`
    res.entries.push({ type: 24, id: 1, lang: 1033, codepage: 0, bin: new Uint8Array(Buffer.from(xml, 'utf8')) })
  } else {
    let xml = Buffer.from(entry.bin).toString('utf8')
    if (/<requestedExecutionLevel[^>]*level="/.test(xml)) {
      xml = xml.replace(/(<requestedExecutionLevel[^>]*level=")[^"]*(")/, `$1${execLevel}$2`)
    } else {
      const trust = `<trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="${execLevel}" uiAccess="false"/></requestedPrivileges></security></trustInfo>`
      xml = xml.replace('</assembly>', `${trust}</assembly>`)
    }
    entry.bin = new Uint8Array(Buffer.from(xml, 'utf8'))
  }
}

res.outputResource(exe)
fs.writeFileSync(target, Buffer.from(exe.generate()))
