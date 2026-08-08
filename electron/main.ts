import { app, BrowserWindow, ipcMain, Tray, Menu, nativeImage, crashReporter } from 'electron'
import { join } from 'path'
import { existsSync, rmSync } from 'fs'
import { registerIpc } from './ipc'
import { prewarmPool, shutdownPool } from './lib/powershellPool'

// 禁用 GPU 着色器磁盘缓存：控制面板应用无需 GPU 缓存，
// 且 Windows 上 GPUCache 目录常因文件锁/Archive 属性导致 "Unable to move the cache: 拒绝访问 (0x5)" 警告
app.commandLine.appendSwitch('disable-gpu-shader-disk-cache')
app.commandLine.appendSwitch('disable-gpu-program-cache')

// 启动前清理残留的 GPUCache 目录，避免旧缓存文件被锁导致创建失败
try {
  rmSync(join(app.getPath('userData'), 'GPUCache'), { recursive: true, force: true })
} catch {
  // 清理失败时忽略——上方开关已禁用 GPU 缓存，不会再尝试创建
}

crashReporter.start({ submitURL: '', uploadToServer: false, compress: true })

let mainWindow: BrowserWindow | null = null
let tray: Tray | null = null
let isQuitting = false

function isWin11(): boolean {
  const ver = process.getSystemVersion?.() || ''
  const m = ver.match(/(\d+)\.(\d+)\.(\d+)/)
  if (!m) return false
  return parseInt(m[3], 10) >= 22000
}

function resolveResource(name: string): string {
  if (app.isPackaged) {
    return join(process.resourcesPath, 'resources', name)
  }
  // dev 模式：__dirname 是 out/main，需回退两层到项目根目录的 resources/
  return join(__dirname, '..', '..', 'resources', name)
}

function createWindow(): void {
  const win11 = isWin11()
  mainWindow = new BrowserWindow({
    width: 1200,
    height: 800,
    minWidth: 960,
    minHeight: 640,
    frame: false,
    show: false,
    icon: resolveResource('icon.ico'),
    backgroundColor: '#F4FAFD',
    autoHideMenuBar: true,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  })

  if (win11) {
    // Win11 亚克力材质
    ;(mainWindow as unknown as { setBackgroundMaterial?: (m: string) => void }).setBackgroundMaterial?.('acrylic')
  }

  mainWindow.on('ready-to-show', () => mainWindow?.show())
  mainWindow.on('maximize', () => mainWindow?.webContents.send('window:maximizeChange', true))
  mainWindow.on('unmaximize', () => mainWindow?.webContents.send('window:maximizeChange', false))
  mainWindow.on('focus', () => mainWindow?.flashFrame(false))
  mainWindow.on('close', (e) => {
    if (!isQuitting) {
      e.preventDefault()
      mainWindow?.hide()
    }
  })

  if (process.env['ELECTRON_RENDERER_URL']) {
    mainWindow.loadURL(process.env['ELECTRON_RENDERER_URL'])
  } else {
    mainWindow.loadFile(join(__dirname, '../renderer/index.html'))
  }
}

function createTray(): void {
  const iconPath = resolveResource('logo.png')
  const image = existsSync(iconPath) ? nativeImage.createFromPath(iconPath) : nativeImage.createEmpty()
  if (!image.isEmpty()) image.resize({ width: 16, height: 16 })
  tray = new Tray(image)
  const menu = Menu.buildFromTemplate([
    { label: '显示主窗口', click: () => mainWindow?.show() },
    { type: 'separator' },
    { label: '退出', click: () => app.quit() }
  ])
  tray.setToolTip('WinShare Panel')
  tray.setContextMenu(menu)
  tray.on('click', () => mainWindow?.show())
}

function registerWindowIpc(): void {
  ipcMain.handle('window:minimize', () => mainWindow?.minimize())
  ipcMain.handle('window:toggleMaximize', (): boolean => {
    if (!mainWindow) return false
    if (mainWindow.isMaximized()) {
      mainWindow.unmaximize()
      return false
    }
    mainWindow.maximize()
    return true
  })
  ipcMain.handle('window:close', () => mainWindow?.hide())
  ipcMain.handle('window:isMaximized', () => !!mainWindow?.isMaximized())
  ipcMain.handle('window:balloon', (_e, title: string, body: string) => {
    if (tray) {
      try {
        tray.displayBalloon({ title, content: body, iconType: 'info' })
      } catch {
        // displayBalloon 不可用时降级为窗口闪烁
        mainWindow?.flashFrame(true)
      }
    } else {
      mainWindow?.flashFrame(true)
    }
  })
}

// 单实例锁：防止多开。用户关闭窗口时程序最小化到托盘，
// 若再次双击桌面图标/启动器，应激活已有实例而非启动新进程。
const gotTheLock = app.requestSingleInstanceLock()

if (!gotTheLock) {
  // 已有实例运行：当前进程退出，由首个实例的 second-instance 处理激活
  app.quit()
} else {
  // 首个实例收到第二实例启动请求：激活并聚焦已有窗口
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore()
      if (!mainWindow.isVisible()) mainWindow.show()
      mainWindow.focus()
    }
  })

  app.whenReady().then(() => {
    registerIpc()
    registerWindowIpc()
    createWindow()
    createTray()
    // 后台预热 PowerShell 进程池（不阻塞首屏）；worker 懒 spawn，预热仅提前起 1 个
    prewarmPool()
  })

  app.on('before-quit', () => {
    isQuitting = true
    tray?.destroy()
    // 关闭常驻 PowerShell worker，避免残留子进程
    shutdownPool()
  })

  // 兜底：before-quit 后再保险一次（幂等），确保无 powershell.exe 残留
  app.on('will-quit', () => {
    shutdownPool()
  })

  app.on('window-all-closed', (e: Event) => {
    // 托盘常驻：关闭所有窗口时不退出应用
    e.preventDefault()
  })
}
