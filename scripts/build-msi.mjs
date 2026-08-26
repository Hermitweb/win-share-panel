#!/usr/bin/env node
/**
 * 用 wixl（msitools）从 electron-builder 的 win-unpacked 目录生成 MSI 安装包。
 *
 * 背景：electron-builder 的 msi target 依赖 WiX3（32 位 .NET 程序，需 wine 运行 32 位代码），
 * 在无 IA32 内核支持的 Linux 沙箱中不可用。本脚本直接生成 WiX 源文件并调用原生 wixl 完成打包，
 * 产物命名与 electron-builder.yml 中 msi.artifactName 保持一致：
 *   ${productName}-${version}-${arch}.msi
 *
 * 用法：node scripts/build-msi.mjs [--arch x64|ia32]
 */
import fs from 'node:fs'
import path from 'node:path'
import crypto from 'node:crypto'
import { execFileSync } from 'node:child_process'

const args = process.argv.slice(2)
let arch = 'x64'
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--arch') arch = args[++i]
}

const pkg = JSON.parse(fs.readFileSync('package.json', 'utf8'))
const version = pkg.version
const productName = 'WinShare Panel'
const manufacturer = pkg.author || productName
const description = (pkg.description || productName).replace(/"/g, '')
// wixl 0.103 对 Feature/Shortcut 等 Description 列的非 ASCII 内容会崩溃，
// 这些列使用 ASCII 描述；Package 级描述保留中文。
const descriptionEn = 'Windows File Sharing Control Panel'
const win64 = arch === 'x64'
const unpackedDir = path.resolve(
  arch === 'ia32' ? 'release/win-ia32-unpacked' : 'release/win-unpacked'
)
const outMsi = path.resolve('release', `${productName}-${version}-${arch}.msi`)
const wxsPath = path.resolve('release', `msi-${arch}.wxs`)

if (!fs.existsSync(unpackedDir)) {
  console.error(`未找到 ${unpackedDir}，请先运行 electron-builder 构建`)
  process.exit(1)
}

function guidFor(seed) {
  const h = crypto.createHash('sha1').update(`winshare-panel-msi/${seed}`).digest()
  h[6] = (h[6] & 0x0f) | 0x50
  h[8] = (h[8] & 0x3f) | 0x80
  const hex = h.toString('hex')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20, 32)}`.toUpperCase()
}

const upgradeCode = guidFor(`upgradecode/${arch}`)

function listFiles(dir, base = '') {
  const out = []
  for (const name of fs.readdirSync(dir).sort()) {
    const full = path.join(dir, name)
    const rel = base ? `${base}/${name}` : name
    if (fs.statSync(full).isDirectory()) out.push(...listFiles(full, rel))
    else out.push(rel)
  }
  return out
}

const files = listFiles(unpackedDir)
const componentIds = []
let dirCounter = 0
const dirIdMap = new Map()

function directoryTree(dir, base = '') {
  // 返回该目录下子目录 + 文件的 XML（递归）
  let xml = ''
  for (const name of fs.readdirSync(dir).sort()) {
    const full = path.join(dir, name)
    const rel = base ? `${base}/${name}` : name
    if (fs.statSync(full).isDirectory()) {
      const id = `D${dirCounter++}`
      dirIdMap.set(rel, id)
      xml += `<Directory Id="${id}" Name="${name}">\n${directoryTree(full, rel)}</Directory>\n`
    }
  }
  for (const name of fs.readdirSync(dir).sort()) {
    const full = path.join(dir, name)
    if (!fs.statSync(full).isFile()) continue
    const rel = base ? `${base}/${name}` : name
    const cid = `C${componentIds.length}`
    componentIds.push(cid)
    xml += `<Component Id="${cid}" Guid="${guidFor(`component/${arch}/${rel}`)}"${
      win64 ? ' Win64="yes"' : ''
    }><File Id="F${componentIds.length}" Source="${full.replace(/"/g, '')}" KeyPath="yes"/></Component>\n`
  }
  return xml
}

const treeXml = directoryTree(unpackedDir)
const installDirName = 'WinShare Panel'
const programFilesId = win64 ? 'ProgramFiles64Folder' : 'ProgramFilesFolder'

const wxs = `<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
  <Product Id="*" Name="${productName} ${version}" Language="1033" Version="${version}" Manufacturer="${manufacturer}" UpgradeCode="${upgradeCode}">
    <Package InstallerVersion="200" Compressed="yes" InstallScope="perMachine" Description="${description}" Comments="${description}"/>
    <MajorUpgrade AllowDowngrades="yes"/>
    <Media Id="1" Cabinet="app.cab" EmbedCab="yes"/>
    <Icon Id="app.ico" SourceFile="${path.resolve('resources/icon.ico')}"/>
    <Property Id="ARPPRODUCTICON" Value="app.ico"/>
    <Property Id="DISABLEADVTSHORTCUTS" Value="1"/>
    <Directory Id="TARGETDIR" Name="SourceDir">
      <Directory Id="${programFilesId}">
        <Directory Id="INSTALLDIR" Name="${installDirName}">
${treeXml}        </Directory>
      </Directory>
      <Directory Id="ProgramMenuFolder">
        <Directory Id="StartMenuDir" Name="${productName}">
          <Component Id="StartMenuShortcut" Guid="${guidFor(`shortcut/startmenu/${arch}`)}"${win64 ? ' Win64="yes"' : ''}>
            <Shortcut Id="AppStartMenuShortcut" Name="${productName}" Description="${descriptionEn}" Target="[INSTALLDIR]${productName}.exe" WorkingDirectory="INSTALLDIR" Icon="app.ico"/>
            <RemoveFolder Id="RemoveStartMenuDir" On="uninstall"/>
            <RegistryValue Root="HKLM" Key="Software\\${productName}" Name="startmenu" Type="integer" Value="1" KeyPath="yes"/>
          </Component>
        </Directory>
      </Directory>
      <Directory Id="DesktopFolder">
        <Component Id="DesktopShortcut" Guid="${guidFor(`shortcut/desktop/${arch}`)}"${win64 ? ' Win64="yes"' : ''}>
          <Shortcut Id="AppDesktopShortcut" Name="${productName}" Description="${descriptionEn}" Target="[INSTALLDIR]${productName}.exe" WorkingDirectory="INSTALLDIR" Icon="app.ico"/>
          <RegistryValue Root="HKLM" Key="Software\\${productName}" Name="desktop" Type="integer" Value="1" KeyPath="yes"/>
        </Component>
      </Directory>
    </Directory>
    <Feature Id="Main" Level="1" Title="${productName}" Description="${descriptionEn}" Absent="disallow">
      ${componentIds.map(id => `<ComponentRef Id="${id}"/>`).join('\n      ')}
      <ComponentRef Id="StartMenuShortcut"/>
      <ComponentRef Id="DesktopShortcut"/>
    </Feature>
  </Product>
</Wix>
`

fs.writeFileSync(wxsPath, wxs)
console.log(`生成 ${wxsPath}（${componentIds.length} 个文件组件）`)

execFileSync('wixl', ['-v', '-a', win64 ? 'x64' : 'x86', '-o', outMsi, wxsPath], {
  stdio: 'inherit',
})
console.log(`已生成 ${outMsi}`)
