using System.Text;

namespace TradingBot.Worker;

internal static class FileLogging
{
    public static IDisposable Configure(string directory)
    {
        Directory.CreateDirectory(directory);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outputFile = new DailyFileTextWriter(directory, "worker", originalOut.Encoding);
        var errorFile = new DailyFileTextWriter(directory, "errors", originalError.Encoding);

        Console.SetOut(new TeeTextWriter(originalOut, outputFile));
        Console.SetError(new TeeTextWriter(originalError, errorFile));

        return new ConsoleLoggingScope(originalOut, originalError, outputFile, errorFile);
    }

    private sealed class ConsoleLoggingScope(
        TextWriter originalOut,
        TextWriter originalError,
        DailyFileTextWriter outputFile,
        DailyFileTextWriter errorFile) : IDisposable
    {
        public void Dispose()
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            outputFile.Dispose();
            errorFile.Dispose();
        }
    }
}

internal sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
{
    private readonly object _gate = new();

    public override Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        lock (_gate)
        {
            first.Write(value);
            second.Write(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            first.Write(value);
            second.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            first.WriteLine(value);
            second.WriteLine(value);
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            first.Flush();
            second.Flush();
        }
    }
}

internal sealed class DailyFileTextWriter(string directory, string prefix, Encoding encoding) : TextWriter
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    public override Encoding Encoding => encoding;

    public override void Write(char value)
    {
        lock (_gate)
        {
            EnsureWriter();
            _writer!.Write(value);
            _writer.Flush();
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            EnsureWriter();
            _writer!.Write(value);
            _writer.Flush();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            EnsureWriter();
            _writer!.WriteLine(value);
            _writer.Flush();
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            _writer?.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _writer?.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_writer is not null && _writerDate == today)
        {
            return;
        }

        _writer?.Dispose();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{prefix}-{today:yyyy-MM-dd}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), encoding)
        {
            AutoFlush = true
        };
        _writerDate = today;
    }
}
