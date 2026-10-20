using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using LinkLab.Core;

namespace LinkLab.App;

internal sealed class VirtualSerialBackend(Func<string> toolPath) : IVirtualPortBackend
{
    public static string DetectTool() => new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
        .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p!, "com0com", "setupc.exe")).FirstOrDefault(File.Exists) ?? "";

    private string Tool()
    {
        string path = toolPath().Trim();
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("setupc.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new Uri(path).IsUnc)
            throw new FileNotFoundException("请先安装兼容的 com0com 驱动，再选择安装目录中的 setupc.exe（本地绝对路径）。");
        return Path.GetFullPath(path);
    }

    public Task<VirtualPortSnapshot> Read(CancellationToken ct) => Task.Run(() => VirtualPortDevices.Read(ct), ct);

    public async Task<int> Change(IReadOnlyList<string> arguments)
    {
        string path = Tool();
        // Arguments are generated from validated COM names / integer IDs, never from command text supplied by the user.
        var info = new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            // ShellExecute waits for UAC; keep it off the WPF dispatcher so the page stays responsive.
            using var process = await Task.Run(() => Process.Start(info)) ?? throw new IOException("无法启动驱动管理工具。");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("已取消管理员授权，未执行端口管理操作。", ex); }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 740) { throw new IOException("Windows 未允许启动驱动管理工具，请重试并在管理员授权窗口中允许此次创建或删除。", ex); }
    }
}
