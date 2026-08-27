using UnityEditor;

namespace SpriteAtlasAutoOrganizer.Editor
{
    /// <summary>
    /// 可取消进度。全量扫描不能卡住主线程还不给取消。
    /// </summary>
    internal static class OrganizerProgress
    {
        public static bool Canceled;

        public static void Reset()
        {
            Canceled = false;
        }

        public static bool Report(string info, float progress)
        {
            if (EditorUtility.DisplayCancelableProgressBar(
                    "Sprite Atlas Auto Organizer",
                    info,
                    progress))
            {
                Canceled = true;
            }

            return Canceled;
        }

        public static void ThrowIfCanceled()
        {
            if (Canceled)
                throw new System.OperationCanceledException("用户取消了分析。");
        }
    }
}
