using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DshControl;

public record CommandResult(int ExitCode, string Output, bool TimedOut = false, bool Cancelled = false, string StandardOutput = "")
{
    public bool Ok => ExitCode == 0 && !TimedOut && !Cancelled;
}

public static class CommandRunner
{
    /// <summary>Keeps the beginning and end of long command output so failures retain their cause.</summary>
    public static string Brief(string text, int limit = 1600)
    {
        var value = (text ?? "").Replace("\r\n", "\n").Trim();
        if (value.Length <= limit) return value;
        var tail = Math.Clamp(limit / 5, 20, Math.Max(20, limit / 2));
        var head = limit - tail;
        return value[..head] + "\n…输出已截断…\n" + value[^tail..];
    }

    public static string DescribeFailure(string stage, string command, CommandResult result)
    {
        var state = result.TimedOut ? "命令超时" : result.Cancelled ? "命令已取消" : "命令退出码 " + result.ExitCode;
        return $"{stage}失败：{state}。\n命令：{command}\n{Redact(Brief(result.Output))}";
    }

    public static string DescribeHttpStatus(string stage, int statusCode, string reason, long? contentLength)
    {
        var length = contentLength.HasValue ? "，Content-Length " + contentLength.Value : "，响应未声明 Content-Length";
        return $"{stage}失败：HTTP {statusCode} {reason ?? "无状态说明"}{length}。";
    }

    public static string DescribeHashFailure(string stage, string algorithm, string expected, string actual)
        => $"{stage}失败：{algorithm} 校验不一致。\n期望：{expected}\n实际：{actual}";

    public static string DescribeSizeFailure(string stage, long expected, long actual)
        => $"{stage}失败：下载大小不一致。期望 {expected} 字节（{(expected / 1024.0 / 1024).ToString("0.00")} MB），实际 {actual} 字节（{(actual / 1024.0 / 1024).ToString("0.00")} MB）。";

    public static string Redact(string text) => Regex.Replace(text ?? "",
        @"(?i)((?:[\w.-]*token|[\w.-]*api[_-]?key|authorization|password|secret)[\""']?\s*[:=]\s*)(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|(?:Bearer|Basic)\s+[^\s,}]+|[^\s\""',&}]+)", "$1[hidden]");

    public static ProcessStartInfo Create(string executable, IEnumerable<string> arguments, string directory)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static async Task<CommandResult> RunAsync(ProcessStartInfo start, TimeSpan timeout,
        Action<string> progress = null, CancellationToken cancellation = default, Action<Process> started = null)
    {
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var standardOutput = new StringBuilder();
        var launched = false;
        async Task Drain(StreamReader reader, bool standard = false)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (output)
                {
                    output.AppendLine(line);
                    if (standard) standardOutput.AppendLine(line);
                    if (output.Length > 100000) output.Remove(0, output.Length - 80000);
                }
                progress?.Invoke(Redact(line));
            }
        }
        try
        {
            process.Start();
            launched = true;
            var standard = Drain(process.StandardOutput, true);
            var error = Drain(process.StandardError);
            started?.Invoke(process);
            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            var timedOut = false;
            var cancelled = false;
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                cancelled = cancellation.IsCancellationRequested;
                timedOut = !cancelled;
                try { process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(standard, error);
            return new(process.ExitCode, output.ToString().Trim(), timedOut, cancelled, standardOutput.ToString().Trim());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (launched && !process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
            return new(-1, exception.Message);
        }
    }

    public static ProcessStartInfo PackageManager(string manager, IEnumerable<string> arguments, string directory)
    {
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = path.Trim('"');
            var relative = manager == "npm" ? "npm/bin/npm-cli.js" : "pnpm/bin/pnpm.cjs";
            var entry = Path.Combine(folder, "node_modules", relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(entry)) return Create("node", new[] { entry }.Concat(arguments), directory);
        }
        throw new FileNotFoundException($"未找到 {manager} 的 Node 入口，请检查 Node 与 {manager} 的安装位置。");
    }
}
