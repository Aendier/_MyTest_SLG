using System.IO;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools.Svn
{
    /// <summary>
    /// SVN 工具入口 - 直接调用 TortoiseSVN GUI
    /// </summary>
    public static class SvnTools
    {
        // 项目根目录：从 Assets 向上查找 SVN 工作副本根（含 .svn 的目录），强制去除尾部斜杠
        private static string ProjectRootPath => FindSvnWorkingCopyRoot();

        /// <summary>
        /// 从 Assets 目录向上逐级查找 SVN 工作副本根（包含 .svn 文件夹的目录）。
        /// 避免写死目录层级，适配不同路径结构；找不到时兜底为 Assets 上两级。
        /// </summary>
        private static string FindSvnWorkingCopyRoot()
        {
            // 从 Assets 的父级（即 Unity 工程目录）开始向上查找
            var dir = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".svn")))
                {
                    return dir.FullName.TrimEnd('\\', '/');
                }
                dir = dir.Parent;
            }

            // 兜底：沿用原有的 Assets 上两级
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../../"))
                .TrimEnd('\\', '/');
        }

        // GameAssets 路径，强制去除尾部斜杠
        private static string GameAssetsPath => Path.GetFullPath(Path.Combine(Application.dataPath, "GameAssets"))
            .TrimEnd('\\', '/');

        /// <summary>
        /// 打开 SVN 提交面板 (GameAssets)
        /// </summary>
        public static void CommitGameAssets()
        {
            if (!Directory.Exists(GameAssetsPath))
            {
                EditorUtility.DisplayDialog("SVN 提交", $"目录不存在:\n{GameAssetsPath}", "确定");
                return;
            }

            SvnHelper.OpenTortoiseGui("commit", GameAssetsPath);
        }

        /// <summary>
        /// 打开 SVN 更新面板 (整个仓库)
        /// </summary>
        public static void UpdateProject()
        {
            if (!Directory.Exists(ProjectRootPath))
            {
                EditorUtility.DisplayDialog("SVN 更新", $"仓库根目录不存在:\n{ProjectRootPath}", "确定");
                return;
            }

            SvnHelper.OpenTortoiseGui("update", ProjectRootPath);
        }
    }
}