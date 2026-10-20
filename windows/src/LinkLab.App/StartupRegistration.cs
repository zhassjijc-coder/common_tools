using System.IO;
using Microsoft.Win32;

namespace LinkLab.App;

public interface IStartupRegistration
{
    string ExecutablePath { get; }
    string? Read();
    void Write(string? command);
}

internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LinkLab";
    public string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "LinkLab.exe");
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
    public void Write(string? command)
    {
        if (command is not null && !File.Exists(ExecutablePath)) throw new FileNotFoundException("未找到 LinkLab.exe，请从完整发布目录运行后设置自启动。");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new IOException("无法访问当前用户的启动项。");
        if (command is null) key.DeleteValue(ValueName, false);
        else key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}

internal static class StartupCommand
{
    internal static string Build(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || executablePath.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("自启动需要有效的 EXE 绝对路径。");
        string command = $"\"{Path.GetFullPath(executablePath)}\" --background";
        if (command.Length > 260) throw new ArgumentException("程序路径过长，请将发布目录放到较短的路径后启用自启动。");
        return command;
    }
}
