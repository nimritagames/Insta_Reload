using System;

namespace Nimrita.InstaReload
{
    [Flags]
    public enum InstaReloadLogLevel
    {
        None = 0,
        Info = 1 << 0,
        Warning = 1 << 1,
        Error = 1 << 2,
        Verbose = 1 << 3,
        All = Info | Warning | Error | Verbose
    }

    [Flags]
    public enum InstaReloadLogCategory
    {
        None = 0,
        General = 1 << 0,
        Roslyn = 1 << 1,
        FileDetector = 1 << 2,
        Patcher = 1 << 3,
        Suppressor = 1 << 4,
        ChangeAnalyzer = 1 << 5,
        Dispatcher = 1 << 6,
        UI = 1 << 7,
        All = General | Roslyn | FileDetector | Patcher | Suppressor | ChangeAnalyzer | Dispatcher | UI,
        Default = All & ~Dispatcher
    }
}
