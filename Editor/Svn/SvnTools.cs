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
        // 项目根目录 (slggz)，强制去除尾部斜杠
        private static string ProjectRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, "../../"))
            .TrimEnd('\\', '/');

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