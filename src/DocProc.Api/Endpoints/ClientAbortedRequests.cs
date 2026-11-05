namespace DocProc.Api.Endpoints;

/// <summary>
/// A request the client gave up on (a browser tab closed, a list query superseded by the next keystroke) cancels
/// <see cref="HttpContext.RequestAborted"/>. SqlClient reports that as a <c>SqlException</c> ("A severe error occurred on
/// the current command … Operation cancelled by user"), not as an <see cref="OperationCanceledException"/>, so the
/// exception handler logged an unhandled error and answered 500 to nobody. Found in the compose stack by the P1 demo:
/// the portal's inbox cancels its earlier list query whenever a filter changes. This middleware, placed inside the
/// exception handler, turns any exception of an aborted request into an <see cref="OperationCanceledException"/>, which
/// the exception handler treats as a closed request (status 499, logged at debug level).
/// </summary>
internal static class ClientAbortedRequests
{
    public static IApplicationBuilder UseClientAbortedRequests(this IApplicationBuilder app) => app.Use(InvokeAsync);

    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            throw new OperationCanceledException("The client closed the request.", exception, context.RequestAborted);
        }
    }
}
