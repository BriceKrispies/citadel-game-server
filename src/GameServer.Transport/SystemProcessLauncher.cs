using System.Diagnostics;

namespace GameServer.Transport;

/// <summary>
/// The production <see cref="IChildProcessLauncher"/>: starts a real OS process from a
/// caller-supplied <see cref="ProcessStartInfo"/> factory (one fresh start-info per launch,
/// so a restart gets a clean process). Cooperative stop closes the child's stdin — a simple
/// cross-platform EOF convention a well-behaved child treats as "shut down"; the hard stop
/// kills the process tree.
/// </summary>
public sealed class SystemProcessLauncher : IChildProcessLauncher
{
    private readonly Func<ProcessStartInfo> _startInfoFactory;

    public SystemProcessLauncher(Func<ProcessStartInfo> startInfoFactory) => _startInfoFactory = startInfoFactory;

    public IChildProcessHandle Launch()
    {
        var process = Process.Start(_startInfoFactory())
            ?? throw new InvalidOperationException("Failed to start child worker process.");
        return new Handle(process);
    }

    private sealed class Handle : IChildProcessHandle
    {
        private readonly Process _process;

        public Handle(Process process)
        {
            _process = process;
            // Capture the id now: reading Process.Id after exit can throw.
            Id = process.Id;
        }

        public int Id { get; }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public void RequestStop()
        {
            try
            {
                if (_process.StartInfo.RedirectStandardInput && !_process.HasExited)
                {
                    _process.StandardInput.Close(); // EOF on stdin: the cooperative stop signal.
                }
            }
            catch (InvalidOperationException)
            {
                // Racing the child's own exit; the stop is best-effort.
            }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and the kill.
            }
        }
    }
}
