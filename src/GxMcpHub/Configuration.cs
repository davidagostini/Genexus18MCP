using System.Security.Cryptography;
using System.Text.Json;

namespace GxMcpHub;

internal sealed class HubConfig
{
    public string ProfileName { get; set; } = string.Empty;
    public string WorkspaceRoot { get; set; } = string.Empty;
    public string KnowledgeBasePath { get; set; } = string.Empty;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int CallTimeoutSeconds { get; set; } = 900;
    public WritePolicyConfig WritePolicy { get; set; } = new();
    public List<BackendConfig> Backends { get; set; } = new();
}

internal sealed class WritePolicyConfig
{
    public string Mode { get; set; } = "read-only";
    public string? WriterBackend { get; set; }
    public bool HideBlockedTools { get; set; } = true;
}

internal sealed class BackendConfig
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Kind { get; set; } = "fixed";
    public bool Enabled { get; set; }
    public string ToolPrefix { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = "2025-11-25";
    public string? Endpoint { get; set; }
    public string? BearerTokenEnvVar { get; set; }
    public bool UseRemoteReadOnlyAnnotations { get; set; }
    public List<string> ReadOnlyTools { get; set; } = new();
    public StartupConfig? Startup { get; set; }
}

internal sealed class StartupConfig
{
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public int StartupTimeoutSeconds { get; set; } = 30;
    public Dictionary<string, string> Environment { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

// Fingerprint is the SHA-256 of the very bytes the configuration was parsed from, so the revision a Hub reports
// always names the content it has loaded, even when the file is rewritten at that same moment.
internal sealed record LoadedHubConfig(
    HubConfig Config,
    string ConfigPath,
    string ConfigDirectory,
    string Fingerprint);

internal static class HubConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static LoadedHubConfig Load(string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"Perfil não encontrado: {fullPath}");
        }

        HubConfig config;
        string fingerprint;
        try
        {
            var bytes = ReadProfileBytes(fullPath);
            fingerprint = Fingerprint(bytes);
            config = JsonSerializer.Deserialize<HubConfig>(DecodeProfileText(bytes), JsonOptions)
                     ?? throw new ConfigurationException("O JSON do perfil está vazio.");
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"JSON inválido em {fullPath}: {ex.Message}");
        }

        // "backends": null, "writePolicy": null and the like deserialize without complaint and would only fail
        // later, as a null reference somewhere else.
        if (config.WritePolicy is null || config.WritePolicy.Mode is null || config.Backends is null ||
            config.Backends.Any(backend => backend is null || backend.ReadOnlyTools is null ||
                                           backend.Startup is { Environment: null }))
        {
            throw new ConfigurationException(
                "O perfil tem um valor nulo onde ele é obrigatório: writePolicy, writePolicy.mode, backends, " +
                "readOnlyTools ou startup.environment.");
        }

        var configDir = Path.GetDirectoryName(fullPath)
                        ?? throw new ConfigurationException("Não foi possível resolver a pasta do perfil.");
        config.WorkspaceRoot = ResolvePath(config.WorkspaceRoot, configDir, configDir, config.KnowledgeBasePath);
        config.KnowledgeBasePath = ResolvePath(
            config.KnowledgeBasePath,
            configDir,
            config.WorkspaceRoot,
            config.KnowledgeBasePath);

        foreach (var backend in config.Backends)
        {
            backend.Endpoint = ExpandTokens(backend.Endpoint, configDir, config.WorkspaceRoot, config.KnowledgeBasePath);
            if (backend.Startup is null)
            {
                continue;
            }

            backend.Startup.Command = ResolvePath(
                backend.Startup.Command,
                configDir,
                config.WorkspaceRoot,
                config.KnowledgeBasePath);
            backend.Startup.WorkingDirectory = ResolvePath(
                backend.Startup.WorkingDirectory,
                configDir,
                config.WorkspaceRoot,
                config.KnowledgeBasePath);
            backend.Startup.Arguments = ExpandTokens(
                backend.Startup.Arguments,
                configDir,
                config.WorkspaceRoot,
                config.KnowledgeBasePath) ?? string.Empty;

            foreach (var key in backend.Startup.Environment.Keys.ToArray())
            {
                backend.Startup.Environment[key] = ExpandTokens(
                    backend.Startup.Environment[key],
                    configDir,
                    config.WorkspaceRoot,
                    config.KnowledgeBasePath) ?? string.Empty;
            }
        }

        Validate(config);
        return new LoadedHubConfig(config, fullPath, configDir, fingerprint);
    }

    // The profile is read without locking out whoever writes it: a promotion rewrites the file while every open Hub
    // keeps looking at it. What is read may then be half a file; the caller treats that as a profile that does not
    // load and looks again later.
    internal static byte[] ReadProfileBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // The revision of the profile as it is on disk now.
    internal static string ReadProfileFingerprint(string path) => Fingerprint(ReadProfileBytes(path));

    private static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string DecodeProfileText(byte[] bytes)
    {
        using var reader = new StreamReader(
            new MemoryStream(bytes),
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void Validate(HubConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ProfileName))
        {
            throw new ConfigurationException("profileName é obrigatório.");
        }
        if (string.IsNullOrWhiteSpace(config.WorkspaceRoot) || !Directory.Exists(config.WorkspaceRoot))
        {
            throw new ConfigurationException($"workspaceRoot não existe: {config.WorkspaceRoot}");
        }
        if (string.IsNullOrWhiteSpace(config.KnowledgeBasePath) || !Directory.Exists(config.KnowledgeBasePath))
        {
            throw new ConfigurationException($"knowledgeBasePath não existe: {config.KnowledgeBasePath}");
        }
        if (config.ConnectTimeoutSeconds is < 1 or > 120)
        {
            throw new ConfigurationException("connectTimeoutSeconds deve ficar entre 1 e 120.");
        }
        if (config.CallTimeoutSeconds is < 1 or > 3600)
        {
            throw new ConfigurationException("callTimeoutSeconds deve ficar entre 1 e 3600.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in config.Backends)
        {
            if (string.IsNullOrWhiteSpace(backend.Id) || !ids.Add(backend.Id))
            {
                throw new ConfigurationException($"Backend sem id ou id duplicado: '{backend.Id}'.");
            }
            if (string.IsNullOrWhiteSpace(backend.ToolPrefix) ||
                backend.ToolPrefix.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')) ||
                !prefixes.Add(backend.ToolPrefix))
            {
                throw new ConfigurationException($"toolPrefix inválido ou duplicado no backend '{backend.Id}'.");
            }
            if (string.IsNullOrWhiteSpace(backend.ProtocolVersion))
            {
                throw new ConfigurationException($"protocolVersion é obrigatório no backend '{backend.Id}'.");
            }
            if (backend.Startup is not null)
            {
                if (!File.Exists(backend.Startup.Command))
                {
                    throw new ConfigurationException(
                        $"Comando de startup do backend '{backend.Id}' não existe: {backend.Startup.Command}");
                }
                if (!Directory.Exists(backend.Startup.WorkingDirectory))
                {
                    throw new ConfigurationException(
                        $"Working directory do backend '{backend.Id}' não existe: {backend.Startup.WorkingDirectory}");
                }
            }
        }

        var mode = config.WritePolicy.Mode.Trim().ToLowerInvariant();
        if (mode is not ("read-only" or "single-writer" or "open-writer"))
        {
            throw new ConfigurationException(
                "writePolicy.mode deve ser read-only, single-writer ou open-writer.");
        }
        config.WritePolicy.Mode = mode;

        if (mode is "single-writer" or "open-writer")
        {
            if (string.IsNullOrWhiteSpace(config.WritePolicy.WriterBackend) ||
                !ids.Contains(config.WritePolicy.WriterBackend))
            {
                throw new ConfigurationException(
                    $"{mode} exige writerBackend igual ao id de um backend configurado.");
            }
            if (!config.Backends.Any(backend =>
                    backend.Enabled && backend.Id.Equals(
                        config.WritePolicy.WriterBackend,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConfigurationException(
                    "writerBackend deve apontar para um backend habilitado.");
            }
        }
        else
        {
            config.WritePolicy.WriterBackend = null;
        }
    }

    private static string ResolvePath(string? value, string configDir, string workspaceRoot, string kbPath)
    {
        var expanded = ExpandTokens(value, configDir, workspaceRoot, kbPath);
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return string.Empty;
        }
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(configDir, expanded));
    }

    private static string? ExpandTokens(string? value, string configDir, string workspaceRoot, string kbPath)
    {
        if (value is null)
        {
            return null;
        }

        return Environment.ExpandEnvironmentVariables(value)
            .Replace("${CONFIG_DIR}", configDir, StringComparison.OrdinalIgnoreCase)
            .Replace("${WORKSPACE_ROOT}", workspaceRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("${KB_PATH}", kbPath, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message)
    {
    }
}
