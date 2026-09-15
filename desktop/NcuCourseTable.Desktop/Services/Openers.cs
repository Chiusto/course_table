using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// “点击跳转”帮助类：教室地图搜索、打开门户链接、打开数据目录。
/// 使用默认浏览器 / 资源管理器打开（原生跳转，不内嵌 WebView）。
/// </summary>
public static class Openers
{
    /// <summary>高德地图地点搜索（编码地点关键词，包含校区与楼栋，命中率高）。</summary>
    public static string MapSearchUrl(string keyword)
    {
        string kw = string.IsNullOrWhiteSpace(keyword) ? "南昌大学" : keyword.Trim();
        return "https://uri.amap.com/search?keyword=" + Uri.EscapeDataString(kw);
    }

    public static void OpenMap(Course course)
    {
        string kw = course.Room;
        if (string.IsNullOrWhiteSpace(kw))
        {
            kw = course.Name; // 无地点时按课程名搜索
        }
        OpenUrl(MapSearchUrl(kw));
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"打开链接失败：{ex.Message}", "跳转失败");
        }
    }

    public static void OpenDataDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"打开数据目录失败：{ex.Message}", "操作失败");
        }
    }

    public static void OpenLinkFromStore(StoreData data, string key, string fallbackUrl)
    {
        if (data.Links.TryGetValue(key, out string? url) && !string.IsNullOrWhiteSpace(url))
        {
            OpenUrl(url);
        }
        else
        {
            OpenUrl(fallbackUrl);
        }
    }
}
