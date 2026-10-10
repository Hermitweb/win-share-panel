/**
 * 渲染层的项目元信息（唯一落点，避免仓库地址在页面与组件里各写一份）。
 *
 * 注意：主进程侧另有一份等价常量（`electron/services/update.ts` 的 `UPDATE_REPO` / `RELEASES_PAGE`）。
 * 两处刻意不合并——渲染层不能 import 主进程模块（会把 electron 依赖拖进 renderer bundle），
 * 而主进程也不该依赖 src/。改仓库地址时**两处都要改**，这个约束写在这里以免被忘掉。
 */
export const PROJECT_URL = 'https://github.com/Hermitweb/win-share-panel'
export const RELEASES_URL = `${PROJECT_URL}/releases/latest`
export const ISSUES_URL = `${PROJECT_URL}/issues`
