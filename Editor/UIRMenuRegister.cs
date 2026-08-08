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
        
        [MenuItem("UIR/提交资源", false, 100)]
        private static void Menu_SvnCommit()
        {
            SvnTools.CommitGameAssets();
        }

        [MenuItem("UIR/更新工程", false, 101)]
        private static void Menu_SvnUpdate()
        {
            SvnTools.UpdateProject();
        }

        // ==================== UI 工具 ====================

        [MenuItem("UIR/Create UI Folder", false, 200)]
        private static void Menu_CreateUIFolder()
        {
            UIFolderCreatorWindow.Open();
        }

        // ==================== 图片工具 ====================

        [MenuItem("UIR/图搜图", false, 300)]
        private static void Menu_ImageSimilaritySearch()
        {
            ImageSimilaritySearchWindow.Open();
        }

        [MenuItem("UIR/Game 截图", false, 301)]
        private static void Menu_GameViewScreenshot()
        {
            GameViewScreenshotWindow.Open();
        }

        [MenuItem("UIR/SpriteAtlas 拆分", false, 302)]
        private static void Menu_SpriteAtlasSplitter()
        {
            SpriteAtlasSplitterWindow.Open();
        }

        [MenuItem("UIR/图片降尺寸", false, 303)]
        private static void Menu_TextureDownscale()
        {
            TextureDownscaleWindow.Open();
        }
    }
}