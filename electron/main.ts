import { app, BrowserWindow, ipcMain, Tray, Menu, nativeImage, crashReporter } from 'electron'
import { join } from 'path'
import { existsSync } from 'fs'
import { registerIpc } from './ipc'

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
  return join(__dirname, '..', 'resources', name)
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
      sandbox: false
    }
  })

  if (win11) {
    // Win11 亚克力材质
    ;(mainWindow as unknown as { setBackgroundMaterial?: (m: string) => void }).setBackgroundMaterial?.('acrylic')
  }

  mainWindow.on('ready-to-show', () => mainWindow?.show())
  mainWindow.on('maximize', () => mainWindow?.webContents.send('window:maximizeChange', true))
  mainWindow.on('unmaximize', () => mainWindow?.webContents.send('window:maximizeChange', false))
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
}

app.whenReady().then(() => {
  registerIpc()
  registerWindowIpc()
  createWindow()
  createTray()
})

app.on('before-quit', () => {
  isQuitting = true
  tray?.destroy()
})

app.on('window-all-closed', (e: Event) => {
  e.preventDefault()
})
