using System.Text;
using PixelPin.Models;

namespace PixelPin.Services;

public sealed class ApplicationLogService
{
    private const long MaximumBytes = 768 * 1024;
    private readonly object _gate = new();

    public string FilePath => AppPaths.DiagnosticLogFile;

    public void Info(string area, string message) => Write("INFO", area, message);

    public void Error(string area, string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", area, detail);
    }

    public string ReadRecent(int maximumCharacters = 24000)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return "No diagnostics have been recorded yet.";
            }

            var content = File.ReadAllText(FilePath);
            return content.Length <= maximumCharacters
                ? content
                : content[^maximumCharacters..];
        }
        catch (Exception exception)
        {
            return $"Diagnostic log could not be read: {exception.Message}";
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch
            {
                // Diagnostics must never block capture or shutdown.
            }
        }
    }

    private void Write(string level, string area, string message)
    {
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureDirectories();
                RotateIfNeeded();
                var sanitizedArea = area.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
                var entry = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(sanitizedArea).Append(": ")
                    .AppendLine(message)
                    .ToString();
                File.AppendAllText(FilePath, entry);
            }
            catch
            {
                // A full disk or invalid local path must not destabilize the application.
            }
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaximumBytes)
        {
            return;
        }

        var archive = FilePath + ".previous";
        File.Move(FilePath, archive, overwrite: true);
    }
}
