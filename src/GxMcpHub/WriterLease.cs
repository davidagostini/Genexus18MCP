using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GxMcpHub;

internal sealed class WriterLease : IDisposable
{
    private readonly FileStream _stream;

    private WriterLease(FileStream stream)
    {
        _stream = stream;
    }

    public static WriterLease? AcquireIfNeeded(HubConfig config, string? lockRoot = null)
    {
        if (!config.WritePolicy.Mode.Equals("single-writer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalizedKb = Path.GetFullPath(config.KnowledgeBasePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKb)));
        lockRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GxMcpHub",
            "writer-locks");
        Directory.CreateDirectory(lockRoot);
        var path = Path.Combine(lockRoot, key + ".lock");

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new ConfigurationException(
                $"Já existe outra instância escritora do GxMcpHub para esta KB. " +
                $"Feche-a antes de iniciar o perfil '{config.ProfileName}'. Detalhe: {ex.Message}");
        }

        try
        {
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, new
            {
                processId = Environment.ProcessId,
                startedAtUtc = DateTimeOffset.UtcNow,
                profile = config.ProfileName,
                writerBackend = config.WritePolicy.WriterBackend
            });
            stream.Flush(flushToDisk: true);
            return new WriterLease(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();
}
