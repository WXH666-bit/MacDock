using System.Runtime.CompilerServices;
using NLog;
using NLog.Config;

namespace MacDock.Tests;

internal static class TestAssembly
{
    /// <summary>测试中的预期故障不应写入用户真实的 %APPDATA%\MacDock\logs。</summary>
    [ModuleInitializer]
    internal static void InitializeLogging()
        => LogManager.Configuration = new LoggingConfiguration();
}
