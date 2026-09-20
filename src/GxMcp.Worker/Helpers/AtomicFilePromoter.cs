using System;
using System.IO;

namespace GxMcp.Worker.Helpers
{
    internal enum AtomicFilePromotionResult
    {
        Created,
        Replaced,
        DestinationExists
    }

    /// <summary>
    /// Promotes a fully staged file without a delete-then-move window.
    /// </summary>
    internal static class AtomicFilePromoter
    {
        internal static AtomicFilePromotionResult Promote(string stagedPath, string destinationPath, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(stagedPath)) throw new ArgumentException("Staged path is required.", nameof(stagedPath));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));

            string staged = Path.GetFullPath(stagedPath);
            string destination = Path.GetFullPath(destinationPath);
            if (!File.Exists(staged)) throw new FileNotFoundException("Staged file was not found.", staged);

            if (!overwrite && File.Exists(destination))
                return AtomicFilePromotionResult.DestinationExists;

            if (File.Exists(destination))
            {
                ReplaceOrMoveIfDestinationDisappeared(staged, destination);
                return AtomicFilePromotionResult.Replaced;
            }

            try
            {
                File.Move(staged, destination);
                return AtomicFilePromotionResult.Created;
            }
            catch (IOException) when (File.Exists(destination) && File.Exists(staged))
            {
                if (!overwrite) return AtomicFilePromotionResult.DestinationExists;

                // The destination appeared after the existence check. Replace it
                // without opening a delete/recreate window.
                ReplaceOrMoveIfDestinationDisappeared(staged, destination);
                return AtomicFilePromotionResult.Replaced;
            }
        }

        private static void ReplaceOrMoveIfDestinationDisappeared(string staged, string destination)
        {
            try
            {
                File.Replace(staged, destination, null);
            }
            catch (IOException) when (!File.Exists(destination) && File.Exists(staged))
            {
                // A concurrent delete can win between the existence check and
                // File.Replace. The staged file is still intact, so create the
                // destination atomically rather than reporting a false failure.
                File.Move(staged, destination);
            }
        }
    }
}
