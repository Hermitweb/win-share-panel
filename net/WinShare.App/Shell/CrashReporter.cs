using System.Windows;

namespace WinShare.App.Shell;

/// <summary>
/// 崩溃上报（对齐 electron/main.ts crashReporter.start：本地落盘、不上服务器）。
/// 挂三个面：UI 线程 DispatcherUnhandledException、线程池/未观察 Task、AppDomain 终极兜底。
/// </summary>
public sealed class CrashReporter
{
    readonly Action<string>? _onUiThread;

    public CrashReporter(Action<string>? onUiThread = null)
    {
        _onUiThread = onUiThread;
        Application.Current?.DispatcherUnhandledException += (_, e) =>
        {
            Log("crash-reporter", "UI 线程未处理异常：" + e.Exception);
            e.Handled = true; // 应用级兜底：记录后不崩（控制面板常驻托盘语义）
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("crash-reporter", "未观察 Task 异常：" + e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("crash-reporter", "AppDomain 未处理异常：" + e.ExceptionObject);
    }

    public static void Log(string scope, string message)
    {
        LogSink.Error(scope, message);
    }

    /// <summary>UI 线程专用日志出口（供托盘气泡提示崩溃已记录）。</summary>
    public void ReportToUi(string message) => _onUiThread?.Invoke(message);
}
