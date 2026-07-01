using UnityEditor;
using UIR.EditorTools.Svn;

namespace UIR.EditorTools
{
    /// <summary>
    /// UIR 工具集统一菜单入口
    /// 后续所有 UIR 相关工具均在此处注册菜单项
    /// </summary>
    public static class UIRMenuRegister
    {
        // ==================== SVN 工具 ====================
        
        [MenuItem("UIR/Svn/提交资源", false, 100)]
        private static void Menu_SvnCommit()
        {
            SvnTools.CommitGameAssets();
        }

        [MenuItem("UIR/Svn/更新工程", false, 101)]
        private static void Menu_SvnUpdate()
        {
            SvnTools.UpdateProject();
        }

        // ==================== 未来扩展示例 ====================
        // [MenuItem("UIR/Build/打包Android", false, 200)]
        // private static void Menu_BuildAndroid() { BuildTools.BuildAndroid(); }
    }
}