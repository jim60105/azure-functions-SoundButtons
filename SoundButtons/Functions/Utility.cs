using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Serilog;

namespace SoundButtons.Functions;

public class Utility
{
    private static ILogger Logger => Helper.Log.Logger;

    [FunctionName(nameof(Wake))]
    public IActionResult Wake([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "wake")] HttpRequest req)
    {
        Wake();
        return new OkResult();
    }

    private static void Wake()
    {
#if DEBUG
#pragma warning disable IDE0022 // �ϥΤ�k���B�⦡�D��
        Logger.Verbose("Wake executed at: {time}", System.DateTime.Now);
#pragma warning restore IDE0022 // �ϥΤ�k���B�⦡�D��
#endif
    }
}
