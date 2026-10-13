using System.Runtime.CompilerServices;
using VRAirBridge.Core;
using Xunit;

namespace VRAirBridge.Tests;

internal static class TestSetup
{
    /// <summary>
    /// 现有测试按简体中文断言文字，所以整个测试程序集固定用 zh-CN；
    /// 需要切换语言的测试放在 <see cref="LanguageSwitchCollection"/> 里串行执行，结束后还原。
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => Loc.SetLanguage(Loc.ChineseSimplified);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageSwitchCollection
{
    public const string Name = "Language switch (serial)";
}
