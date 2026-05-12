using System.Text;

namespace Acd.Mcp.Ui
{
    // Pure view-model around a LogEntry. Formats the one-line header (timestamp,
    // source, status, elapsed, code preview) and the multi-line detail body
    // (return value, stdout, stderr, diagnostics). The View binds; no logic here
    // beyond text formatting.
    public sealed class LogEntryViewModel
    {
        public LogEntry Entry { get; }

        public LogEntryViewModel(LogEntry entry)
        {
            Entry = entry;
        }

        public string Header
        {
            get
            {
                var time = Entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
                var source = Entry.Source switch
                {
                    ExecutionSource.Mcp => "MCP ",
                    ExecutionSource.Repl => "REPL",
                    _ => "?   ",
                };
                var status = Entry.Result.Success ? "OK  " : "FAIL";
                var preview = Entry.Code.Replace("\r", "").Replace("\n", " ").Trim();
                if (preview.Length > 60) preview = preview[..57] + "...";
                return $"{time}  {source}  {status}  {Entry.Result.ElapsedMs,5} ms   {preview}";
            }
        }

        public string Code => Entry.Code;

        public string Detail
        {
            get
            {
                var sb = new StringBuilder();
                var r = Entry.Result;

                if (r.Success && !string.IsNullOrEmpty(r.ReturnValueRepr))
                    sb.AppendLine("=> " + r.ReturnValueRepr);

                if (!string.IsNullOrEmpty(r.Stdout))
                {
                    sb.AppendLine("[stdout]");
                    sb.Append(r.Stdout);
                    if (!r.Stdout.EndsWith("\n")) sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(r.Stderr))
                {
                    sb.AppendLine("[stderr]");
                    sb.Append(r.Stderr);
                    if (!r.Stderr.EndsWith("\n")) sb.AppendLine();
                }

                foreach (var d in r.Diagnostics)
                    sb.AppendLine($"{d.Severity} ({d.Line ?? 0},{d.Column ?? 0}): {d.Message}");

                if (sb.Length == 0)
                    sb.Append("(no output)");

                return sb.ToString().TrimEnd();
            }
        }

        public bool IsSuccess => Entry.Result.Success;
    }
}
