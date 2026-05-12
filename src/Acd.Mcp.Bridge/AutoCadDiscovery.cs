using System.Diagnostics;

namespace Acd.Mcp.Bridge
{
    public static class AutoCadDiscovery
    {
        // Finds AutoCAD instances by process name and returns their PIDs.
        // Multiple AutoCAD versions/instances are all called "acad".
        public static int[] FindAutoCadPids()
        {
            return Process.GetProcessesByName("acad")
                .Select(p => p.Id)
                .OrderBy(id => id)
                .ToArray();
        }

        public static int ResolvePid(int? explicitPid)
        {
            if (explicitPid is int pid)
            {
                try
                {
                    _ = Process.GetProcessById(pid);
                    return pid;
                }
                catch (ArgumentException)
                {
                    throw new InvalidOperationException(
                        $"No process with PID {pid}.");
                }
            }

            var found = FindAutoCadPids();
            return found.Length switch
            {
                0 => throw new InvalidOperationException(
                    "No AutoCAD instance found. Start AutoCAD and load the Acd.Mcp plugin, " +
                    "or pass --pid <PID> explicitly."),
                1 => found[0],
                _ => throw new InvalidOperationException(
                    $"Multiple AutoCAD instances found (PIDs: {string.Join(", ", found)}). " +
                    "Pass --pid <PID> to disambiguate."),
            };
        }
    }
}
