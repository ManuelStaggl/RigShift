using RigShift.Windows.Apps;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class ProcessAppLauncherTests
{
    [Theory]
    [InlineData("C:\\SimHub\\SimHubWPF.exe", "C:\\SimHub\\SimHubWPF.exe", true)]
    [InlineData("C:\\SimHub\\SimHubWPF.exe", "c:\\simhub\\SIMHUBWPF.EXE", true)]
    [InlineData("C:\\SimHub\\..\\SimHub\\SimHubWPF.exe", "C:\\SimHub\\SimHubWPF.exe", true)]
    [InlineData("C:\\SimHub\\SimHubWPF.exe", "D:\\Other\\SimHubWPF.exe", false)]
    [InlineData("C:\\SimHub\\SimHubWPF.exe", null, true)]
    [InlineData("C:\\SimHub\\SimHubWPF.exe", "", true)]
    public void IsSameExecutable_ComparesFullPathsAndKeepsUnreadableOnes(string configured, string? running, bool same) =>
        ProcessAppLauncher.IsSameExecutable(configured, running).ShouldBe(same);
}
