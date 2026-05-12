using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acd.Mcp.Batch
{
    // Durable history of completed batch runs.
    //
    // Storage:
    //   %LOCALAPPDATA%\Acd.Mcp\batch-runs\<yyyy-MM-dd_HH-mm-ss>_<run_id>.json
    //
    // The filename embeds the timestamp so newest-first enumeration is
    // a string-sort on the file name — no metadata index file needed.
    //
    // Pagination:
    //   ListRecent(limit, offset) returns at most `limit` entries, skipping
    //   the first `offset`. Default limit 20; max 100 — enforced so an
    //   unbounded response cannot flood the agent's context.
    //
    // Each list entry is a summary (RunSummary), NOT the full report; the
    // full report is fetched by id via Load(runId).
    public sealed class BatchRunHistory
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Enum-as-string so the file is human-readable.
            Converters = { new JsonStringEnumConverter() },
        };

        public const int DefaultLimit = 20;
        public const int MaxLimit = 100;

        public string Root { get; }

        public BatchRunHistory(string? rootOverride = null)
        {
            Root = rootOverride ?? DefaultRoot();
            Directory.CreateDirectory(Root);
        }

        public string Save(BatchRunReport report)
        {
            var stamp = report.StartedAt.ToString("yyyy-MM-dd_HH-mm-ss");
            var filename = $"{stamp}_{report.RunId}.json";
            var path = System.IO.Path.Combine(Root, filename);

            // Reports contain System.Exception which serialises as a big mess
            // — we project to a serialisable shape first.
            var serialisable = ReportEnvelope.FromReport(report);
            File.WriteAllText(path, JsonSerializer.Serialize(serialisable, JsonOptions));
            return path;
        }

        public BatchRunReport? Load(string runId)
        {
            var match = FindFileByRunId(runId);
            if (match is null) return null;
            using var stream = File.OpenRead(match);
            var envelope = JsonSerializer.Deserialize<ReportEnvelope>(stream, JsonOptions);
            return envelope?.ToReport();
        }

        // newest-first enumeration; pagination applied at this layer so the
        // caller never accidentally loads the entire history into memory.
        public IReadOnlyList<RunSummary> ListRecent(int limit = DefaultLimit, int offset = 0)
        {
            if (limit < 1) limit = 1;
            if (limit > MaxLimit) limit = MaxLimit;
            if (offset < 0) offset = 0;

            var files = Directory.Exists(Root)
                ? Directory.GetFiles(Root, "*.json")
                : Array.Empty<string>();
            Array.Sort(files);
            Array.Reverse(files); // newest-first

            var page = files.Skip(offset).Take(limit);
            var summaries = new List<RunSummary>();
            foreach (var f in page)
            {
                try
                {
                    using var stream = File.OpenRead(f);
                    var envelope = JsonSerializer.Deserialize<ReportEnvelope>(stream, JsonOptions);
                    if (envelope is null) continue;
                    summaries.Add(envelope.ToSummary());
                }
                catch
                {
                    // A corrupt history file MUST NOT block listing — skip it.
                }
            }
            return summaries;
        }

        public RunSummary? LoadLastSummary()
        {
            var page = ListRecent(limit: 1, offset: 0);
            return page.Count > 0 ? page[0] : null;
        }

        public int Count()
        {
            if (!Directory.Exists(Root)) return 0;
            return Directory.GetFiles(Root, "*.json").Length;
        }

        private string? FindFileByRunId(string runId)
        {
            if (!Directory.Exists(Root)) return null;
            return Directory.GetFiles(Root, $"*_{runId}.json").FirstOrDefault();
        }

        private static string DefaultRoot() => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Acd.Mcp",
            "batch-runs");
    }

    // Public, JSON-serialisable summary of a single run.
    public sealed record RunSummary(
        string RunId,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        BatchMode RequestedMode,
        int FileCount,
        int PassCount,
        int FailureCount,
        bool Cancelled,
        string? AbortedReason);

    // JSON envelope. Project the BatchRunReport into a serialisable shape:
    // exceptions become message + type-name strings, step outcomes get
    // discriminator + payload.
    internal sealed class ReportEnvelope
    {
        public string RunId { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset CompletedAt { get; set; }
        public BatchMode RequestedMode { get; set; }
        public string[] Files { get; set; } = Array.Empty<string>();
        public List<FileResultEnvelope> Results { get; set; } = new();
        public bool Cancelled { get; set; }
        public string? AbortedReason { get; set; }

        public static ReportEnvelope FromReport(BatchRunReport r) => new()
        {
            RunId = r.RunId,
            StartedAt = r.StartedAt,
            CompletedAt = r.CompletedAt,
            RequestedMode = r.RequestedMode,
            Files = r.Files.ToArray(),
            Results = r.Results.Select(FileResultEnvelope.From).ToList(),
            Cancelled = r.Cancelled,
            AbortedReason = r.AbortedReason,
        };

        public BatchRunReport ToReport() => new(
            RunId: RunId,
            StartedAt: StartedAt,
            CompletedAt: CompletedAt,
            RequestedMode: RequestedMode,
            Files: Files,
            Results: Results.Select(f => f.ToResult()).ToList(),
            Cancelled: Cancelled,
            AbortedReason: AbortedReason);

        public RunSummary ToSummary()
        {
            int pass = 0, fail = 0;
            foreach (var r in Results)
            {
                if (r.Status == FileOutcomeStatus.Pass) pass++;
                else fail++;
            }
            return new RunSummary(
                RunId: RunId,
                StartedAt: StartedAt,
                CompletedAt: CompletedAt,
                RequestedMode: RequestedMode,
                FileCount: Files.Length,
                PassCount: pass,
                FailureCount: fail,
                Cancelled: Cancelled,
                AbortedReason: AbortedReason);
        }
    }

    internal sealed class FileResultEnvelope
    {
        public string Path { get; set; } = "";
        public BatchPhase Phase { get; set; }
        public FileOutcomeStatus Status { get; set; }
        public List<StepOutcomeEnvelope> Steps { get; set; } = new();
        public bool Committed { get; set; }
        public bool Cancelled { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
        public long ElapsedMs { get; set; }

        public static FileResultEnvelope From(BatchFileResult r) => new()
        {
            Path = r.Path,
            Phase = r.Phase,
            Status = r.Status,
            Steps = r.Steps.Select(StepOutcomeEnvelope.From).ToList(),
            Committed = r.Committed,
            Cancelled = r.Cancelled,
            ErrorType = r.Error?.GetType().FullName,
            ErrorMessage = r.Error?.Message,
            ElapsedMs = r.ElapsedMs,
        };

        public BatchFileResult ToResult() => new(
            Path: Path,
            Phase: Phase,
            Status: Status,
            Steps: Steps.Select(s => s.ToOutcome()).ToList(),
            Committed: Committed,
            Cancelled: Cancelled,
            Error: ErrorMessage is null ? null : new BatchPersistedError(ErrorType, ErrorMessage),
            ElapsedMs: ElapsedMs);
    }

    internal sealed class StepOutcomeEnvelope
    {
        public string Kind { get; set; } = ""; // Pass | Skipped | Failure
        public string Name { get; set; } = "";
        public List<RequirementResultEnvelope> Requirements { get; set; } = new();
        public string? Summary { get; set; }
        public string? FailingRequirement { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }

        public static StepOutcomeEnvelope From(StepOutcome s) => s switch
        {
            StepOutcome.Pass p => new()
            {
                Kind = "Pass",
                Name = p.Name,
                Requirements = p.Requirements.Select(RequirementResultEnvelope.From).ToList(),
                Summary = p.Summary,
            },
            StepOutcome.Skipped sk => new()
            {
                Kind = "Skipped",
                Name = sk.Name,
                Requirements = sk.Requirements.Select(RequirementResultEnvelope.From).ToList(),
                FailingRequirement = sk.FailingRequirement,
            },
            StepOutcome.Failure f => new()
            {
                Kind = "Failure",
                Name = f.Name,
                Requirements = f.Requirements.Select(RequirementResultEnvelope.From).ToList(),
                ErrorType = f.Error.GetType().FullName,
                ErrorMessage = f.Error.Message,
            },
            _ => throw new InvalidOperationException("Unknown StepOutcome subtype: " + s.GetType().FullName),
        };

        public StepOutcome ToOutcome() => Kind switch
        {
            "Pass" => new StepOutcome.Pass(Name,
                Requirements.Select(r => r.ToResult()).ToList(),
                Summary ?? ""),
            "Skipped" => new StepOutcome.Skipped(Name,
                Requirements.Select(r => r.ToResult()).ToList(),
                FailingRequirement ?? ""),
            "Failure" => new StepOutcome.Failure(Name,
                Requirements.Select(r => r.ToResult()).ToList(),
                new BatchPersistedError(ErrorType, ErrorMessage ?? "")),
            _ => throw new InvalidOperationException("Unknown StepOutcomeEnvelope.Kind: " + Kind),
        };
    }

    internal sealed class RequirementResultEnvelope
    {
        public string Name { get; set; } = "";
        public bool Passed { get; set; }
        public string? ErrorMessage { get; set; }

        public static RequirementResultEnvelope From(RequirementResult r) => new()
        {
            Name = r.Name,
            Passed = r.Passed,
            ErrorMessage = r.Error?.Message,
        };

        public RequirementResult ToResult() => new(
            Name: Name,
            Passed: Passed,
            Error: ErrorMessage is null ? null : new BatchPersistedError(null, ErrorMessage));
    }

    // Lightweight wrapper used when reading a persisted error back. We don't
    // try to round-trip the original exception type (it may not exist in
    // this assembly); the type-name and message are enough for display.
    public sealed class BatchPersistedError : Exception
    {
        public string? PersistedTypeName { get; }

        public BatchPersistedError(string? persistedTypeName, string message) : base(message)
        {
            PersistedTypeName = persistedTypeName;
        }
    }
}
