using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UIR.EditorTools.Svn
{
    /// <summary>
    /// TortoiseSVN GUI 调用器
    /// 前提：已安装 TortoiseSVN 并勾选了 "command line client tools" (TortoiseProc)
    /// </summary>
    public static class SvnHelper
    {
        // TortoiseProc 默认安装路径，如果不在 PATH 中则尝试常见位置
        private static string TortoiseProcPath
        {
            get
            {
                // 优先从环境变量找
                string path = "TortoiseProc.exe";
                
                // 如果找不到，尝试常见安装路径
                string[] commonPaths = new[]
                {
                    @"C:\Program Files\TortoiseSVN\bin\TortoiseProc.exe",
                    @"C:\Program Files (x86)\TortoiseSVN\bin\TortoiseProc.exe"
                };

                foreach (var p in commonPaths)
                {
                    if (File.Exists(p)) return p;
                }

                return path; // 兜底，让系统自己找
            }
        }

        /// <summary>
        /// 打开 TortoiseSVN GUI 窗口
        /// </summary>
        /// <param name="command">svn 命令，如 commit, update</param>
        /// <param name="targetPath">目标路径（不带尾部斜杠）</param>
        public static void OpenTortoiseGui(string command, string targetPath)
        {
            // 强制去除尾部斜杠，防止 TortoiseProc 解析异常
            string cleanPath = targetPath.TrimEnd('\\', '/');

            string arguments = $"/command:{command} /path:\"{cleanPath}\" /notempfile";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = TortoiseProcPath,
                    Arguments = arguments,
                    UseShellExecute = true // 必须为 true 才能弹出 GUI
                };

                Process.Start(startInfo);
                Debug.Log($"[UIR/Svn] 已打开 TortoiseSVN {command} 面板: {cleanPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[UIR/Svn] 无法启动 TortoiseSVN: {e.Message}\n请确认已安装 TortoiseSVN。");
                UnityEditor.EditorUtility.DisplayDialog(
                    "TortoiseSVN 未找到",
                    "无法启动 TortoiseSVN 图形界面。\n\n请确认：\n1. 已安装 TortoiseSVN\n2. 安装时勾选了 'command line client tools'",
                    "确定"
                );
            }
        }
    }
}