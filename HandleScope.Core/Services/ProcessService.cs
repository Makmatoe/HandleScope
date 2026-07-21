using System.Diagnostics;
using HandleScope.Models;

namespace HandleScope.Services;

public sealed class ProcessService
{
    public IReadOnlyList<ProcessRow> GetProcesses()
    {
        var rows = new List<ProcessRow>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    int? handleCount = null;
                    long? workingSet = null;

                    try
                    {
                        handleCount = process.HandleCount;
                    }
                    catch
                    {
                        // A protected or exiting process may deny individual properties.
                    }

                    try
                    {
                        workingSet = process.WorkingSet64;
                    }
                    catch
                    {
                        // Keep the process visible even when memory data is unavailable.
                    }

                    rows.Add(new ProcessRow(
                        process.Id,
                        process.ProcessName,
                        handleCount,
                        workingSet));
                }
                catch
                {
                    // Processes can exit while the snapshot is being built.
                }
            }
        }

        return rows
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProcessId)
            .ToArray();
    }
}
