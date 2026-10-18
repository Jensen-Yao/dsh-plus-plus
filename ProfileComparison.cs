namespace DshControl;

/// <summary>Compares direct community package declarations and installed versions without copying either profile.</summary>
public record ProfileDifference(string Name, string WebVersion, string DesktopVersion, string State, string Detail, bool Different)
{
    internal static List<ProfileDifference> Compare(IEnumerable<PluginItem> plugins)
    {
        return plugins.Where(item => !item.IsIndirect && !item.IsCore && item.Name is not ("electron" or "playwright-core")
                && item.Profile is "web" or "desktop")
            .GroupBy(item => item.Name).OrderBy(group => group.Key).Select(group =>
            {
                var web = group.FirstOrDefault(item => item.Profile == "web");
                var desktop = group.FirstOrDefault(item => item.Profile == "desktop");
                var missing = web == null || desktop == null || web.Installed.Length == 0 || desktop.Installed.Length == 0;
                var state = web == null ? "仅桌面版" : desktop == null ? "仅 Web" : missing ? "未安装"
                    : web.Installed != desktop.Installed ? "版本不同" : web.Spec != desktop.Spec ? "声明不同" : "一致";
                return new ProfileDifference(group.Key, web?.Installed is { Length: > 0 } w ? w : "未安装",
                    desktop?.Installed is { Length: > 0 } d ? d : "未安装", state,
                    "Web 声明：" + (web?.Spec ?? "无") + "\n桌面声明：" + (desktop?.Spec ?? "无"), state != "一致");
            }).ToList();
    }
}
