// 三个协议适配器测试类的 collection 定义。
// 原因：适配器每个方法都会 Logger.Info（TS 侧 log.info 逐字移植），而 Logger 是进程级全局，
// LoggerTests 用 SetLogDirForTesting + 2MB 轮转做断言——两者并行时适配器日志会与轮转用例写同一个
// app.log，破坏「app.log.1 已生成且 marker 在主文件尾部」的时序（实测全量跑偶发失败）。
// DisableParallelization 让本 collection 独占运行，与 LoggerTests 互不穿插；既有测试一行不改。
using Xunit;

namespace WinShare.Core.Tests.Services.Protocols.Adapters;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProtocolAdaptersCollection
{
    public const string Name = "protocol-adapters-serial";
}
