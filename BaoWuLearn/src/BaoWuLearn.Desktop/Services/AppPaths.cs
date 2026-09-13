namespace BaoWuLearn.Desktop.Services;

/// <summary>
/// 应用数据目录。按各平台惯例放置，日志与设置都落在这里。
/// </summary>
public static class AppPaths
{
    /// <summary>应用数据根目录。</summary>
    public static string DataDirectory
    {
        get
        {
            string dir;
            if (OperatingSystem.IsMacOS())
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "BaoWuLearn");
            }
            else if (OperatingSystem.IsWindows())
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BaoWuLearn");
            }
            else
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "BaoWuLearn");
            }

            try { Directory.CreateDirectory(dir); } catch { /* 目录不可写时由调用方降级处理 */ }
            return dir;
        }
    }

    /// <summary>日志目录。</summary>
    public static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "logs");
            try { Directory.CreateDirectory(dir); } catch { /* 忽略 */ }
            return dir;
        }
    }
}
