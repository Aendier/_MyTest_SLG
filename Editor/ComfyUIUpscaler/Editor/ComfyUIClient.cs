using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ComfyUIUpscaler.Editor
{
    internal sealed class ComfyUIClient
    {
        private readonly string baseUrl;
        private readonly int requestTimeoutSeconds;

        public ComfyUIClient(string baseUrl, int requestTimeoutSeconds)
        {
            this.baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            this.requestTimeoutSeconds = Math.Max(1, requestTimeoutSeconds);
            if (!Uri.TryCreate(this.baseUrl, UriKind.Absolute, out _))
                throw new ArgumentException("ComfyUI 地址无效: " + baseUrl);
        }

        // 读取 /system_stats：既验证连接，又解析设备显存，用于内存预估
        public async Task<ComfyDeviceMemory> GetDeviceMemoryAsync(CancellationToken cancellationToken)
        {
            string response = await GetTextAsync("/system_stats", cancellationToken);
            if (!(MiniJson.Deserialize(response) is Dictionary<string, object> root))
                throw new InvalidDataException("ComfyUI /system_stats 返回的不是 JSON 对象。");

            var result = new ComfyDeviceMemory();
            if (root.TryGetValue("devices", out object devicesValue) && devicesValue is List<object> devices)
            {
                foreach (object deviceValue in devices)
                {
                    if (!(deviceValue is Dictionary<string, object> device))
                        continue;
                    long total = ReadLong(device, "vram_total");
                    // 取显存最大的设备（通常是 GPU），忽略无显存信息的设备
                    if (total > result.vramTotalBytes)
                    {
                        result.vramTotalBytes = total;
                        result.vramFreeBytes = ReadLong(device, "vram_free");
                        result.deviceName = device.TryGetValue("name", out object name) ? name as string ?? string.Empty : string.Empty;
                        result.hasVram = total > 0;
                    }
                }
            }
            return result;
        }

        private static long ReadLong(Dictionary<string, object> dictionary, string key)
        {
            if (!dictionary.TryGetValue(key, out object value) || value == null)
                return 0;
            switch (value)
            {
                case long l: return l;
                case int i: return i;
                case double d: return (long)d;
                default: return 0;
            }
        }

        public async Task<ComfyOutputImage> RunPageAsync(
            string inputPath,
            string workflowJson,
            string inputNodeId,
            string inputFieldName,
            string outputNodeId,
            TimeSpan jobTimeout,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            string uploadedName = await UploadImageAsync(inputPath, cancellationToken);
            log?.Invoke("已上传 " + Path.GetFileName(inputPath));
            string promptId = await QueuePromptAsync(
                workflowJson,
                inputNodeId,
                inputFieldName,
                uploadedName,
                cancellationToken);
            log?.Invoke("ComfyUI prompt_id: " + promptId);
            ComfyOutputImage output = await WaitForOutputAsync(
                promptId,
                outputNodeId,
                jobTimeout,
                cancellationToken);
            output.promptId = promptId;
            return output;
        }

        public async Task<byte[]> DownloadAsync(ComfyOutputImage image, CancellationToken cancellationToken)
        {
            string query = "/view?filename=" + UnityWebRequest.EscapeURL(image.filename) +
                           "&subfolder=" + UnityWebRequest.EscapeURL(image.subfolder ?? string.Empty) +
                           "&type=" + UnityWebRequest.EscapeURL(string.IsNullOrEmpty(image.type) ? "output" : image.type);
            using (var request = UnityWebRequest.Get(baseUrl + query))
            {
                await SendAsync(request, cancellationToken);
                EnsureSuccess(request, "/view");
                return request.downloadHandler.data;
            }
        }

        private async Task<string> UploadImageAsync(string path, CancellationToken cancellationToken)
        {
            var form = new WWWForm();
            form.AddBinaryData("image", File.ReadAllBytes(path), Path.GetFileName(path), "image/png");
            form.AddField("type", "input");
            form.AddField("overwrite", "true");
            using (UnityWebRequest request = UnityWebRequest.Post(baseUrl + "/upload/image", form))
            {
                await SendAsync(request, cancellationToken);
                EnsureSuccess(request, "/upload/image");
                var response = RequireObject(request.downloadHandler.text, "/upload/image");
                string name = RequireString(response, "name", "/upload/image");
                string subfolder = OptionalString(response, "subfolder");
                return string.IsNullOrEmpty(subfolder) ? name : subfolder.TrimEnd('/') + "/" + name;
            }
        }

        private async Task<string> QueuePromptAsync(
            string workflowJson,
            string inputNodeId,
            string inputFieldName,
            string uploadedName,
            CancellationToken cancellationToken)
        {
            Dictionary<string, object> workflow = RequireObject(workflowJson, "工作流");
            if (workflow.TryGetValue("prompt", out object nestedPrompt) && nestedPrompt is Dictionary<string, object> nested)
                workflow = nested;
            if (!workflow.TryGetValue(inputNodeId, out object nodeValue) || !(nodeValue is Dictionary<string, object> node))
                throw new InvalidDataException("工作流中不存在输入节点 ID: " + inputNodeId);
            if (!node.TryGetValue("inputs", out object inputsValue) || !(inputsValue is Dictionary<string, object> inputs))
                throw new InvalidDataException("输入节点没有 inputs 对象: " + inputNodeId);
            if (!inputs.ContainsKey(inputFieldName))
                throw new InvalidDataException($"输入节点 {inputNodeId} 没有字段 {inputFieldName}。");
            inputs[inputFieldName] = uploadedName;

            var body = new Dictionary<string, object>
            {
                ["prompt"] = workflow,
                ["client_id"] = Guid.NewGuid().ToString("N")
            };
            string responseText = await PostJsonAsync("/prompt", MiniJson.Serialize(body), cancellationToken);
            Dictionary<string, object> response = RequireObject(responseText, "/prompt");
            if (response.TryGetValue("node_errors", out object nodeErrors) &&
                nodeErrors is Dictionary<string, object> errors && errors.Count > 0)
                throw new InvalidOperationException("ComfyUI 节点校验失败: " + MiniJson.Serialize(errors));
            return RequireString(response, "prompt_id", "/prompt");
        }

        private async Task<ComfyOutputImage> WaitForOutputAsync(
            string promptId,
            string outputNodeId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed < timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string text = await GetTextAsync("/history/" + UnityWebRequest.EscapeURL(promptId), cancellationToken);
                    Dictionary<string, object> root = RequireObject(text, "/history");
                    if (root.TryGetValue(promptId, out object historyValue) && historyValue is Dictionary<string, object> history)
                    {
                        if (TryReadExecutionError(history, out string executionError))
                            throw new InvalidOperationException("ComfyUI 执行失败: " + executionError);

                        if (history.TryGetValue("outputs", out object outputsValue) &&
                            outputsValue is Dictionary<string, object> outputs &&
                            outputs.TryGetValue(outputNodeId, out object outputValue) &&
                            outputValue is Dictionary<string, object> outputNode &&
                            TryReadImage(outputNode, out ComfyOutputImage image))
                            return image;

                        if (IsCompleted(history))
                            throw new InvalidOperationException(
                                $"ComfyUI 已完成，但输出节点 {outputNodeId} 没有图片。状态: {TrimJson(history)}");
                    }
                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 用户中断时尽力通知 ComfyUI 停止当前生成，避免服务端继续空跑；失败不影响取消流程
                await TryInterruptAsync();
                throw;
            }
            throw new TimeoutException("等待 ComfyUI 完成超时: " + promptId);
        }

        private async Task TryInterruptAsync()
        {
            try
            {
                using (var interruptCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    await PostJsonAsync("/interrupt", "{}", interruptCancellation.Token);
            }
            catch
            {
                // 中断通知本身失败不应掩盖用户取消，静默忽略
            }
        }

        private static bool TryReadImage(Dictionary<string, object> outputNode, out ComfyOutputImage image)
        {
            image = null;
            if (!outputNode.TryGetValue("images", out object imagesValue) || !(imagesValue is List<object> images))
                return false;
            foreach (object value in images)
            {
                if (!(value is Dictionary<string, object> item) ||
                    !item.TryGetValue("filename", out object filenameValue) || !(filenameValue is string filename))
                    continue;
                image = new ComfyOutputImage
                {
                    filename = filename,
                    subfolder = OptionalString(item, "subfolder"),
                    type = OptionalString(item, "type")
                };
                return true;
            }
            return false;
        }

        private static bool TryReadExecutionError(Dictionary<string, object> history, out string error)
        {
            error = null;
            if (!history.TryGetValue("status", out object statusValue) || !(statusValue is Dictionary<string, object> status) ||
                !status.TryGetValue("messages", out object messagesValue) || !(messagesValue is List<object> messages))
                return false;

            foreach (object messageValue in messages)
            {
                if (!(messageValue is List<object> message) || message.Count == 0)
                    continue;
                if (string.Equals(message[0] as string, "execution_error", StringComparison.Ordinal))
                {
                    error = MiniJson.Serialize(message);
                    return true;
                }
            }
            return false;
        }

        private static bool IsCompleted(Dictionary<string, object> history)
        {
            if (!history.TryGetValue("status", out object statusValue) || !(statusValue is Dictionary<string, object> status))
                return false;
            return status.TryGetValue("completed", out object completed) && completed is bool value && value;
        }

        private async Task<string> GetTextAsync(string relativeUrl, CancellationToken cancellationToken)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(baseUrl + relativeUrl))
            {
                await SendAsync(request, cancellationToken);
                EnsureSuccess(request, relativeUrl);
                return request.downloadHandler.text;
            }
        }

        private async Task<string> PostJsonAsync(string relativeUrl, string json, CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var request = new UnityWebRequest(baseUrl + relativeUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                await SendAsync(request, cancellationToken);
                EnsureSuccess(request, relativeUrl);
                return request.downloadHandler.text;
            }
        }

        private async Task SendAsync(UnityWebRequest request, CancellationToken cancellationToken)
        {
            request.timeout = requestTimeoutSeconds;
            var completion = new TaskCompletionSource<bool>();
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            operation.completed += _ => completion.TrySetResult(true);
            using (cancellationToken.Register(() =>
                   {
                       request.Abort();
                       completion.TrySetCanceled();
                   }))
            {
                await completion.Task;
            }
        }

        private static void EnsureSuccess(UnityWebRequest request, string endpoint)
        {
            if (request.result == UnityWebRequest.Result.Success)
                return;
            string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            throw new InvalidOperationException(
                $"ComfyUI {endpoint} 请求失败 ({request.responseCode}): {request.error}\n{body}");
        }

        private static Dictionary<string, object> RequireObject(string json, string context)
        {
            object value = MiniJson.Deserialize(json);
            if (value is Dictionary<string, object> dictionary)
                return dictionary;
            throw new InvalidDataException(context + " JSON 格式无效。");
        }

        private static string RequireString(Dictionary<string, object> dictionary, string key, string context)
        {
            if (dictionary.TryGetValue(key, out object value) && value is string text && !string.IsNullOrEmpty(text))
                return text;
            throw new InvalidDataException(context + " 缺少字符串字段: " + key);
        }

        private static string OptionalString(Dictionary<string, object> dictionary, string key)
        {
            return dictionary.TryGetValue(key, out object value) ? value as string ?? string.Empty : string.Empty;
        }

        private static string TrimJson(object value)
        {
            string text = MiniJson.Serialize(value);
            return text.Length <= 1000 ? text : text.Substring(0, 1000) + "...";
        }
    }

    internal sealed class ComfyOutputImage
    {
        public string promptId;
        public string filename;
        public string subfolder;
        public string type;
    }

    internal sealed class ComfyDeviceMemory
    {
        public string deviceName = string.Empty;
        public long vramTotalBytes;
        public long vramFreeBytes;
        public bool hasVram;
    }
}
