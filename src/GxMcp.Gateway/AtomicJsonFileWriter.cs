using System;
using System.IO;
using System.Text;

namespace GxMcp.Gateway
{
    /// <summary>Publishes a complete JSON document without exposing a partially written target.</summary>
    internal static class AtomicJsonFileWriter
    {
        private static readonly object ReplaceLock = new object();

        internal static void Write(string path, string content)
        {
            Write(path, content, static (tempPath, text) =>
            {
                using var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
                writer.Write(text);
                writer.Flush();
                stream.Flush(true);
            });
        }

        // The writer delegate is internal so tests can exercise interruption before
        // replacement without changing the production persistence surface.
        internal static void Write(string path, string content, Action<string, string> stage)
        {
            string fullPath = Path.GetFullPath(path);
            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                stage(tempPath, content);
                lock (ReplaceLock)
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            if (File.Exists(fullPath))
                                File.Replace(tempPath, fullPath, null);
                            else
                                File.Move(tempPath, fullPath);
                            break;
                        }
                        catch (IOException) when (attempt < 5)
                        {
                            System.Threading.Thread.Sleep(10 * (attempt + 1));
                        }
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* Never obscure the original staging/replacement error. */ }
            }
        }
    }
}
