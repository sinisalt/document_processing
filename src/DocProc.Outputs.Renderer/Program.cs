using System.Text;
using DocProc.Outputs.Renderer;

// The renderer child process (docs/11 §13.1): one request per line on stdin, one response per line on stdout, until
// stdin closes. It opens no files, sockets or other processes; the host kills it when a render passes the time limit,
// and the GC heap limit the host sets (DOTNET_GCHeapHardLimit) bounds its memory.
//
// The requests are served on a thread with a large stack (RenderEngine.StackBytes): the library's parser, optimiser
// and evaluator recurse over the expression's syntax tree, and a stack overflow cannot be caught, so the stack must be
// deep enough for the library's own caps (the evaluation depth, the host's nesting check) to fire first.
var exitCode = 0;
var worker = new Thread(() => exitCode = Serve(), RenderEngine.StackBytes) { Name = "render", IsBackground = false };
worker.Start();
worker.Join();
return exitCode;

static int Serve()
{
    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using var input = new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
    using var output = new StreamWriter(Console.OpenStandardOutput(), utf8, bufferSize: 1 << 16) { AutoFlush = false };

    // Warm up the library (its static tables), so the first real render does not pay for it within its time limit.
    RenderEngine.Handle("""{"id":0,"expression":"$string($sum([1,2]))","input":{},"today":"2000-01-01"}""");
    output.WriteLine("""{"ready":true}""");
    output.Flush();

    while (input.ReadLine() is { } line)
    {
        string response;
        try
        {
            response = RenderEngine.Handle(line, () =>
            {
                output.WriteLine(RenderEngine.StartedLine);
                output.Flush();
            });
        }
        catch (OutOfMemoryException)
        {
            output.WriteLine(RenderEngine.MemoryLimit(null));
            output.Flush();
            return 3;
        }
        catch (InsufficientExecutionStackException)
        {
            response = RenderEngine.Failure("depthLimit", "STACK", "The expression nests too deeply.");
        }
        catch (Exception e)
        {
            // Anything else the library throws for this expression (an internal error of the engine or the schema
            // check) fails this render for good; the process keeps serving.
            response = RenderEngine.Failure("runtime", "INTERNAL", $"The renderer could not evaluate the expression ({e.GetType().Name}): {e.Message}");
        }

        output.WriteLine(response);
        output.Flush();
    }

    return 0;
}
