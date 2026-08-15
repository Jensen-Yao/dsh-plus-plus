using System;
using System.IO;
using System.Text;

namespace DshControl;

/// <summary>
/// 手机浏览器兼容补丁管理：把 compat-polyfill.js 注入 dsh 网页入口
/// （node_modules/@deepseek-ai/dsh-web-frontend/dist/index.html）。
/// 应用启动与「启动服务」时检查；缺了会自动补（npm 更新覆盖后自愈）。
/// </summary>
public static class FrontendPatch
{
    const string Marker = "dsh-control-compat";

    public static string PolyfillSource()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "compat-polyfill.js");
        if (!File.Exists(p)) return null;
        return File.ReadAllText(p, Encoding.UTF8);
    }

    public static void Ensure(AppConfig cfg, Action<string> log)
    {
        try
        {
            var indexPath = Path.Combine(cfg.DshAppDir, "node_modules", "@deepseek-ai", "dsh-web-frontend", "dist", "index.html");
            if (!File.Exists(indexPath))
            {
                log?.Invoke("兼容补丁：未找到前端入口 " + indexPath);
                return;
            }
            var html = File.ReadAllText(indexPath, Encoding.UTF8);
            if (html.Contains(Marker))
            {
                log?.Invoke("兼容补丁：已存在，跳过");
                return;
            }
            var poly = PolyfillSource();
            if (string.IsNullOrEmpty(poly))
            {
                log?.Invoke("兼容补丁：缺少 compat-polyfill.js，跳过");
                return;
            }
            var inject = "<script>\n" + poly + "\n    </script>\n    ";
            var idx = html.IndexOf("<script type=\"module\"", StringComparison.Ordinal);
            if (idx < 0) idx = html.IndexOf("</head>", StringComparison.Ordinal);
            if (idx < 0)
            {
                log?.Invoke("兼容补丁：index.html 结构异常，跳过");
                return;
            }
            html = html.Insert(idx, inject);
            File.WriteAllText(indexPath, html, new UTF8Encoding(false));
            log?.Invoke("兼容补丁：已注入到网页入口（index.html），旧版手机浏览器可以正常使用了");
        }
        catch (Exception ex)
        {
            log?.Invoke("兼容补丁失败: " + ex.Message);
        }
    }
}
