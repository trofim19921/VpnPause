using System.Text;

namespace VpnPause.Core;

public sealed class RotatingLog
{
    readonly string dir;
    readonly string baseName;
    readonly long maxBytes;
    readonly int keep;
    readonly object gate = new();

    public RotatingLog(string dir, string baseName, long maxBytes = 1024 * 1024, int keep = 5)
    {
        this.dir = dir;
        this.baseName = baseName;
        this.maxBytes = maxBytes;
        this.keep = keep;
    }

    public string CurrentFile => Path.Combine(dir, baseName + ".log");

    public event Action<string>? Written;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var file = CurrentFile;
                if (File.Exists(file) && new FileInfo(file).Length > maxBytes) Rotate();
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Written?.Invoke(line);
    }

    public IReadOnlyList<string> Tail(int lines)
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(CurrentFile)) return Array.Empty<string>();
                using var fs = new FileStream(CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var q = new Queue<string>();
                while (sr.ReadLine() is { } l)
                {
                    q.Enqueue(l);
                    if (q.Count > lines) q.Dequeue();
                }
                return q.ToList();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }
    }

    void Rotate()
    {
        var oldest = Path.Combine(dir, $"{baseName}.{keep - 1}.log");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = keep - 2; i >= 1; i--)
        {
            var from = Path.Combine(dir, $"{baseName}.{i}.log");
            var to = Path.Combine(dir, $"{baseName}.{i + 1}.log");
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }
        File.Move(CurrentFile, Path.Combine(dir, $"{baseName}.1.log"), overwrite: true);
    }
}
