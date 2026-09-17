namespace GxMcp.Worker.Utils
{
    /// <summary>
    /// Small port used by services that consume user-supplied file paths.
    /// The concrete policy remains an infrastructure detail of the worker.
    /// </summary>
    internal interface IUserFilePathPolicy
    {
        bool TryResolveReadPath(string rawPath, out string fullPath, out string error);

        bool TryResolveWritePath(string rawPath, out string fullPath, out string error);
    }
}
