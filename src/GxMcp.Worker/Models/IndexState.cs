using System;

namespace GxMcp.Worker.Models
{
    public class IndexState
    {
        public string Status { get; set; }       // "Cold" | "Reindexing" | "LiteReady" | "Enriching" | "Ready"
        public DateTime? LastIndexedAt { get; set; }
        // Availability (Status) and freshness are deliberately separate. A disk
        // snapshot can be usable for reads while it still needs a delta refresh.
        public string Freshness { get; set; } // "unknown" | "stale" | "refreshing" | "current"
        public DateTime? LastSuccessfulScanAt { get; set; }
        public int TotalObjects { get; set; }
        public double? Progress { get; set; }    // 0..1, only when Reindexing
        public int? EtaMs { get; set; }          // only when Reindexing
        public DateTime? LitePassCompletedUtc { get; set; }
        public DateTime? EnrichmentStartedUtc { get; set; }
    }
}
