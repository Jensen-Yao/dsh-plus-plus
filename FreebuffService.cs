using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DshControl;

public enum FreebuffState
{
    Unknown,
    Starting,
    Running,
    Stopped,
    DockerUnavailable,
    Error,
}

public sealed class FreebuffStatus
{
    public FreebuffState State { get; init; }
    public string Message { get; init; } = "";
    public bool DockerReady { get; init; }
    public bool ContainerReady { get; init; }
}

/// <summary>
/// Controls the Freebuff2API Compose project without changing dsh or Codex defaults.
/// </summary>
public sealed class FreebuffService
{
    public const int DefaultHostPort = 18080;
    public const string ApiKey = "fb-local-key";

    readonly Action<string> log;
    readonly string projectDir;
    int operationInProgress;

    public FreebuffService(string projectDir, Action<string> log = null)
    {
        this.projectDir = projectDir ?? "";
        this.log = log;
    }

    string EnvPath => Path.Combine(projectDir, ".env");
    string ComposePath => Path.Combine(projectDir, "compose.yaml");
    string DockerDesktopPath => @"E:\DockerDesktop\Docker Desktop.exe";

    public int ConfiguredPort
    {
        get
        {
            var value = ReadEnvValue("FREEBUFF_PORT");
            return int.TryParse(value, out var port) && port >= 1 && port <= 65535 ? port : DefaultHostPort;
        }
    }

    public string ConfiguredBaseUrl => $"http://127.0.0.1:{ConfiguredPort}/v1";

    public string ConfiguredApiKey => ReadEnvValue("FREEBUFF_API_KEY") ?? ApiKey;

    void Log(string message)
    {
        try { log?.Invoke("[Freebuff] " + message); } catch { }
    }

    public bool IsOperationInProgress => Volatile.Read(ref operationInProgress) != 0;

    public FreebuffStatus GetStatus()
    {
        if (!Directory.Exists(projectDir))
            return new FreebuffStatus { State = FreebuffState.Error, Message = "找不到 F:\\freebuffapi 项目目录" };
        if (!File.Exists(EnvPath))
            return new FreebuffStatus { State = FreebuffState.Error, Message = "找不到 Freebuff .env 凭据文件" };
        if (!File.Exists(ComposePath))
            return new FreebuffStatus { State = FreebuffState.Error, Message = "找不到 Freebuff compose.yaml" };

        var docker = Run("docker", new[] { "info" }, 12000);
        if (docker.ExitCode != 0)
            return new FreebuffStatus
            {
                State = FreebuffState.DockerUnavailable,
                Message = "Docker Desktop 未就绪",
                DockerReady = false,
            };

        var container = Run("docker", new[] { "inspect", "--format", "{{.State.Status}}", "freebuff2api" }, 8000);
        var state = container.StdOut.Trim();
        if (container.ExitCode == 0 && string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
            return new FreebuffStatus
            {
                State = FreebuffState.Running,
                Message = "Freebuff2API 正在运行",
                DockerReady = true,
                ContainerReady = true,
            };

        return new FreebuffStatus
        {
            State = FreebuffState.Stopped,
            Message = "Freebuff2API 未启动",
            DockerReady = true,
            ContainerReady = false,
        };
    }

    public FreebuffStatus Start()
    {
        if (Interlocked.Exchange(ref operationInProgress, 1) != 0)
            return new FreebuffStatus { State = FreebuffState.Starting, Message = "Freebuff 正在处理中" };

        try
        {
            if (!Directory.Exists(projectDir) || !File.Exists(EnvPath) || !File.Exists(ComposePath))
                return GetStatus();

            if (GetStatus().State == FreebuffState.Running)
                return GetStatus();

            if (!IsDockerReady())
            {
                if (!File.Exists(DockerDesktopPath))
                    return new FreebuffStatus { State = FreebuffState.DockerUnavailable, Message = "找不到 E:\\DockerDesktop\\Docker Desktop.exe" };

                Log("Docker 引擎未就绪，正在启动 Docker Desktop...");
                try { Process.Start(new ProcessStartInfo(DockerDesktopPath) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    return new FreebuffStatus { State = FreebuffState.DockerUnavailable, Message = "启动 Docker Desktop 失败: " + ex.Message };
                }

                var ready = false;
                for (var i = 0; i < 60; i++)
                {
                    Thread.Sleep(2000);
                    if (IsDockerReady()) { ready = true; break; }
                }
                if (!ready)
                    return new FreebuffStatus { State = FreebuffState.DockerUnavailable, Message = "Docker 引擎未能在 120 秒内就绪" };
            }

            Log("正在启动 Freebuff2API...");
            var compose = Run("docker", new[]
            {
                "compose", "--env-file", EnvPath, "-f", ComposePath, "up", "-d", "--build",
            }, 120000);
            if (compose.ExitCode != 0)
            {
                Log("Compose 启动失败: " + ShortError(compose));
                return new FreebuffStatus { State = FreebuffState.Error, Message = "Freebuff 启动失败，请查看运行日志" };
            }

            Log("Freebuff2API 已启动。");
            return GetStatus();
        }
        finally
        {
            Volatile.Write(ref operationInProgress, 0);
        }
    }

    public FreebuffStatus Stop()
    {
        if (Interlocked.Exchange(ref operationInProgress, 1) != 0)
            return new FreebuffStatus { State = FreebuffState.Starting, Message = "Freebuff 正在处理中" };

        try
        {
            if (!File.Exists(ComposePath))
                return GetStatus();

            Log("正在停止 Freebuff2API...");
            var compose = Run("docker", new[] { "compose", "--env-file", EnvPath, "-f", ComposePath, "down" }, 120000);
            if (compose.ExitCode != 0)
            {
                Log("Compose 停止失败: " + ShortError(compose));
                return new FreebuffStatus { State = FreebuffState.Error, Message = "Freebuff 停止失败，请查看运行日志" };
            }
            Log("Freebuff2API 已停止；项目数据未删除。");
            return GetStatus();
        }
        finally
        {
            Volatile.Write(ref operationInProgress, 0);
        }
    }

    bool IsDockerReady() => Run("docker", new[] { "info" }, 12000).ExitCode == 0;

    string ReadEnvValue(string name)
    {
        try
        {
            foreach (var line in File.ReadLines(EnvPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#") || !trimmed.StartsWith(name + "=", StringComparison.Ordinal)) continue;
                var value = trimmed.Substring(name.Length + 1).Trim();
                if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
                    value = value.Substring(1, value.Length - 2);
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch { }
        return null;
    }

    static string ShortError(ProcessResult result)
    {
        var text = (result.StdErr + " " + result.StdOut).Trim().Replace(Environment.NewLine, " ");
        return text.Length > 220 ? text.Substring(0, 220) + "..." : text;
    }

    static ProcessResult Run(string fileName, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process == null) return new ProcessResult(-1, "", "无法启动进程");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return new ProcessResult(-1, stdout.GetAwaiter().GetResult(), "命令超时");
            }
            return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}
