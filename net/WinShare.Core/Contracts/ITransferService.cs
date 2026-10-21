namespace WinShare.Core.Contracts;

/// <summary>
/// 设置整体导入导出（对应 electron/services/transfer.ts 导出：
/// exportSettings / importSettings；常量 TRANSFER_FORMAT / TRANSFER_VERSION。
/// 刻意不导出 dashboardHistory 与 journal；导入不信任文件，逐字段收敛）。
/// </summary>
public interface ITransferService
{
    /// <summary>TS: exportSettings()（同步，SettingsBundle JSON 文本）。</summary>
    string ExportSettings();

    /// <summary>TS: importSettings(json)（同步），返回实际应用的字段名单。</summary>
    SettingsImportResult ImportSettings(string json);
}
