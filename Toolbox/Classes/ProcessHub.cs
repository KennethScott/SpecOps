using Microsoft.AspNetCore.SignalR;
using System;
using System.Diagnostics;

namespace Toolbox.Classes
{
    public class ProcessHub : Hub
    {
        public void Stream(string command)
        {
            Stream(command, o =>
            {
                Clients.All.SendAsync("OutputRecieved", o);
            });
        }

        private static Process _process = null;
        private const string NEWLINE = "\n"; // NOTE: This should NOT be the environment newline because the enviornment we're talking to may be different than the one this is running in

        private void Stream(string command, Action<string> outputHandler)
        {
            TerminateProcessIfExists();
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo("cmd.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            };

            process.OutputDataReceived += new DataReceivedEventHandler(
              (sendingProcess, outLine) => outputHandler(outLine.Data)
            );

            process.ErrorDataReceived += new DataReceivedEventHandler(
              (sendingProcess, outLine) => outputHandler(outLine.Data)
            );

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Write(command + NEWLINE);
            process.WaitForExit();
            _process = process;
        }

        private void TerminateProcessIfExists()
        {
            if (_process != null)
            {
                try
                {
                    _process.CancelOutputRead();
                    _process.CancelErrorRead();
                    _process.Kill();

                }
                finally
                {
                    _process.Dispose();
                }
            }
        }
    }
}
