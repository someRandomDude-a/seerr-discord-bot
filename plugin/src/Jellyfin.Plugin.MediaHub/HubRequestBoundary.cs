using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Plugin-only admission/body/time budgets; never changes Jellyfin's global MVC or host options.</summary>
public sealed class HubRequestBoundary : IAsyncResourceFilter, IAsyncActionFilter
{
    private readonly SemaphoreSlim requests = new(8);
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!requests.Wait(0))
        { context.Result = new ObjectResult(new { Error = "Media Hub is busy. Try again later; no operation was started." }) { StatusCode = 429 }; return; }
        var http = context.HttpContext; var originalCancellation = http.RequestAborted; var originalBody = http.Request.Body;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(originalCancellation); budget.CancelAfter(TimeSpan.FromSeconds(30));
        using var body = new MemoryStream(); http.RequestAborted = budget.Token;
        try
        {
            if (http.Request.HasJsonContentType())
            {
                if (http.Request.ContentLength > 65536) throw new HubError("JSON requests are limited to 64 KiB.");
                var buffer = new byte[4096]; int read;
                while ((read = await originalBody.ReadAsync(buffer, budget.Token)) != 0)
                { if (body.Length + read > 65536) throw new HubError("JSON requests are limited to 64 KiB."); body.Write(buffer, 0, read); }
                if (body.Length > 0) SafeJson.Validate(body.GetBuffer().AsSpan(0, (int)body.Length), 65536);
                body.Position = 0; http.Request.Body = body;
            }
            var executed = await next();
            if (executed.Exception is not null && !executed.ExceptionHandled && !http.Response.HasStarted)
            { executed.ExceptionHandled = true; executed.Result = new ObjectResult(new { Error = "Media Hub could not verify the operation. Check its receipt before retrying any write." }) { StatusCode = 503 }; }
        }
        catch (HubError e) when (!http.Response.HasStarted) { context.Result = new BadRequestObjectResult(new { Error = e.Message }); }
        catch (OperationCanceledException) when (!http.Response.HasStarted)
        { context.Result = new ObjectResult(new { Error = "Verification timed out or the client disconnected. Check the result before retrying any write." }) { StatusCode = 503 }; }
        catch (Exception) when (!http.Response.HasStarted)
        { context.Result = new ObjectResult(new { Error = "Media Hub could not verify the operation. No automatic retry was performed." }) { StatusCode = 503 }; }
        finally { http.RequestAborted = originalCancellation; http.Request.Body = originalBody; requests.Release(); }
    }
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.ModelState.IsValid)
        { context.Result = new BadRequestObjectResult(new { Error = "Invalid Media Hub request. Check the required fields and limits." }); return; }
        await next();
    }
}
