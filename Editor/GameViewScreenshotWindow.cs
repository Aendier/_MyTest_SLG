using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace UIR.EditorTools
{
    /// <summary>
    /// Game 窗口截图工具：抓取 Game 视图当前显示内容，
    /// 支持自定义保存路径（默认桌面，记忆上次使用路径）、导出为 PNG 文件，或直接复制到系统剪贴板。
    /// </summary>
    public class GameViewScreenshotWindow : EditorWindow
    {
        // EditorPrefs 键：跨会话记忆上次使用的保存目录 / 文件名前缀 / 是否保存后打开目录。
        private const string PrefKeySavePath = "UIR.GameViewScreenshot.SavePath";
        private const string PrefKeyFilePrefix = "UIR.GameViewScreenshot.FilePrefix";
        private const string PrefKeyOpenAfterSave = "UIR.GameViewScreenshot.OpenAfterSave";

        // 截图完成后的目标动作：保存到文件或复制到剪贴板。
        private enum CaptureAction
        {
            SaveToFile,
            CopyToClipboard,
        }

        private string m_savePath;          // 当前保存目录
        private string m_filePrefix;        // 文件名前缀
        private bool m_openFolderAfterSave; // 保存后是否在资源管理器中打开目录

        private Texture2D m_preview;        // 最近一次截图的预览（本工具创建，需主动销毁）
        private string m_lastMessage;       // 底部状态提示
        private MessageType m_lastMessageType = MessageType.None;

        public static void Open()
        {
            var window = GetWindow<GameViewScreenshotWindow>("Game 截图");
            window.minSize = new Vector2(360f, 420f);
        }

        private void OnEnable()
        {
            // 读取记忆路径；首次使用回退到桌面目录。
            m_savePath = EditorPrefs.GetString(PrefKeySavePath, string.Empty);
            if (string.IsNullOrEmpty(m_savePath) || !Directory.Exists(m_savePath))
            {
                m_savePath = GetDesktopPath();
            }

            m_filePrefix = EditorPrefs.GetString(PrefKeyFilePrefix, "GameView");
            m_openFolderAfterSave = EditorPrefs.GetBool(PrefKeyOpenAfterSave, true);
        }

        private void OnDisable()
        {
            // 释放预览纹理，避免泄漏。
            if (m_preview != null)
            {
                DestroyImmediate(m_preview);
                m_preview = null;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Game 窗口截图", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("直接读取 Game 视图已渲染的合成结果（与窗口所见一致，运行时/非运行时通用）。请确保 Game 窗口已打开并可见。", MessageType.Info);

            EditorGUILayout.Space(6f);
            DrawSavePathField();

            EditorGUILayout.Space(4f);
            // 文件名前缀：实际文件名为 “前缀_时间戳.png”。
            string newPrefix = EditorGUILayout.TextField(new GUIContent("文件名前缀", "实际文件名为：前缀_时间戳.png"), m_filePrefix);
            if (newPrefix != m_filePrefix)
            {
                m_filePrefix = newPrefix;
                EditorPrefs.SetString(PrefKeyFilePrefix, m_filePrefix);
            }

            bool newOpenFlag = EditorGUILayout.Toggle(new GUIContent("保存后打开目录"), m_openFolderAfterSave);
            if (newOpenFlag != m_openFolderAfterSave)
            {
                m_openFolderAfterSave = newOpenFlag;
                EditorPrefs.SetBool(PrefKeyOpenAfterSave, m_openFolderAfterSave);
            }

            EditorGUILayout.Space(10f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("截图并保存到路径", GUILayout.Height(32f)))
                {
                    BeginCapture(CaptureAction.SaveToFile);
                }

                if (GUILayout.Button("截图并复制到剪贴板", GUILayout.Height(32f)))
                {
                    BeginCapture(CaptureAction.CopyToClipboard);
                }
            }

            DrawPreview();
            DrawMessage();
        }

        /// <summary>绘制保存路径行：只读文本 + 选择目录 + 恢复桌面默认。</summary>
        private void DrawSavePathField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("保存路径", GUILayout.Width(70f));
                EditorGUILayout.SelectableLabel(m_savePath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

                if (GUILayout.Button("选择", GUILayout.Width(56f)))
                {
                    string picked = EditorUtility.OpenFolderPanel("选择截图保存目录", m_savePath, string.Empty);
                    if (!string.IsNullOrEmpty(picked))
                    {
                        SetSavePath(picked);
                    }
                }

                if (GUILayout.Button("桌面", GUILayout.Width(48f)))
                {
                    SetSavePath(GetDesktopPath());
                }
            }
        }

        private void DrawPreview()
        {
            if (m_preview == null)
            {
                return;
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField($"预览（{m_preview.width} x {m_preview.height}）", EditorStyles.miniBoldLabel);

            // 按窗口宽度等比缩放预览。
            float maxWidth = position.width - 24f;
            float aspect = (float)m_preview.height / m_preview.width;
            float previewWidth = Mathf.Min(maxWidth, m_preview.width);
            float previewHeight = previewWidth * aspect;
            Rect rect = GUILayoutUtility.GetRect(previewWidth, previewHeight, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(rect, m_preview, ScaleMode.ScaleToFit);
        }

        private void DrawMessage()
        {
            if (string.IsNullOrEmpty(m_lastMessage))
            {
                return;
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(m_lastMessage, m_lastMessageType);
        }

        /// <summary>更新并记忆保存目录。</summary>
        private void SetSavePath(string path)
        {
            m_savePath = path;
            EditorPrefs.SetString(PrefKeySavePath, m_savePath);
        }

        /// <summary>
        /// 发起一次截图。运行时与非运行时统一：直接同步读取 Game 视图已渲染的合成结果，
        /// 避免 ScreenCapture 在编辑器回调中因时机问题导致的黑屏/失败。
        /// </summary>
        private void BeginCapture(CaptureAction action)
        {
            DoCapture(action);
        }

        /// <summary>执行截图并按目标动作处理结果。</summary>
        private void DoCapture(CaptureAction action)
        {
            Texture2D captured = CaptureGameView(out string error);
            if (captured == null)
            {
                SetMessage(string.IsNullOrEmpty(error) ? "截图失败：未能获取 Game 视图内容。" : $"截图失败：{error}", MessageType.Error);
                return;
            }

            try
            {
                UpdatePreview(captured);

                switch (action)
                {
                    case CaptureAction.SaveToFile:
                        SaveToFile(captured);
                        break;
                    case CaptureAction.CopyToClipboard:
                        CopyToClipboard(captured);
                        break;
                }
            }
            finally
            {
                // 预览已复制一份，原始截图纹理可安全释放，避免泄漏。
                if (captured != null && captured != m_preview)
                {
                    DestroyImmediate(captured);
                }
            }

            Repaint();
        }

        /// <summary>统一抓取方式：读取 Game 视图已渲染的合成结果（运行时/非运行时通用）。</summary>
        private static Texture2D CaptureGameView(out string error)
        {
            return CaptureFromGameView(out error);
        }

        /// <summary>
        /// 非运行时抓取：直接读取 Game 视图已经渲染好的结果（含相机堆栈、后处理、合批、Overlay UI 等）。
        /// 通过反射调用 GameView 内部的 RenderView 同步渲染并拿到其 RenderTexture，再读回像素。
        /// 这样能完整还原“Game 窗口里看到的画面”，不依赖具体渲染管线的手动重渲染。
        /// </summary>
        private static Texture2D CaptureFromGameView(out string error)
        {
            error = null;

            Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType == null)
            {
                error = "找不到 UnityEditor.GameView 类型。";
                return null;
            }

            // 获取（或打开）Game 视图窗口，但不抢占焦点。
            EditorWindow gameView = GetWindow(gameViewType, false, null, false);
            if (gameView == null)
            {
                error = "无法获取 Game 视图窗口，请先打开 Game 窗口。";
                return null;
            }

            RenderTexture rt = TryRenderGameView(gameView, gameViewType) ?? TryFindRenderTexture(gameView);
            if (rt == null || rt.width <= 0 || rt.height <= 0)
            {
                error = "读取 Game 视图渲染结果失败，请确保 Game 窗口可见并已渲染（或改用运行时截图）。";
                return null;
            }

            RenderTexture prevActive = RenderTexture.active;
            Texture2D result = null;
            try
            {
                RenderTexture.active = rt;
                result = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
                // 从 RenderTexture 读回的像素在部分图形 API（如 D3D）下是上下翻转的，需竖直翻转还原。
                FlipVertically(result);
                result.Apply(false);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (result != null)
                {
                    DestroyImmediate(result);
                    result = null;
                }
            }
            finally
            {
                RenderTexture.active = prevActive;
            }

            return result;
        }

        /// <summary>将纹理像素上下翻转（原地修改，未调用 Apply，由调用方统一提交）。</summary>
        private static void FlipVertically(Texture2D tex)
        {
            int width = tex.width;
            int height = tex.height;
            Color32[] pixels = tex.GetPixels32();
            var flipped = new Color32[pixels.Length];

            for (int y = 0; y < height; y++)
            {
                int srcRow = y * width;
                int dstRow = (height - 1 - y) * width;
                Array.Copy(pixels, srcRow, flipped, dstRow, width);
            }

            tex.SetPixels32(flipped);
        }

        /// <summary>
        /// 反射调用 GameView/PlayModeView 内部的 RenderView 方法，使其同步渲染并返回结果 RenderTexture。
        /// 参数按类型自动填充（Vector2 传零、bool 传 false），以兼容不同 Unity 版本的方法签名。
        /// </summary>
        private static RenderTexture TryRenderGameView(EditorWindow gameView, Type gameViewType)
        {
            try
            {
                MethodInfo renderView = null;
                for (Type t = gameViewType; t != null && renderView == null; t = t.BaseType)
                {
                    MethodInfo m = t.GetMethod(
                        "RenderView",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                        null,
                        new[] { typeof(Vector2), typeof(bool) },
                        null);
                    if (m != null && m.ReturnType == typeof(RenderTexture))
                    {
                        renderView = m;
                    }
                }

                if (renderView == null)
                {
                    return null;
                }

                object rendered = renderView.Invoke(gameView, new object[] { Vector2.zero, false });
                return rendered as RenderTexture;
            }
            catch
            {
                // 版本差异或调用失败时回退到直接读取字段。
                return null;
            }
        }

        /// <summary>回退方案：沿类型层级扫描 GameView 上已有内容的 RenderTexture 字段/属性。</summary>
        private static RenderTexture TryFindRenderTexture(EditorWindow gameView)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            for (Type t = gameView.GetType(); t != null; t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(flags))
                {
                    if (f.FieldType == typeof(RenderTexture) && f.GetValue(gameView) is RenderTexture rt && rt.width > 0)
                    {
                        return rt;
                    }
                }

                foreach (PropertyInfo p in t.GetProperties(flags))
                {
                    if (p.PropertyType != typeof(RenderTexture) || p.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (p.GetValue(gameView) is RenderTexture rt && rt.width > 0)
                        {
                            return rt;
                        }
                    }
                    catch
                    {
                        // 部分属性读取可能抛异常，忽略继续。
                    }
                }
            }

            return null;
        }

        /// <summary>用最新截图刷新预览纹理（复制一份，避免与被销毁的原纹理生命周期耦合）。</summary>
        private void UpdatePreview(Texture2D source)
        {
            if (m_preview != null)
            {
                DestroyImmediate(m_preview);
                m_preview = null;
            }

            m_preview = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            m_preview.SetPixels32(source.GetPixels32());
            m_preview.Apply(false);
        }

        private void SaveToFile(Texture2D tex)
        {
            try
            {
                if (string.IsNullOrEmpty(m_savePath) || !Directory.Exists(m_savePath))
                {
                    Directory.CreateDirectory(m_savePath);
                }

                string prefix = string.IsNullOrEmpty(m_filePrefix) ? "GameView" : m_filePrefix;
                string fileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string fullPath = Path.Combine(m_savePath, fileName);

                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(fullPath, png);

                SetMessage($"已保存：{fullPath}", MessageType.Info);

                if (m_openFolderAfterSave)
                {
                    EditorUtility.RevealInFinder(fullPath);
                }
            }
            catch (Exception ex)
            {
                SetMessage($"保存失败：{ex.Message}", MessageType.Error);
            }
        }

        private void CopyToClipboard(Texture2D tex)
        {
            if (ClipboardImage.SetImage(tex, out string error))
            {
                SetMessage("已复制截图到剪贴板。", MessageType.Info);
            }
            else
            {
                SetMessage($"复制到剪贴板失败：{error}", MessageType.Error);
            }
        }

        private void SetMessage(string message, MessageType type)
        {
            m_lastMessage = message;
            m_lastMessageType = type;
            Repaint();
        }

        private static string GetDesktopPath()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.IsNullOrEmpty(desktop) ? Directory.GetCurrentDirectory() : desktop;
        }

        /// <summary>
        /// 将纹理写入 Windows 系统剪贴板（CF_DIB 格式，32 位 BGRA、自底向上）。
        /// 仅支持 Windows 编辑器。
        /// </summary>
        private static class ClipboardImage
        {
            public static bool SetImage(Texture2D tex, out string error)
            {
                error = null;

                if (tex == null)
                {
                    error = "截图为空。";
                    return false;
                }

#if UNITY_EDITOR_WIN
                IntPtr hMem = IntPtr.Zero;
                try
                {
                    byte[] dib = BuildDib(tex);

                    // 分配可移动全局内存并写入 DIB 数据（剪贴板会接管该内存的所有权）。
                    hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dib.Length);
                    if (hMem == IntPtr.Zero)
                    {
                        error = "分配剪贴板内存失败。";
                        return false;
                    }

                    IntPtr dst = GlobalLock(hMem);
                    if (dst == IntPtr.Zero)
                    {
                        error = "锁定剪贴板内存失败。";
                        return false;
                    }

                    try
                    {
                        Marshal.Copy(dib, 0, dst, dib.Length);
                    }
                    finally
                    {
                        GlobalUnlock(hMem);
                    }

                    if (!OpenClipboard(IntPtr.Zero))
                    {
                        error = "无法打开剪贴板。";
                        return false;
                    }

                    try
                    {
                        EmptyClipboard();
                        if (SetClipboardData(CF_DIB, hMem) == IntPtr.Zero)
                        {
                            error = "写入剪贴板失败。";
                            return false;
                        }

                        // 成功交给剪贴板后，内存所有权转移，避免下方 finally 释放。
                        hMem = IntPtr.Zero;
                        return true;
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
                finally
                {
                    if (hMem != IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                    }
                }
#else
                error = "当前仅支持 Windows 编辑器复制截图到剪贴板。";
                return false;
#endif
            }

#if UNITY_EDITOR_WIN
            private const uint CF_DIB = 8;
            private const uint GMEM_MOVEABLE = 0x0002;

            [DllImport("user32.dll")]
            private static extern bool OpenClipboard(IntPtr hWndNewOwner);

            [DllImport("user32.dll")]
            private static extern bool CloseClipboard();

            [DllImport("user32.dll")]
            private static extern bool EmptyClipboard();

            [DllImport("user32.dll")]
            private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

            [DllImport("kernel32.dll")]
            private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

            [DllImport("kernel32.dll")]
            private static extern IntPtr GlobalFree(IntPtr hMem);

            [DllImport("kernel32.dll")]
            private static extern IntPtr GlobalLock(IntPtr hMem);

            [DllImport("kernel32.dll")]
            private static extern bool GlobalUnlock(IntPtr hMem);

            /// <summary>
            /// 构建 CF_DIB 数据：BITMAPINFOHEADER(40 字节) + 32 位像素。
            /// biHeight 为正表示自底向上；GetPixels32 返回的首像素即左下角，顺序天然匹配。
            /// </summary>
            private static byte[] BuildDib(Texture2D tex)
            {
                int width = tex.width;
                int height = tex.height;
                Color32[] pixels = tex.GetPixels32();

                const int headerSize = 40;
                int pixelBytes = width * height * 4;
                var dib = new byte[headerSize + pixelBytes];

                // 写入 BITMAPINFOHEADER。
                WriteInt(dib, 0, headerSize);      // biSize
                WriteInt(dib, 4, width);           // biWidth
                WriteInt(dib, 8, height);          // biHeight（正数：自底向上）
                WriteShort(dib, 12, 1);            // biPlanes
                WriteShort(dib, 14, 32);           // biBitCount
                WriteInt(dib, 16, 0);              // biCompression = BI_RGB
                WriteInt(dib, 20, pixelBytes);     // biSizeImage
                // 其余字段（分辨率、调色板）保持 0。

                // 写入像素（BGRA），行顺序与 GetPixels32 一致。
                int offset = headerSize;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 c = pixels[i];
                    dib[offset++] = c.b;
                    dib[offset++] = c.g;
                    dib[offset++] = c.r;
                    dib[offset++] = c.a;
                }

                return dib;
            }

            private static void WriteInt(byte[] buffer, int index, int value)
            {
                buffer[index] = (byte)(value & 0xFF);
                buffer[index + 1] = (byte)((value >> 8) & 0xFF);
                buffer[index + 2] = (byte)((value >> 16) & 0xFF);
                buffer[index + 3] = (byte)((value >> 24) & 0xFF);
            }

            private static void WriteShort(byte[] buffer, int index, short value)
            {
                buffer[index] = (byte)(value & 0xFF);
                buffer[index + 1] = (byte)((value >> 8) & 0xFF);
            }
#endif
        }
    }
}
