using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Serilog;
using Serilog.Context;
using SoundButtons.Helper;
using SoundButtons.Models;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SoundButtons;

public class SoundButtons
{
    private static ILogger Logger => Helper.Log.Logger;

    [FunctionName("sound-buttons")]
    public static async Task<IActionResult> HttpStart(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
        [DurableClient] IDurableOrchestrationClient starter)
    {
        // 驗證ContentType為multipart/form-data
        string contentType = req.ContentType;
        Logger.Information("Content-Type: {contentType}", contentType);
        if (!contentType.Contains("multipart/form-data;"))
            return new BadRequestResult();

        Source source = GetSourceInfo(req);
        string? clip = await ProcessYoutubeClip(req, source);

        string tempPath = "";
        try
        {
            tempPath = await ProcessAudioFileAsync(req, source);
        }
        catch (Exception e)
        {
            Logger.Error("ProcessAudioFileAsync: {exception}, {message}, {stacktrace}", e, e.Message, e.StackTrace);
            return new BadRequestResult();
        }

        if (!float.TryParse(req.Form.GetFirstValue("volume"), out float volume)) { volume = 1; }

        // 啟動長輪詢
        IActionResult orchestratorResult = await StartOrchestrator(req: req,
                                                                   starter: starter,
                                                                   filename: GetFileName(req),
                                                                   directory: req.Form.GetFirstValue("directory") ?? "test",
                                                                   source: source,
                                                                   clip: clip,
                                                                   // toast ID用於回傳，讓前端能取消顯示toast
                                                                   toastId: req.Form.GetFirstValue("toastId") ?? "-1",
                                                                   tempPath: tempPath,
                                                                   ip: req.Headers.FirstOrDefault(x => x.Key == "X-Forwarded-For").Value.FirstOrDefault() ?? "",
                                                                   nameZH: req.Form.GetFirstValue("nameZH") ?? "",
                                                                   nameJP: req.Form.GetFirstValue("nameJP") ?? "",
                                                                   volume: volume,
                                                                   group: req.Form.GetFirstValue("group") ?? "未分類");
        return orchestratorResult;
    }

    private static async Task<IActionResult> StartOrchestrator(HttpRequest req, IDurableOrchestrationClient starter, string filename, string directory, Source source, string? clip, string toastId, string tempPath, string ip, string nameZH, string nameJP, float volume, string group)
    {
        string instanceId = Guid.NewGuid().ToString();
        await starter.StartNewAsync(
            orchestratorFunctionName: "main-sound-buttons",
            instanceId: instanceId,
            input: new Request()
            {
                Directory = directory,
                Filename = filename,
                Ip = ip,
                Source = source,
                Group = group,
                NameZH = nameZH,
                NameJP = nameJP,
                Volume = volume,
                TempPath = tempPath,
                ToastId = toastId,
                Clip = clip,
                InstanceId = instanceId
            });

        Logger.Information("Started orchestration with ID {instanceId}.", instanceId);

        return starter.CreateCheckStatusResponse(req, instanceId, true);
    }

    private static string GetFileName(HttpRequest req)
    {
        string? name = req.Form.GetFirstValue("nameZH"); // 用於回傳
        string filename = name ?? "";
        filename = Regex.Replace(filename, @"[^0-9a-zA-Z\p{L}]+", ""); // 比對通過英數、中日文字等(多位元組字元)
        if (filename.Length == 0)
            filename = Guid.NewGuid().ToString("n");
        Logger.Information("FileName: {filename}", filename);
        return filename;
    }

    private static Source GetSourceInfo(HttpRequest req)
    {
        var source = new Source
        {
            VideoId = req.Form.GetFirstValue("videoId") ?? "",
            Start = 0,
            End = 0
        };
        if (double.TryParse(req.Form.GetFirstValue("start"), out double start)
            && double.TryParse(req.Form.GetFirstValue("end"), out double end))
        {
            source.Start = start;
            source.End = end;
        }

        if (!string.IsNullOrEmpty(source.VideoId) && source.VideoId.StartsWith("http"))
        {
            // Regex for strip youtube video id from url c# and returl default thumbnail
            // https://gist.github.com/Flatlineato/f4cc3f3937272646d4b0
            source.VideoId = Regex.Match(
                source.VideoId,
                "https?:\\/\\/(?:[\\w-]+\\.)?(?:youtu\\.be\\/|youtube(?:-nocookie)?\\.com\\S*[^\\w\\s-])([\\w-]{11})(?=[^\\w-]|$)(?![?=&+%\\w.-]*(?:['\"][^<>]*>|<\\/a>))[?=&+%\\w.-]*",
                RegexOptions.IgnoreCase).Groups[1].Value;

            if (string.IsNullOrEmpty(source.VideoId))
            {
                // Discard unknown source
                source.VideoId = "";
                source.Start = 0;
                source.End = 0;
                Logger.Error("Discard unknown source: {source}", source.VideoId);
            }
            Logger.Information("Get info from form: {videoId}, {start}, {end}", source.VideoId, source.Start, source.End);
        }

        return source;
    }

    private static async Task<string?> ProcessYoutubeClip(HttpRequest req, Source source)
    {
        string? clip = req.Form.GetFirstValue("clip");
        Regex clipReg = new(@"https?:\/\/(?:[\w-]+\.)?(?:youtu\.be\/|youtube(?:-nocookie)?\.com\/)clip\/[?=&+%\w.-]*");
        if (!string.IsNullOrEmpty(clip) && clipReg.IsMatch(clip))
        {
            using HttpClient client = new();
            var response = await client.GetAsync(clip);
            string body = await response.Content.ReadAsStringAsync();

            // "clipConfig":{"postId":"UgkxVQpxshiN76QUwblPu-ggj6fl594-ORiU","startTimeMs":"1891037","endTimeMs":"1906037"}
            Regex reg1 = new(@"clipConfig"":{""postId"":""(?:[\w-]+)"",""startTimeMs"":""(\d+)"",""endTimeMs"":""(\d+)""}");
            Match match1 = reg1.Match(body);
            if (double.TryParse(match1.Groups[1].Value, out double _start)
                && double.TryParse(match1.Groups[2].Value, out double _end))
            {
                source.Start = _start / 1000;
                source.End = _end / 1000;
            }

            // {"videoId":"Gs7QYATahy4"}
            Regex reg2 = new(@"{""videoId"":""([\w-]+)""");
            Match match2 = reg2.Match(body);
            source.VideoId = match2.Groups[1].Value;
            Logger.Information("Get info from clip: {videoId}, {start}, {end}", source.VideoId, source.Start, source.End);
        }

        return clip;
    }

    private static async Task<string> ProcessAudioFileAsync(HttpRequest req, Source source)
    {
        IFormFileCollection files = req.Form.Files;
        Logger.Information("Files Count: {fileCount}", files.Count);
        if (files.Count > 0)
        {
            return await ProcessAudioFromFileUpload(files);
        }
        // source檢核
        else if (string.IsNullOrEmpty(source.VideoId)
                 || source.End - source.Start <= 0
                 || source.End - source.Start > 180)
        {
            Logger.Error("video time invalid: {start}, {end}", source.Start, source.End);
            throw new Exception($"video time invalid: {source.Start}, {source.End}");
        }
        return "";
    }

    private static async Task<string> ProcessAudioFromFileUpload(IFormFileCollection files)
    {
        var tempDir = Helper.FileHelper.PrepareTempDir();
        string tempPath = Path.Combine(tempDir, DateTime.Now.Ticks.ToString() + ".tmp");

        Logger.Information("Get file from form post.");

        // 有音檔，直接寫到暫存路徑使用
        IFormFile file = files[0];
        // Get file info
        var _fileExtension = Path.GetExtension(file.FileName) ?? "";
        tempPath = Path.ChangeExtension(tempPath, _fileExtension);
        Logger.Information("Get extension: {fileExtension}", _fileExtension);
        using (var fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write))
        {
            file.CopyTo(fs);
            Logger.Information("Write file from upload.");
        }

        return _fileExtension == ".webm"
            ? tempPath
            : await ProcessAudioHelper.TranscodeAudioAsync(tempPath);
    }

    private static void CleanUp(string tempPath)
    {
        var path = Path.GetDirectoryName(tempPath);
        if (path == null) { return; }
        Directory.Delete(path, true);
    }

    [FunctionName("main-sound-buttons")]
    public static async Task<bool> RunOrchestrator(
        [OrchestrationTrigger] IDurableOrchestrationContext context)
    {
        Request request = context.GetInput<Request>();
        using var _ = LogContext.PushProperty("InstanceId", request.InstanceId);

        // 若表單有音檔，前一步驟會把檔案放入這個路徑
        if (string.IsNullOrEmpty(request.TempPath))
        {
            request.TempPath = await context.CallActivityAsync<string>("ProcessAudioAsync", request);
        }

        request = await context.CallActivityAsync<Request>("UploadAudioToStorageAsync", request);
        request = await context.CallActivityAsync<Request>("SpeechToTextAsync", request);

        await context.CallActivityAsync("ProcessJsonFile", request);

        CleanUp(request.TempPath);
        Logger.Information("Finish. {filename}", request.NameZH);

        return true;
    }
}
