using System.IO;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools.Git
{
    /// <summary>
    /// Git 工具入口 - 直接调用 UGit GUI
    /// </summary>
    public static class GitTools
    {
        // 项目根目录：从 Assets 向上查找 Git 仓库根（含 .git 的目录），强制去除尾部斜杠
        private static string ProjectRootPath => FindGitWorkingCopyRoot();

        /// <summary>
        /// 从 Assets 目录向上逐级查找 Git 仓库根（包含 .git 文件夹或 gitdir 文件）。
        /// 避免写死目录层级，适配不同路径结构；找不到时兜底为 Assets 上两级。
        /// </summary>
        private static string FindGitWorkingCopyRoot()
        {
            // 从 Assets 的父级（即 Unity 工程目录）开始向上查找
            var dir = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));

            while (dir != null)
            {
                if (IsGitRoot(dir.FullName))
                {
                    return dir.FullName.TrimEnd('\\', '/');
                }
                dir = dir.Parent;
            }

            // 兜底：沿用原有的 Assets 上两级
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../../"))
                .TrimEnd('\\', '/');
        }

        /// <summary>
        /// .git 可能是目录（普通仓库）或文件（worktree / submodule）。
        /// </summary>
        private static bool IsGitRoot(string dirPath)
        {
            string gitPath = Path.Combine(dirPath, ".git");
            return Directory.Exists(gitPath) || File.Exists(gitPath);
        }

        // GameAssets 路径，强制去除尾部斜杠
        private static string GameAssetsPath => Path.GetFullPath(Path.Combine(Application.dataPath, "GameAssets"))
            .TrimEnd('\\', '/');

        /// <summary>
        /// 打开 UGit 提交面板 (GameAssets)
        /// </summary>
        public static void CommitGameAssets()
        {
            if (!Directory.Exists(GameAssetsPath))
            {
                EditorUtility.DisplayDialog("Git 提交", $"目录不存在:\n{GameAssetsPath}", "确定");
                return;
            }

            UGitHelper.OpenUGitGui("commit", GameAssetsPath);
        }

        /// <summary>
        /// 打开 UGit 更新面板 (整个仓库)
        /// </summary>
        public static void UpdateProject()
        {
            if (!Directory.Exists(ProjectRootPath))
            {
                EditorUtility.DisplayDialog("Git 更新", $"仓库根目录不存在:\n{ProjectRootPath}", "确定");
                return;
            }

            UGitHelper.OpenUGitGui("pull", ProjectRootPath);
        }
    }
}
