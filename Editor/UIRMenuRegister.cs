using UnityEditor;
using UIR.EditorTools.Svn;
using UIR.EditorTools.Git;
using ComfyUIUpscaler.Editor;

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

        // ==================== Git 工具（UGit） ====================

        [MenuItem("UIR/Git 提交资源", false, 102)]
        private static void Menu_GitCommit()
        {
            GitTools.CommitGameAssets();
        }

        [MenuItem("UIR/Git 更新工程", false, 103)]
        private static void Menu_GitUpdate()
        {
            GitTools.UpdateProject();
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

        [MenuItem("UIR/ComfyUI 图片批量高清化", false, 302)]
        private static void Menu_ComfyUIUpscaler()
        {
            ComfyUIUpscalerWindow.Open();
        }

        [MenuItem("UIR/同步 Art UI 图集", false, 303)]
        private static void Menu_ArtUISpriteAtlasSync()
        {
            ArtUISpriteAtlasSyncWindow.Open();
        }

        [MenuItem("UIR/同名图片查找合并", false, 305)]
        private static void Menu_SameNameImageFinder()
        {
            SameNameImageFinderWindow.Open();
        }

        [MenuItem("UIR/图片导入设置统一工具", false, 304)]
        private static void Menu_ImageImportSettings()
        {
            ImageImportSettingsWindow.Open();
        }
    }
}
