using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UIR.EditorTools.Git
{
    /// <summary>
    /// UGit GUI 调用器
    /// 前提：已安装腾讯 UGit（https://ugit.qq.com）
    /// </summary>
    public static class UGitHelper
    {
        // UGit 默认安装路径；找不到时再交给系统 PATH
        private static string UGitExePath
        {
            get
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] commonPaths =
                {
                    Path.Combine(localAppData, @"UGit\UGit.exe"),
                    Path.Combine(localAppData, @"ugit\UGit.exe"),
                    Path.Combine(localAppData, @"Programs\UGit\UGit.exe"),
                    Path.Combine(localAppData, @"Programs\ugit\UGit.exe"),
                    @"C:\Program Files\UGit\UGit.exe",
                    @"C:\Program Files\Tencent\UGit\UGit.exe",
                    @"C:\Program Files (x86)\UGit\UGit.exe"
                };

                foreach (var p in commonPaths)
                {
                    if (File.Exists(p)) return p;
                }

                // Squirrel 安装会把真实程序放在 app-版本号 目录下
                string squirrelExe = FindSquirrelUGitExe(Path.Combine(localAppData, "UGit"))
                    ?? FindSquirrelUGitExe(Path.Combine(localAppData, "ugit"));
                if (!string.IsNullOrEmpty(squirrelExe)) return squirrelExe;

                return "UGit.exe";
            }
        }

        /// <summary>
        /// 在 Squirrel 的 app-* 目录中查找 UGit.exe。
        /// </summary>
        private static string FindSquirrelUGitExe(string squirrelRoot)
        {
            if (!Directory.Exists(squirrelRoot)) return null;

            string[] appDirs = Directory.GetDirectories(squirrelRoot, "app-*");
            Array.Sort(appDirs);
            for (int i = appDirs.Length - 1; i >= 0; i--)
            {
                string exe = Path.Combine(appDirs[i], "UGit.exe");
                if (File.Exists(exe)) return exe;
            }

            return null;
        }

        /// <summary>
        /// 打开 UGit GUI，定位到目标路径。
        /// </summary>
        /// <param name="command">git 动作，如 commit、pull；用于日志，UGit 以路径打开仓库后由 GUI 完成提交/更新</param>
        /// <param name="targetPath">目标路径（不带尾部斜杠）</param>
        public static void OpenUGitGui(string command, string targetPath)
        {
            // 强制去除尾部斜杠，避免客户端解析异常
            string cleanPath = targetPath.TrimEnd('\\', '/');
            string arguments = $"\"{cleanPath}\"";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = UGitExePath,
                    Arguments = arguments,
                    UseShellExecute = true // 必须为 true 才能弹出 GUI
                };

                Process.Start(startInfo);
                Debug.Log($"[UIR/Git] 已打开 UGit {command} 面板: {cleanPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UIR/Git] 无法启动 UGit: {e.Message}\n请确认已安装 UGit。");
                UnityEditor.EditorUtility.DisplayDialog(
                    "UGit 未找到",
                    "无法启动 UGit 图形界面。\n\n请确认：\n1. 已安装腾讯 UGit\n2. 可从 https://ugit.qq.com 下载安装",
                    "确定"
                );
            }
        }
    }
}
